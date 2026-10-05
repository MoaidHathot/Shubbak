using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Shubbak.Ipc;

/// <summary>
/// Named-pipe client, used by the CLI, the bar, the palette and the watcher - and by
/// anything else that wants to talk to the window manager.
/// </summary>
/// <remarks>
/// <para>
/// One connection can both carry requests and stream a subscription. The server has
/// always allowed it - it reads the next request from a subscribed connection and
/// writes the reply between the events, whole lines each, never interleaved
/// mid-message - and it was this class that refused, because its subscription read
/// the stream directly and a request reading alongside it would have raced it for
/// lines. The subscription now reads through one loop that hands each line to
/// whoever it is for: an event to the queue <see cref="ReadEventsAsync"/> drains, a
/// reply to the request waiting for it. A provider that holds a context with a lease
/// no longer needs a second connection to hear that the file was reloaded, which was
/// the pipe's one rough edge for anything written against it.
/// </para>
/// <para>
/// Paid for only when used. A connection that has not subscribed reads each reply
/// in line with the request that asked for it, as it always did: no task, no read
/// pending on the pipe while nothing is asked. The loop starts when a subscription
/// is accepted, which is the moment a read has to be pending anyway.
/// </para>
/// </remarks>
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
    /// thing. A caller that has to remember to lock is a caller that will forget. Held
    /// across the reply as well as the request, on a subscribed connection too, so
    /// that a caller queued behind a slow request fails on its own deadline rather
    /// than inheriting an unbounded one, and so the server - which answers one request
    /// per connection at a time anyway - is never asked to hold two.
    /// </para>
    /// </remarks>
    private readonly SemaphoreSlim _turn = new(1, 1);

    /// <summary>
    /// The loop that reads a subscribed connection, or null until one is subscribed.
    /// </summary>
    /// <remarks>
    /// Set under <see cref="_turn"/> by the subscription's handshake, before the
    /// handshake lets go of the turn, so the next request to get the turn finds the
    /// loop reading and leaves the reply to it; read under the turn by every request.
    /// Nothing but the loop touches <see cref="_reader"/> from then on.
    /// </remarks>
    private Task? _reading;

    /// <summary>
    /// Where the reading loop puts events for <see cref="ReadEventsAsync"/>.
    /// </summary>
    /// <remarks>
    /// Bounded, and never waited on by the loop, because a reply may be behind the
    /// event the loop is holding and the request waiting for the reply may be the very
    /// thing the consumer is busy with - the bar asks the window manager things from
    /// inside its event handler. A loop that blocked on a full queue would then be
    /// waiting for a consumer that is waiting for it. So a queue the consumer has let
    /// fill is emptied and a <see cref="IpcProtocol.ResyncTopic"/> put in its place,
    /// which is what the server does to a client that has let its outbox fill, for
    /// the same reason, and is what the consumer already knows how to read.
    /// </remarks>
    private Channel<IpcEvent>? _events;

    /// <summary>
    /// The request waiting for its reply on a subscribed connection, or null.
    /// </summary>
    /// <remarks>
    /// One, not a table: <see cref="_turn"/> admits one request at a time. Set before
    /// the request is written and cleared when the reply is in hand or the wait is
    /// given up, so a reply that arrives for a request nobody waits for any more - one
    /// that timed out - is dropped by its id rather than handed to the next request.
    /// </remarks>
    private volatile PendingReply? _pending;

    private sealed class PendingReply(int id)
    {
        public int Id { get; } = id;

        // Completed from the reading loop; continuations run elsewhere, so a caller
        // with a long continuation - the bar, applying what it asked for - does not
        // run it on the loop and hold up the events behind it.
        public TaskCompletionSource<IpcResponse> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <inheritdoc cref="IpcServer.PipeName"/>
    public string PipeName { get; init; } = IpcProtocol.PipeName;

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
    public static bool IsServerRunning(string pipeName)
    {
        // Enumerated more than once before answering no. The pipe namespace is a live
        // directory, and an enumeration that overlaps another process creating or
        // closing pipes can skip an entry - observed as one miss in a few hundred
        // under a parallel test run with dozens of pipes churning. A false "not
        // running" is the worse error here: the command line prints that the window
        // manager is not there while it is, and a companion waits for a start that
        // has already happened. Three passes cost a millisecond or two, and only on
        // the way to no; a running server is found on the first.
        for (int attempt = 0; attempt < 3; attempt++)
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

    /// <summary>Whether this connection is streaming a subscription.</summary>
    public bool IsSubscribed => _reading is not null;

    /// <summary>Sends one request and waits for its reply.</summary>
    /// <remarks>
    /// <para>
    /// Bounded. The server can decline to reply at all - a payload that deserialises
    /// to JSON null takes that path - and every caller passed no token, so a single
    /// wedged request left the caller waiting for the life of the process rather than
    /// failing with something it could report.
    /// </para>
    /// <para>
    /// Safe to call from several threads, and on a subscribed connection. Callers are
    /// serialised onto the pipe one at a time; see <see cref="_turn"/> for what
    /// interleaving them did.
    /// </para>
    /// </remarks>
    public Task<IpcResponse> SendAsync(
        string method, string? payload = null, CancellationToken token = default) =>
        SendAsync(method, payload, beginStreaming: false, token);

    /// <summary>
    /// <see cref="SendAsync(string, string?, CancellationToken)"/>, with the one thing a
    /// subscription's handshake needs that an ordinary request must not have.
    /// </summary>
    /// <param name="method">What to do; see <see cref="IpcRequest.Method"/>.</param>
    /// <param name="payload">The method's argument.</param>
    /// <param name="beginStreaming">
    /// Whether a successful reply starts the reading loop. Decided here, under the
    /// turn, and not by the caller afterwards, so that a request queued behind the
    /// handshake finds the loop reading when its turn comes and leaves the reply to it
    /// rather than reading alongside.
    /// </param>
    /// <param name="token">Abandons the request.</param>
    private async Task<IpcResponse> SendAsync(
        string method, string? payload, bool beginStreaming, CancellationToken token)
    {
        if (_writer is null || _reader is null)
            throw new InvalidOperationException("Not connected.");

        using var timeout = new CancellationTokenSource(ResponseTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);

        // The wait is inside the timeout, so a caller queued behind a wedged request
        // fails on its own deadline rather than inheriting an unbounded one.
        await _turn.WaitAsync(linked.Token).ConfigureAwait(false);

        try
        {
            IpcResponse response;

            if (_reading is not null)
            {
                response = await ExchangeThroughLoopAsync(method, payload, linked.Token).ConfigureAwait(false);
            }
            else
            {
                // Checked under the turn, never before it: while a request is in flight
                // the flag is true of that request rather than of the connection, so a
                // caller reading it on the way in would refuse a connection that is
                // perfectly well.
                if (_broken)
                    throw new IOException("This connection was abandoned mid-message and cannot be reused.");

                response = await ExchangeAsync(method, payload, linked.Token).ConfigureAwait(false);

                _broken = false;

                if (beginStreaming && response.Ok) BeginReading();
            }

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
    /// <para>
    /// Only a connection that reads its own replies can be left so. On a subscribed
    /// one the loop owns the stream and reads every line whether or not anybody is
    /// still waiting for it, so a request that is given up on costs the caller its
    /// answer and the connection nothing.
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

        await WriteAsync(request, token).ConfigureAwait(false);

        while (true)
        {
            string? line = await _reader!.ReadLineAsync(token).ConfigureAwait(false);
            if (line is null) throw new IOException("The window manager closed the connection.");
            if (line.Length == 0) continue;

            // Nothing but replies arrives on a connection that has not subscribed, but
            // a line that is not ours - a reply to a request abandoned by an earlier
            // caller, say - is skipped rather than mistaken for ours.
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

    /// <summary>Writes one request and waits for the reading loop to hand over its reply.</summary>
    private async Task<IpcResponse> ExchangeThroughLoopAsync(string method, string? payload, CancellationToken token)
    {
        var request = new IpcRequest(method, payload, Interlocked.Increment(ref _nextId));
        var pending = new PendingReply(request.Id);

        // Registered before the write, so the reply cannot arrive before anybody is
        // waiting for it.
        _pending = pending;

        try
        {
            await WriteAsync(request, token).ConfigureAwait(false);

            return await pending.Reply.Task.WaitAsync(token).ConfigureAwait(false);
        }
        finally
        {
            _pending = null;
        }
    }

    private Task WriteAsync(IpcRequest request, CancellationToken token) =>
        _writer!.WriteLineAsync(
            JsonSerializer.Serialize(request, IpcJsonContext.Default.IpcRequest).AsMemory(), token);

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
    /// The handshake runs inside the first <c>MoveNextAsync</c>; a caller that needs
    /// to know the subscription is in place before doing something else - taking a
    /// snapshot that the events then keep current - uses
    /// <see cref="BeginSubscriptionAsync"/> and <see cref="ReadEventsAsync"/>
    /// separately. Requests may be sent on the connection throughout; see
    /// <see cref="SendAsync(string, string?, CancellationToken)"/>.
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
    /// happened. Asked again on a subscribed connection, it adds topics; the server
    /// keeps the set.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The window manager refused the subscription.</exception>
    public async Task BeginSubscriptionAsync(string? topics, CancellationToken token = default)
    {
        if (_reader is null) throw new InvalidOperationException("Not connected.");

        // Checked, because the server can refuse - an unknown topic can never fire, so
        // accepting the refusal quietly leaves the caller waiting for something that
        // was never going to arrive. A reply that is accepted starts the reading loop
        // before the handshake lets go of the turn; see SendAsync.
        IpcResponse response = await SendAsync("subscribe", topics ?? "*", beginStreaming: true, token).ConfigureAwait(false);

        if (!response.Ok)
            throw new InvalidOperationException(response.Error ?? "the subscription was refused.");
    }

    /// <summary>Yields the events of a subscription begun with <see cref="BeginSubscriptionAsync"/>.</summary>
    /// <remarks>
    /// Ends when the window manager closes the connection or this client is disposed.
    /// A consumer that falls <see cref="MaxQueuedEvents"/> events behind is handed a
    /// <see cref="IpcProtocol.ResyncTopic"/> in place of the backlog, as the server
    /// hands one to a connection it could not keep up with; see <see cref="_events"/>.
    /// </remarks>
    public async IAsyncEnumerable<IpcEvent> ReadEventsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token = default)
    {
        if (_reader is null) throw new InvalidOperationException("Not connected.");
        if (_events is not { } events) throw new InvalidOperationException("Not subscribed; call BeginSubscriptionAsync first.");

        while (await events.Reader.WaitToReadAsync(token).ConfigureAwait(false))
        {
            while (events.Reader.TryRead(out IpcEvent? notification))
                yield return notification;
        }
    }

    /// <summary>How many events may wait for the consumer before the backlog is dropped. The server's figure.</summary>
    private const int MaxQueuedEvents = 512;

    /// <summary>Starts the loop that reads a subscribed connection. Under <see cref="_turn"/>.</summary>
    private void BeginReading()
    {
        if (_reading is not null) return;

        _events = Channel.CreateBounded<IpcEvent>(new BoundedChannelOptions(MaxQueuedEvents)
        {
            SingleWriter = true,
            SingleReader = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

        // Not Task.Run: the loop runs here until its first read has to wait, which
        // drains whatever the handshake's read left buffered - events that arrived on
        // the heels of the reply - without a hop through the pool. It is still under
        // the turn, and nothing it does there can want the turn.
        _reading = ReadLoopAsync(_events);
    }

    /// <summary>
    /// Reads every line of a subscribed connection and hands it to whoever it is for.
    /// </summary>
    /// <remarks>
    /// Never throws; the stream ending, however it ends, completes the event queue -
    /// which ends <see cref="ReadEventsAsync"/> - and fails the request waiting for a
    /// reply, which is where each is awaited and the right place for each to hear it.
    /// </remarks>
    private async Task ReadLoopAsync(Channel<IpcEvent> events)
    {
        Exception? ended = null;

        try
        {
            while (true)
            {
                string? line = await _reader!.ReadLineAsync().ConfigureAwait(false);
                if (line is null) break;
                if (line.Length == 0) continue;

                Deliver(line, events);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            // The pipe was closed under the read - by the server leaving, or by
            // DisposeAsync - which is the end of the stream by another name.
            ended = ex;
        }
        finally
        {
            events.Writer.TryComplete();

            // A request waiting on this connection would otherwise wait out its
            // timeout to learn what the loop already knows.
            _pending?.Reply.TrySetException(
                ended as IOException ?? new IOException("The window manager closed the connection.", ended));
        }
    }

    /// <summary>One line, to the queue if it is an event and to the waiting request if it is its reply.</summary>
    private void Deliver(string line, Channel<IpcEvent> events)
    {
        IpcEvent? notification;
        IpcResponse? response;

        try
        {
            // An event first, because on a subscribed connection nearly every line is
            // one. Either shape reads as the other with its fields missing - the
            // serialiser ignores what it does not know - so the topic is the test, and
            // then the id.
            notification = JsonSerializer.Deserialize(line, IpcJsonContext.Default.IpcEvent);

            if (notification is { Topic.Length: > 0 })
            {
                Enqueue(notification, events);
                return;
            }

            response = JsonSerializer.Deserialize(line, IpcJsonContext.Default.IpcResponse);
        }
        catch (JsonException)
        {
            return;
        }

        // Dropped unless it is the reply the one waiting request asked for: a reply to
        // a request that timed out has nobody to go to, and the server's answer to a
        // line it could not read carries id zero and is nobody's.
        if (response is not null && _pending is { } pending && pending.Id == response.Id)
            pending.Reply.TrySetResult(response);
    }

    /// <summary>Queues an event for the consumer, or tells it the backlog is gone.</summary>
    private static void Enqueue(IpcEvent notification, Channel<IpcEvent> events)
    {
        if (events.Writer.TryWrite(notification)) return;

        // Full: the consumer has not read in a while. The backlog goes, a resync takes
        // its place so the consumer re-reads its picture, and this event follows, so
        // nothing after the resync is missing. Only the loop writes, so the drain and
        // the two writes cannot interleave with another writer; a consumer reading in
        // the meantime only makes room.
        while (events.Reader.TryRead(out _))
        {
        }

        events.Writer.TryWrite(new IpcEvent(IpcProtocol.ResyncTopic, "{}"));
        events.Writer.TryWrite(notification);
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
    /// awaited, which is the right place for them. The reading loop, if there is one,
    /// ends with its read and is waited for, so nothing of this client runs after this
    /// returns.
    /// </para>
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (_pipe is not null) await _pipe.DisposeAsync().ConfigureAwait(false);

        if (_reading is { } reading) await reading.ConfigureAwait(false);

        _turn.Dispose();
    }
}
