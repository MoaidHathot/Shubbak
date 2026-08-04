using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Shubbak.Ipc;

/// <summary>
/// Named-pipe server for CLI and bar clients.
/// </summary>
/// <remarks>
/// <para>
/// A named pipe rather than a TCP socket: it is faster for local traffic, it needs
/// no port, and Windows secures it with an ACL so another user on the same machine
/// cannot drive your window manager.
/// </para>
/// <para>
/// Each client gets its own connection and its own reader task. Requests are
/// handed to the caller through <see cref="RequestHandler"/>, which the daemon
/// marshals onto its single thread - the tree must never be touched from a pipe
/// thread, or an arrange pass could observe a half-mutated tree.
/// </para>
/// </remarks>
public sealed class IpcServer : IAsyncDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<ClientConnection> _clients = [];
    private readonly Lock _gate = new();

    private Task? _acceptLoop;
    private bool _disposed;

    /// <summary>
    /// Reports something worth knowing, without this assembly knowing how to log.
    /// </summary>
    /// <remarks>
    /// This layer deliberately has no dependencies, so it cannot reach the logger -
    /// but refusing a connection or a subscription in silence is exactly the failure
    /// mode being fixed elsewhere. The host wires this to its own logging.
    /// </remarks>
    public Action<string>? Warn { get; set; }

    /// <summary>
    /// Handles one request, returning the response.
    /// </summary>
    /// <remarks>
    /// Invoked from a pipe thread. Implementations must marshal any work that
    /// touches window manager state onto the daemon thread.
    /// </remarks>
    public delegate Task<IpcResponse> RequestHandler(IpcRequest request);

    private RequestHandler? _handler;

    /// <summary>Starts listening.</summary>
    public void Start(RequestHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ObjectDisposedException.ThrowIf(_disposed, this);

        _handler = handler;

        // Several listeners run concurrently. With only one, there is a window
        // between accepting a client and creating the next instance during which
        // nothing is listening - so back-to-back CLI invocations fail
        // intermittently, which is maddening to diagnose because it is timing
        // dependent and never reproduces under a debugger.
        _acceptLoop = Task.WhenAll(
            Enumerable.Range(0, ListenerCount).Select(_ => Task.Run(AcceptLoopAsync)));
    }

    /// <summary>
    /// How many pipe instances listen at once.
    /// </summary>
    /// <remarks>
    /// Four is ample: the CLI is one short-lived client at a time, and the bar holds
    /// one long-lived connection per monitor.
    /// </remarks>
    private const int ListenerCount = 4;

    /// <summary>How many clients are connected.</summary>
    public int ClientCount
    {
        get { lock (_gate) return _clients.Count; }
    }

    /// <summary>
    /// Pushes an event to every client subscribed to its topic.
    /// </summary>
    /// <remarks>
    /// Fire-and-forget. A bar that stops reading must never be able to block the
    /// window manager, so a client whose buffer has filled is disconnected rather
    /// than waited on.
    /// </remarks>
    public void Publish(string topic, string json)
    {
        ClientConnection[] clients;
        lock (_gate) clients = [.. _clients];

        if (clients.Length == 0) return;

        string message = JsonSerializer.Serialize(
            new IpcEvent(topic, json), IpcJsonContext.Default.IpcEvent);

        foreach (ClientConnection client in clients)
            if (client.IsSubscribed(topic)) client.TryEnqueue(message);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;

            try
            {
                pipe = new NamedPipeServerStream(
                    IpcProtocol.PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await pipe.WaitForConnectionAsync(_shutdown.Token).ConfigureAwait(false);

                // Bounded. Every connected client costs a lock taken on the daemon
                // thread for every event published, so an unbounded set is a way for
                // one runaway process to slow the window manager down for everybody.
                bool room;
                lock (_gate) room = _clients.Count < IpcProtocol.MaxClients;

                if (!room)
                {
                    Warn?.Invoke(
                        $"refusing a connection: already serving {IpcProtocol.MaxClients} clients");

                    pipe.Dispose();
                    pipe = null;
                    continue;
                }

                var client = new ClientConnection(pipe, this);

                lock (_gate) _clients.Add(client);

                _ = client.RunAsync(_shutdown.Token);
                pipe = null;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                // A client that vanished mid-handshake is entirely routine.
            }
            finally
            {
                pipe?.Dispose();
            }
        }
    }

    private void Remove(ClientConnection client)
    {
        lock (_gate) _clients.Remove(client);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _shutdown.CancelAsync().ConfigureAwait(false);

        ClientConnection[] clients;
        lock (_gate)
        {
            clients = [.. _clients];
            _clients.Clear();
        }

        foreach (ClientConnection client in clients) client.Dispose();

        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        _shutdown.Dispose();
    }

    /// <summary>One connected client.</summary>
    private sealed class ClientConnection : IDisposable
    {
        private readonly NamedPipeServerStream _pipe;
        private readonly IpcServer _server;
        private readonly HashSet<string> _subscriptions = new(StringComparer.Ordinal);
        private readonly Queue<string> _outbox = new();
        private readonly Lock _gate = new();

        private bool _subscribedToAll;
        private bool _disposed;

        public ClientConnection(NamedPipeServerStream pipe, IpcServer server)
        {
            _pipe = pipe;
            _server = server;
        }

        /// <summary>
        /// Whether this client has fallen behind and needs to re-read the world.
        /// </summary>
        /// <remarks>
        /// Set when the outbox overflows, cleared once the notice has been sent.
        /// </remarks>
        private bool _needsResync;

        public bool IsSubscribed(string topic)
        {
            lock (_gate) return _subscribedToAll || _subscriptions.Contains(topic);
        }

        public void TryEnqueue(string message)
        {
            lock (_gate)
            {
                // Bounded, because the window manager must never be held hostage by a
                // stalled bar.
                //
                // Dropping the backlog was already right; doing it silently was not. A
                // client mirroring state has no way to notice that events went missing,
                // so it carries on displaying whatever it last heard about, wrong and
                // confident, until something unrelated happens to correct it. The
                // resync notice below turns that into something self-healing: the
                // client is told its picture is stale and re-reads it.
                if (_outbox.Count >= MaxQueuedEvents)
                {
                    _outbox.Clear();
                    _needsResync = true;
                    _pending.Release();
                    return;
                }

                _outbox.Enqueue(message);
            }

            _pending.Release();
        }

        /// <summary>How many pushed events may wait before the backlog is dropped.</summary>
        private const int MaxQueuedEvents = 512;

        /// <summary>Released whenever there is something to flush.</summary>
        /// <remarks>
        /// The loop used to race the reader against a sixteen-millisecond timer, so
        /// every connected client woke sixty times a second forever on an idle desktop
        /// and the losing timer was abandoned rather than cancelled. Waiting on a
        /// signal costs nothing until there is something to send.
        /// </remarks>
        private readonly SemaphoreSlim _pending = new(0);

        public async Task RunAsync(CancellationToken token)
        {
            try
            {
                using var reader = new StreamReader(_pipe, Encoding.UTF8, leaveOpen: true);
                await using var writer = new StreamWriter(_pipe, new UTF8Encoding(false), leaveOpen: true)
                {
                    AutoFlush = false,
                };

                Task<string?> readTask = ReadBoundedLineAsync(reader, token);
                Task pendingTask = _pending.WaitAsync(token);

                while (!token.IsCancellationRequested && _pipe.IsConnected)
                {
                    // Whichever comes first: a request to answer, or something to push.
                    // Neither costs anything while waiting.
                    Task completed = await Task.WhenAny(readTask, pendingTask).ConfigureAwait(false);

                    if (completed == readTask)
                    {
                        string? line = await readTask.ConfigureAwait(false);
                        if (line is null) break;

                        await HandleLineAsync(line, writer).ConfigureAwait(false);
                        readTask = ReadBoundedLineAsync(reader, token);
                    }
                    else
                    {
                        // Recreated only when it actually fired, so a signal arriving
                        // while a request was being handled is not swallowed.
                        pendingTask = _pending.WaitAsync(token);
                    }

                    await FlushOutboxAsync(writer).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
                // Client disconnected; entirely normal.
            }
            finally
            {
                _server.Remove(this);
                Dispose();
            }
        }

        /// <summary>
        /// Reads one message, refusing one that never ends.
        /// </summary>
        /// <remarks>
        /// A plain ReadLine waits for a newline that a hostile or broken client need
        /// never send, growing its buffer until the window manager runs out of memory.
        /// Returning null past the limit closes the connection, which is the only
        /// answer available: the stream is no longer at a message boundary, so there
        /// is nothing to resynchronise to.
        /// </remarks>
        private static async Task<string?> ReadBoundedLineAsync(StreamReader reader, CancellationToken token)
        {
            var builder = new StringBuilder();
            var buffer = new char[1];

            while (true)
            {
                int read = await reader.ReadAsync(buffer.AsMemory(0, 1), token).ConfigureAwait(false);

                if (read == 0) return builder.Length > 0 ? builder.ToString() : null;

                char c = buffer[0];

                if (c == IpcProtocol.MessageTerminator) return builder.ToString();
                if (c == '\r') continue;

                if (builder.Length >= IpcProtocol.MaxMessageBytes) return null;

                builder.Append(c);
            }
        }

        private async Task HandleLineAsync(string line, StreamWriter writer)
        {
            if (line.Length == 0) return;

            IpcRequest? request;

            try
            {
                request = JsonSerializer.Deserialize(line, IpcJsonContext.Default.IpcRequest);
            }
            catch (JsonException ex)
            {
                await WriteAsync(writer, new IpcResponse(0, false, null, $"malformed request: {ex.Message}"))
                    .ConfigureAwait(false);
                return;
            }

            if (request is null) return;

            if (string.Equals(request.Method, "subscribe", StringComparison.Ordinal))
            {
                string? rejected = Subscribe(request.Payload);

                await WriteAsync(writer, rejected is null
                    ? new IpcResponse(request.Id, true)
                    : new IpcResponse(request.Id, false, null, rejected)).ConfigureAwait(false);

                return;
            }

            IpcResponse response = _server._handler is { } handler
                ? await handler(request).ConfigureAwait(false)
                : new IpcResponse(request.Id, false, null, "server is not ready");

            await WriteAsync(writer, response).ConfigureAwait(false);
        }

        /// <summary>Records a subscription, or explains why it was refused.</summary>
        /// <returns>Null when accepted, otherwise the reason.</returns>
        private string? Subscribe(string? topics)
        {
            if (string.IsNullOrWhiteSpace(topics) || topics == "*")
            {
                lock (_gate) _subscribedToAll = true;
                return null;
            }

            string[] requested = topics.Split(
                ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            // Checked rather than accepted. A topic that does not exist can never fire,
            // so telling the client it worked leaves them waiting for something that
            // was never going to arrive - and a misspelling is the likeliest cause.
            string[] unknown = [.. requested.Where(t => !IpcProtocol.Topics.Contains(t))];

            if (unknown.Length > 0)
            {
                return $"unknown topic(s): {string.Join(", ", unknown)}. " +
                       $"Known topics: {string.Join(", ", IpcProtocol.Topics.Order())}.";
            }

            lock (_gate)
            {
                if (_subscriptions.Count + requested.Length > IpcProtocol.MaxSubscriptionsPerClient)
                    return $"too many subscriptions; the limit is {IpcProtocol.MaxSubscriptionsPerClient}.";

                foreach (string topic in requested) _subscriptions.Add(topic);
            }

            return null;
        }

        private async Task FlushOutboxAsync(StreamWriter writer)
        {
            string[] pending;
            bool resync;

            lock (_gate)
            {
                resync = _needsResync;
                _needsResync = false;

                if (!resync && _outbox.Count == 0) return;

                pending = [.. _outbox];
                _outbox.Clear();
            }

            foreach (string message in pending)
                await writer.WriteLineAsync(message).ConfigureAwait(false);

            // Sent after whatever survived, so a client that acts on it re-reads a
            // world at least as new as the events it just processed.
            if (resync)
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(
                    new IpcEvent(IpcProtocol.ResyncTopic, "{}"),
                    IpcJsonContext.Default.IpcEvent)).ConfigureAwait(false);
            }

            await writer.FlushAsync().ConfigureAwait(false);
        }

        private static async Task WriteAsync(StreamWriter writer, IpcResponse response)
        {
            await writer.WriteLineAsync(
                JsonSerializer.Serialize(response, IpcJsonContext.Default.IpcResponse))
                .ConfigureAwait(false);

            await writer.FlushAsync().ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _pipe.Dispose(); } catch (IOException) { }
        }
    }
}
