using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Shubbak.Ipc;

/// <summary>
/// Named-pipe client, used by the CLI and by Taj.
/// </summary>
public sealed class IpcClient : IAsyncDisposable
{
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private int _nextId;

    /// <summary>
    /// Held for a whole request-and-reply, so only one is ever on the wire.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One connection, two threads. Taj refreshes its state from the pump thread on
    /// every workspace, focus and layout event, and sends a command from the
    /// message-loop thread whenever the user clicks a widget - and clicking a
    /// workspace is precisely what produces the event that triggers the refresh, so
    /// the two overlap by design rather than by chance.
    /// </para>
    /// <para>
    /// The failure was not a garbled string. <see cref="StreamWriter"/> and
    /// <see cref="StreamReader"/> refuse a second async operation while one is pending
    /// and throw <see cref="InvalidOperationException"/>, which nothing on either path
    /// caught, so a click landing during a refresh killed the bar's pump outright: the
    /// workspace list froze, every later click became a silent no-op, and the clock and
    /// keyboard language kept ticking because they are local timers that never touch
    /// this pipe. Nothing was logged, because the pump only ever logged the two
    /// exceptions it expected.
    /// </para>
    /// <para>
    /// Serialising here rather than at the callers because the pipe is the shared
    /// thing. A caller that has to remember to lock is a caller that will forget.
    /// </para>
    /// </remarks>
    private readonly SemaphoreSlim _turn = new(1, 1);

    /// <summary>
    /// Set once <see cref="SubscribeAsync"/> owns the reader.
    /// </summary>
    /// <remarks>
    /// A subscription reads the stream directly and forever, which
    /// <see cref="_turn"/> cannot cover without blocking every request for the life of
    /// the subscription. Sending on a subscribed connection is therefore refused
    /// outright: it is a mistake in the caller, and a loud one is worth more than a
    /// corrupted stream. Set while the handshake still holds the turn, so that the
    /// refusal also reaches a request that was already queued behind the handshake.
    /// </remarks>
    private bool _streaming;

    /// <inheritdoc cref="IpcServer.PipeName"/>
    internal string PipeName { get; init; } = IpcProtocol.PipeName;

    /// <summary>Whether a window manager is listening.</summary>
    /// <remarks>
    /// Enumerates the pipe namespace rather than calling <c>File.Exists</c> on the
    /// pipe path. <c>File.Exists</c> on <c>\\.\pipe\name</c> is unreliable: it
    /// reports false while the server is between accepting one client and creating
    /// the next listening instance, which makes back-to-back CLI invocations fail
    /// intermittently.
    /// </remarks>
    public static bool IsServerRunning() => IsServerRunning(IpcProtocol.PipeName);

    /// <inheritdoc cref="IsServerRunning()"/>
    internal static bool IsServerRunning(string pipeName)
    {
        try
        {
            foreach (string pipe in Directory.EnumerateFiles(@"\\.\pipe\"))
            {
                if (string.Equals(
                        Path.GetFileName(pipe), pipeName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Fall through to the cruder check rather than reporting "not running"
            // for what is really an enumeration failure.
            return File.Exists($@"\\.\pipe\{pipeName}");
        }

        return false;
    }

    /// <summary>Connects, or throws if no window manager is running.</summary>
    public async Task ConnectAsync(TimeSpan timeout, CancellationToken token = default)
    {
        _pipe = new NamedPipeClientStream(
            ".", PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        await _pipe.ConnectAsync((int)timeout.TotalMilliseconds, token).ConfigureAwait(false);

        _reader = new StreamReader(_pipe, Encoding.UTF8, leaveOpen: true);
        _writer = new StreamWriter(_pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
    }

    /// <summary>Sends one request and waits for its reply.</summary>
    /// <remarks>
    /// <para>
    /// Bounded. The server can decline to reply at all - a payload that deserialises
    /// to JSON null takes that path - and every caller passed no token, so a single
    /// wedged request left the caller waiting for the life of the process rather than
    /// failing with something it could report.
    /// </para>
    /// <para>
    /// Safe to call from several threads. Callers are serialised onto the pipe one at
    /// a time; see <see cref="_turn"/> for what interleaving them did.
    /// </para>
    /// </remarks>
    public Task<IpcResponse> SendAsync(
        string method, string? payload = null, CancellationToken token = default) =>
        SendAsync(method, payload, claimStream: false, token);

    /// <summary>
    /// <see cref="SendAsync(string, string?, CancellationToken)"/>, with the one thing a
    /// subscription's handshake needs that an ordinary request must not have.
    /// </summary>
    /// <param name="method">What to do; see <see cref="IpcRequest.Method"/>.</param>
    /// <param name="payload">The method's argument.</param>
    /// <param name="claimStream">
    /// Whether a successful reply takes the connection over for a subscription. Decided
    /// here, under the turn, and not by the caller afterwards: see the second check of
    /// <see cref="_streaming"/> below for why the moment matters.
    /// </param>
    /// <param name="token">Abandons the request.</param>
    private async Task<IpcResponse> SendAsync(
        string method, string? payload, bool claimStream, CancellationToken token)
    {
        if (_writer is null || _reader is null)
            throw new InvalidOperationException("Not connected.");

        if (_streaming) throw StreamingRefusal();

        using var timeout = new CancellationTokenSource(ResponseTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);

        // The wait is inside the timeout, so a caller queued behind a wedged request
        // fails on its own deadline rather than inheriting an unbounded one.
        await _turn.WaitAsync(linked.Token).ConfigureAwait(false);

        try
        {
            // Checked again, under the turn. The check on the way in is a courtesy; this
            // one is the guarantee. A subscription's handshake is itself a request, and it
            // claims the stream before letting go of the turn - so a request that queued
            // behind the handshake finds the connection spoken for when its turn comes.
            // Without this it was let onto a stream the subscription had already started
            // reading, and the two raced each other for lines.
            if (_streaming) throw StreamingRefusal();

            // Checked under the turn, never before it: while a request is in flight the
            // flag is true of that request rather than of the connection, so a caller
            // reading it on the way in would refuse a connection that is perfectly well.
            if (_broken)
                throw new IOException("This connection was abandoned mid-message and cannot be reused.");

            IpcResponse response = await ExchangeAsync(method, payload, linked.Token).ConfigureAwait(false);

            _broken = false;

            if (claimStream && response.Ok) _streaming = true;

            return response;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !token.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The window manager did not answer '{method}' within {ResponseTimeout.TotalSeconds:F0} s.");
        }
        finally
        {
            _turn.Release();
        }
    }

    private static InvalidOperationException StreamingRefusal() => new(
        "This connection is streaming a subscription and cannot carry requests. " +
        "Open a second connection for them.");

    /// <summary>
    /// Set when an exchange did not finish, so the stream is no longer at a boundary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A request that is cancelled or times out has already been written. The reply is
    /// still coming, and the framing is byte-oriented with nothing to resynchronise to,
    /// so the next caller on this connection would read the previous caller's answer
    /// and every later one would be off by a message. Cancelling the read also leaves
    /// the underlying overlapped I/O in a state the reader will not start a second
    /// operation against - which is where "the stream is currently in use by a previous
    /// operation" comes from, long after the operation that abandoned it.
    /// </para>
    /// <para>
    /// Refusing is the honest answer. Callers already reconnect on
    /// <see cref="IOException"/>, which is the only sound thing to do with a stream
    /// whose position is unknown.
    /// </para>
    /// </remarks>
    private bool _broken;

    /// <summary>Writes one request and reads until its reply comes back.</summary>
    private async Task<IpcResponse> ExchangeAsync(string method, string? payload, CancellationToken token)
    {
        // Allocated under the turn, so the ids on the wire are in the order they were
        // written. It was a plain `_nextId++` - a non-atomic read-modify-write - so two
        // threads could be handed the same id and each match on the other's reply.
        var request = new IpcRequest(method, payload, Interlocked.Increment(ref _nextId));

        // Set before the write rather than in a catch, because the ways this can be
        // abandoned include ones that unwind without an exception this method sees.
        _broken = true;

        await _writer!.WriteLineAsync(
            JsonSerializer.Serialize(request, IpcJsonContext.Default.IpcRequest).AsMemory(), token)
            .ConfigureAwait(false);

        while (true)
        {
            string? line = await _reader!.ReadLineAsync(token).ConfigureAwait(false);
            if (line is null) throw new IOException("The window manager closed the connection.");
            if (line.Length == 0) continue;

            // Events can interleave with responses on a subscribed connection, so
            // anything that is not our response is skipped.
            IpcResponse? response;
            try
            {
                response = JsonSerializer.Deserialize(line, IpcJsonContext.Default.IpcResponse);
            }
            catch (JsonException)
            {
                continue;
            }

            if (response is not null && response.Id == request.Id) return response;
        }
    }

    /// <summary>
    /// How long to wait for a reply.
    /// </summary>
    /// <remarks>
    /// Generous, because a request is answered on the daemon thread and that thread
    /// may legitimately be busy adopting windows at startup. Long enough never to fire
    /// in normal use, short enough that a wedged daemon is reported rather than waited
    /// on forever.
    /// </remarks>
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Subscribes and yields events until cancelled.</summary>
    /// <param name="topics">Comma-separated topics, or null for everything.</param>
    /// <param name="token">Stops the stream.</param>
    /// <remarks>
    /// Takes the connection over. Once subscribed it owns the reader for good, so
    /// <see cref="SendAsync(string, string?, CancellationToken)"/> on the same client is
    /// refused rather than allowed to race the event loop for lines. The handshake runs
    /// inside the first <c>MoveNextAsync</c>; a caller that needs to know the
    /// subscription is in place before doing something else - taking a snapshot that
    /// the events then keep current - uses <see cref="BeginSubscriptionAsync"/> and
    /// <see cref="ReadEventsAsync"/> separately.
    /// </remarks>
    public async IAsyncEnumerable<IpcEvent> SubscribeAsync(
        string? topics,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token = default)
    {
        await BeginSubscriptionAsync(topics, token).ConfigureAwait(false);

        await foreach (IpcEvent notification in ReadEventsAsync(token).ConfigureAwait(false))
            yield return notification;
    }

    /// <summary>
    /// Asks for a subscription and returns once the window manager has accepted it,
    /// from which moment every event on the topics is queued for this connection.
    /// </summary>
    /// <remarks>
    /// The half of <see cref="SubscribeAsync"/> that decides whether events will
    /// arrive, separated so a client can subscribe first and read its snapshot second.
    /// The other order - snapshot, then subscribe - has a gap between the two in which
    /// a focus change or a workspace switch is neither in the snapshot nor in the
    /// stream, and the bar showed the wrong workspace until something unrelated
    /// happened.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The window manager refused the subscription.</exception>
    public async Task BeginSubscriptionAsync(string? topics, CancellationToken token = default)
    {
        if (_reader is null) throw new InvalidOperationException("Not connected.");

        // Checked, because the server can refuse - an unknown topic can never fire, so
        // accepting the refusal quietly leaves the caller waiting for something that
        // was never going to arrive. A reply that is accepted takes the connection over
        // before the handshake lets go of the turn; see SendAsync.
        IpcResponse response = await SendAsync("subscribe", topics ?? "*", claimStream: true, token).ConfigureAwait(false);

        if (!response.Ok)
            throw new InvalidOperationException(response.Error ?? "the subscription was refused.");
    }

    /// <summary>Yields the events of a subscription begun with <see cref="BeginSubscriptionAsync"/>.</summary>
    public async IAsyncEnumerable<IpcEvent> ReadEventsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token = default)
    {
        if (_reader is null) throw new InvalidOperationException("Not connected.");
        if (!_streaming) throw new InvalidOperationException("Not subscribed; call BeginSubscriptionAsync first.");

        while (!token.IsCancellationRequested)
        {
            string? line = await _reader.ReadLineAsync(token).ConfigureAwait(false);
            if (line is null) yield break;
            if (line.Length == 0) continue;

            IpcEvent? notification = null;

            try
            {
                notification = JsonSerializer.Deserialize(line, IpcJsonContext.Default.IpcEvent);
            }
            catch (JsonException)
            {
            }

            if (notification is { Topic.Length: > 0 }) yield return notification;
        }
    }

    /// <summary>Closes the connection. Does not throw for the state the connection is in.</summary>
    /// <remarks>
    /// <para>
    /// The pipe is disposed; the reader and writer over it are not, and that is the
    /// point of this method. Neither owns the pipe and neither holds anything - the
    /// writer flushes on every write, so its buffer is empty the moment a call returns.
    /// All that disposing them does is flush once more, and a flush asks the pipe how it
    /// is. Broken, because a request failed after the window manager left - a write to a
    /// closed pipe is what marks it so; a read that meets the closed end merely reports
    /// the end of the stream - is an <see cref="IOException"/>. A write still being
    /// accounted for is an <see cref="InvalidOperationException"/>: the bytes are long
    /// since in the pipe and the server has acted on them, but the task that wrote them
    /// has not run its last continuation, and the writer refuses a second operation
    /// until it has. Under load that is exactly the moment a caller that has seen the
    /// server respond decides it is done.
    /// </para>
    /// <para>
    /// Each of those was an exception out of <c>DisposeAsync</c>, at the one moment a
    /// caller has nothing left to do with one, and every caller had grown its own catch
    /// around this to say so - a different list of exception types in each. There is
    /// nothing to be saved by asking, so it is not asked.
    /// </para>
    /// <para>
    /// Closing the pipe is what ends whatever was still going on: a subscription's
    /// pending read, a request whose reply had not come. Those fail with an
    /// <see cref="IOException"/> or <see cref="ObjectDisposedException"/> where they were
    /// awaited, which is the right place for them.
    /// </para>
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (_pipe is not null) await _pipe.DisposeAsync().ConfigureAwait(false);

        _turn.Dispose();
    }
}
