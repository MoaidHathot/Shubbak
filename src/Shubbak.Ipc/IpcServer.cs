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

        public bool IsSubscribed(string topic)
        {
            lock (_gate) return _subscribedToAll || _subscriptions.Contains(topic);
        }

        public void TryEnqueue(string message)
        {
            lock (_gate)
            {
                // Bounded: a client that has stopped reading is dropped rather than
                // allowed to grow the queue without limit. The window manager must
                // never be held hostage by a stalled bar.
                if (_outbox.Count >= 512)
                {
                    _outbox.Clear();
                    return;
                }

                _outbox.Enqueue(message);
            }
        }

        public async Task RunAsync(CancellationToken token)
        {
            try
            {
                using var reader = new StreamReader(_pipe, Encoding.UTF8, leaveOpen: true);
                await using var writer = new StreamWriter(_pipe, new UTF8Encoding(false), leaveOpen: true)
                {
                    AutoFlush = false,
                };

                Task<string?> readTask = reader.ReadLineAsync(token).AsTask();

                while (!token.IsCancellationRequested && _pipe.IsConnected)
                {
                    // Interleave reading requests with flushing queued events, so a
                    // subscriber receives pushes without having to send anything.
                    Task completed = await Task.WhenAny(readTask, Task.Delay(16, token))
                        .ConfigureAwait(false);

                    if (completed == readTask)
                    {
                        string? line = await readTask.ConfigureAwait(false);
                        if (line is null) break;

                        await HandleLineAsync(line, writer).ConfigureAwait(false);
                        readTask = reader.ReadLineAsync(token).AsTask();
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
                Subscribe(request.Payload);
                await WriteAsync(writer, new IpcResponse(request.Id, true)).ConfigureAwait(false);
                return;
            }

            IpcResponse response = _server._handler is { } handler
                ? await handler(request).ConfigureAwait(false)
                : new IpcResponse(request.Id, false, null, "server is not ready");

            await WriteAsync(writer, response).ConfigureAwait(false);
        }

        private void Subscribe(string? topics)
        {
            lock (_gate)
            {
                if (string.IsNullOrWhiteSpace(topics) || topics == "*")
                {
                    _subscribedToAll = true;
                    return;
                }

                foreach (string topic in topics.Split(',', StringSplitOptions.RemoveEmptyEntries |
                                                          StringSplitOptions.TrimEntries))
                {
                    _subscriptions.Add(topic);
                }
            }
        }

        private async Task FlushOutboxAsync(StreamWriter writer)
        {
            string[] pending;

            lock (_gate)
            {
                if (_outbox.Count == 0) return;
                pending = [.. _outbox];
                _outbox.Clear();
            }

            foreach (string message in pending)
                await writer.WriteLineAsync(message).ConfigureAwait(false);

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
