using System.Collections.Concurrent;
using Shubbak.Ipc;

namespace Shubbak.Ipc.Tests;

/// <summary>
/// One connection used by two threads at once, and by a subscription and requests at
/// once.
/// </summary>
/// <remarks>
/// <para>
/// Taj holds a single <see cref="IpcClient"/> as its command channel and uses it from
/// two threads that never coordinate. The pump thread re-queries the whole state on
/// every workspace, focus and layout event; the message-loop thread sends a command
/// whenever the user clicks a widget. Nothing stopped the two overlapping.
/// </para>
/// <para>
/// The failure is not a garbled string. <see cref="StreamWriter"/> and
/// <see cref="StreamReader"/> refuse a second async operation while one is pending and
/// throw <see cref="InvalidOperationException"/>, which neither caller catches - so a
/// click that lands during a refresh kills the pump task outright. The bar keeps
/// drawing, the clock and the keyboard language keep ticking because they are local
/// timers, and the workspace list never changes again.
/// </para>
/// <para>
/// Even without the throw the ids cross: <c>_nextId++</c> is a non-atomic
/// read-modify-write, and a reply that does not match is discarded rather than handed
/// to the caller it belongs to, leaving that caller waiting out the ten-second
/// timeout for an answer that has already been thrown away.
/// </para>
/// <para>
/// A subscribed connection used to refuse requests, for the same reason: its reader
/// read the stream directly. It now reads through one loop that hands each line to
/// whoever it is for, and the second half of these tests holds that loop to its two
/// promises - every reply to its request, every event to the consumer, in order -
/// while both flow at once.
/// </para>
/// </remarks>
public sealed class IpcClientConcurrencyTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long the hammering tests get before they are called failed.
    /// </summary>
    /// <remarks>
    /// Bounded on purpose. Interleaved requests do not merely return the wrong answer;
    /// the caller that lost the race waits out the client's own ten-second reply
    /// timeout, so a regression here ran for over five minutes before saying anything.
    /// A test that hangs reports nothing useful.
    /// </remarks>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    /// <summary>A pipe name nothing else is using.</summary>
    /// <remarks>
    /// The real name is fixed per account, so a test server binding it would collide
    /// with a running window manager - or, worse, its clients would connect to that
    /// one instead and every assertion would time out for no visible reason. A name
    /// per test keeps these runnable on a desktop that is using Shubbak at the time.
    /// </remarks>
    private static string IsolatedPipe() => $"shubbak-test-{Guid.NewGuid():N}";

    /// <summary>A server that answers every request with the payload it was sent.</summary>
    /// <remarks>
    /// Echoing is what makes a crossed reply visible. A server returning a constant
    /// would pass whichever caller received whichever answer.
    /// </remarks>
    private static IpcServer StartServer(string pipe, IpcServer.RequestHandler handler)
    {
        var server = new IpcServer { PipeName = pipe };
        server.Start(handler);

        return server;
    }

    private static async Task<IpcClient> ConnectAsync(string pipe)
    {
        var client = new IpcClient { PipeName = pipe };
        await client.ConnectAsync(Timeout);

        return client;
    }

    [Fact]
    public async Task TwoThreadsSharingOneConnectionEachGetTheirOwnAnswer()
    {
        // The pump refreshing state while the user clicks a workspace, which is the
        // ordinary case and not a rare one: every workspace switch triggers a refresh,
        // and clicking a workspace is what triggers the switch.
        string pipe = IsolatedPipe();

        await using IpcServer server = StartServer(pipe, request => Task.FromResult(
            new IpcResponse(request.Id, Ok: true, Data: $"\"{request.Payload}\"")));

        await using IpcClient client = await ConnectAsync(pipe);

        const int Requests = 200;

        using var budget = new CancellationTokenSource(Budget);

        ConcurrentBag<string> failures = [];

        async Task HammerAsync(string method, string tag)
        {
            for (int i = 0; i < Requests && !budget.IsCancellationRequested; i++)
            {
                string payload = $"{tag}-{i}";

                try
                {
                    IpcResponse response = await client.SendAsync(method, payload, budget.Token);

                    // The reply has to be the reply to this request. Anything else
                    // means the caller was handed somebody else's answer.
                    string? echoed = response.Data?.Trim('"');

                    if (echoed != payload)
                        failures.Add($"sent '{payload}' and was answered '{echoed}'");
                }
                catch (Exception ex)
                {
                    failures.Add($"'{payload}' threw {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        await Task.WhenAll(
            Task.Run(() => HammerAsync("query", "refresh")),
            Task.Run(() => HammerAsync("command", "click")));

        Assert.False(
            budget.IsCancellationRequested,
            $"{Requests * 2} requests did not finish within {Budget.TotalSeconds:F0} s, " +
            "which is what a caller waiting out its own reply timeout looks like");

        Assert.True(
            failures.IsEmpty,
            $"{failures.Count} of {Requests * 2} requests were mishandled; first few: " +
            string.Join("; ", failures.Take(5)));
    }

    [Fact]
    public async Task ARequestIdIsNeverIssuedTwice()
    {
        // The ids are what pair a reply to its request. Handing the same one out twice
        // means a caller can match on somebody else's reply and return it as its own,
        // which is worse than failing because nothing reports it.
        string pipe = IsolatedPipe();

        var seen = new ConcurrentDictionary<int, byte>();
        var duplicates = new ConcurrentBag<int>();

        await using IpcServer server = StartServer(pipe, request =>
        {
            if (!seen.TryAdd(request.Id, 0)) duplicates.Add(request.Id);

            return Task.FromResult(new IpcResponse(request.Id, Ok: true, Data: $"\"{request.Payload}\""));
        });

        await using IpcClient client = await ConnectAsync(pipe);

        const int Requests = 150;

        using var budget = new CancellationTokenSource(Budget);

        async Task HammerAsync(string tag)
        {
            for (int i = 0; i < Requests && !budget.IsCancellationRequested; i++)
            {
                try { await client.SendAsync("query", $"{tag}-{i}", budget.Token); }
                catch (Exception) { /* Counted by the other test; this one is about ids. */ }
            }
        }

        await Task.WhenAll(Task.Run(() => HammerAsync("a")), Task.Run(() => HammerAsync("b")));

        Assert.True(
            duplicates.IsEmpty,
            $"the client issued {duplicates.Count} duplicate request ids: " +
            string.Join(", ", duplicates.Distinct().Take(10)));
    }

    [Fact]
    public async Task ASubscribedConnectionAnswersRequestsAndKeepsStreaming()
    {
        // One connection, both jobs. The server always interleaved replies between
        // events, whole lines each; it was the client that refused to send on a
        // subscribed connection, because its subscription read the stream directly and
        // a request reading beside it would have raced it for lines. The one loop now
        // hands each line to whoever it is for - which is what lets a provider hold a
        // lease on the connection it also hears the file was reloaded on.
        string pipe = IsolatedPipe();

        await using IpcServer server = StartServer(pipe, request => Task.FromResult(
            new IpcResponse(request.Id, Ok: true, Data: $"\"{request.Payload}\"")));

        await using IpcClient client = await ConnectAsync(pipe);
        using var stop = new CancellationTokenSource(Budget);

        await client.BeginSubscriptionAsync("*", stop.Token);
        Assert.True(client.IsSubscribed);

        // A request on the subscribed connection is answered, with its own answer.
        IpcResponse answered = await client.SendAsync("query", "first", stop.Token);
        Assert.True(answered.Ok);
        Assert.Equal("\"first\"", answered.Data);

        // And the subscription is intact: an event published after it arrives.
        IAsyncEnumerator<IpcEvent> events = client.ReadEventsAsync(stop.Token).GetAsyncEnumerator(stop.Token);

        server.Publish("window.focused", "{\"id\":1}");

        Assert.True(await events.MoveNextAsync());
        Assert.Equal("window.focused", events.Current.Topic);
        Assert.Equal("{\"id\":1}", events.Current.Data);

        // As is a second request, after events have flowed.
        IpcResponse again = await client.SendAsync("query", "second", stop.Token);
        Assert.Equal("\"second\"", again.Data);

        await stop.CancelAsync();
        try { await events.DisposeAsync(); } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task ARequestQueuedBehindTheSubscriptionHandshakeIsAnsweredByTheLoop()
    {
        // The window the check on the way in cannot see. The handshake is itself a
        // request and holds the turn; a request arriving meanwhile queues behind it;
        // the handshake completes and starts the loop; the queued request's turn
        // comes. It used to be refused here - and before that, let onto a stream the
        // subscription was already reading, where the two raced each other for lines.
        // Now it finds the loop reading and leaves its reply to it.
        string pipe = IsolatedPipe();

        await using IpcServer server = StartServer(pipe, request => Task.FromResult(
            new IpcResponse(request.Id, Ok: true, Data: $"\"{request.Payload}\"")));

        await using IpcClient client = await ConnectAsync(pipe);
        using var stop = new CancellationTokenSource(Budget);

        // Starts the handshake, which takes the turn and holds it until the server
        // answers. The request below is sent before that answer can have arrived.
        Task handshake = client.BeginSubscriptionAsync("*", stop.Token);
        Task<IpcResponse> queued = client.SendAsync("ping", "behind", stop.Token);

        await handshake;
        IpcResponse answer = await queued;

        Assert.True(answer.Ok);
        Assert.Equal("\"behind\"", answer.Data);
        Assert.True(client.IsSubscribed);

        // And the subscription it queued behind still delivers.
        IAsyncEnumerator<IpcEvent> events = client.ReadEventsAsync(stop.Token).GetAsyncEnumerator(stop.Token);
        server.Publish("layout.changed", "{\"layout\":\"splith\"}");
        Assert.True(await events.MoveNextAsync());
        Assert.Equal("layout.changed", events.Current.Topic);

        await stop.CancelAsync();
        try { await events.DisposeAsync(); } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task RepliesReachTheirRequestsAndNoEventIsLostWhileBothFlow()
    {
        // Events and replies on the same stream at once, from two sides: a thread
        // sending requests back to back while the server publishes between every
        // answer. Every reply must be the reply to its request, and every event must
        // arrive, in order - a loop that handed a line to the wrong party would show
        // up as either a crossed answer or a hole in the sequence.
        string pipe = IsolatedPipe();

        const int Events = 300;
        const int Requests = 100;

        await using IpcServer server = StartServer(pipe, request => Task.FromResult(
            new IpcResponse(request.Id, Ok: true, Data: $"\"{request.Payload}\"")));

        await using IpcClient client = await ConnectAsync(pipe);
        using var budget = new CancellationTokenSource(Budget);

        await client.BeginSubscriptionAsync("*", budget.Token);

        ConcurrentBag<string> failures = [];

        async Task AskAsync()
        {
            for (int i = 0; i < Requests && !budget.IsCancellationRequested; i++)
            {
                string payload = $"ask-{i}";

                try
                {
                    IpcResponse response = await client.SendAsync("query", payload, budget.Token);
                    if (response.Data?.Trim('"') != payload)
                        failures.Add($"sent '{payload}' and was answered '{response.Data}'");
                }
                catch (Exception ex)
                {
                    failures.Add($"'{payload}' threw {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        void PublishAll()
        {
            for (int i = 0; i < Events; i++)
                server.Publish("container.resized", $"{{\"id\":{i}}}");
        }

        var heard = new List<IpcEvent>();

        async Task ListenAsync()
        {
            await foreach (IpcEvent raised in client.ReadEventsAsync(budget.Token))
            {
                heard.Add(raised);
                if (heard.Count == Events) return;
            }
        }

        Task listening = ListenAsync();
        Task asking = Task.Run(AskAsync);
        Task publishing = Task.Run(PublishAll);

        await Task.WhenAll(asking, publishing);

        try { await listening.WaitAsync(budget.Token); }
        catch (OperationCanceledException) { }

        Assert.False(budget.IsCancellationRequested, $"did not finish within {Budget.TotalSeconds:F0} s; heard {heard.Count} of {Events} events");
        Assert.True(failures.IsEmpty, $"{failures.Count} requests were mishandled; first few: {string.Join("; ", failures.Take(5))}");

        Assert.Equal(Events, heard.Count);
        Assert.All(heard, e => Assert.Equal("container.resized", e.Topic));

        for (int i = 0; i < Events; i++)
            Assert.Equal($"{{\"id\":{i}}}", heard[i].Data);
    }

    [Fact]
    public async Task AConsumerThatFallsBehindIsHandedAResyncInPlaceOfTheBacklog()
    {
        // The loop never waits on the consumer, because the consumer may be waiting
        // on a reply that is behind the event the loop holds. A consumer that has not
        // read in a while finds its backlog gone and a resync in its place - the
        // server's own policy, and the notice a client already knows how to read -
        // followed by everything since, so nothing after the resync is missing.
        //
        // Fed from a bare pipe rather than an IpcServer, because the server has an
        // outbox with the same limit and drops first when published to this fast; the
        // point here is the client's queue, so every line goes straight to it.
        string pipe = IsolatedPipe();

        const int Published = 700;

        await using var server = new System.IO.Pipes.NamedPipeServerStream(
            pipe, System.IO.Pipes.PipeDirection.InOut, 1, System.IO.Pipes.PipeTransmissionMode.Byte,
            System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);

        Task serving = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();

            using var reader = new StreamReader(server, System.Text.Encoding.UTF8, leaveOpen: true);
            await using var writer = new StreamWriter(server, new System.Text.UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

            // The handshake: whatever id the client used, say yes to it.
            string? subscribe = await reader.ReadLineAsync();
            IpcRequest handshake = System.Text.Json.JsonSerializer.Deserialize(subscribe!, IpcJsonContext.Default.IpcRequest)!;
            await writer.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(new IpcResponse(handshake.Id, true), IpcJsonContext.Default.IpcResponse));

            for (int i = 0; i < Published; i++)
            {
                await writer.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(
                    new IpcEvent("container.resized", $"{{\"id\":{i}}}"), IpcJsonContext.Default.IpcEvent));
            }

            // Then one request, answered after every event is on the wire - so when
            // its answer is in hand, the loop has read every event above.
            string? ping = await reader.ReadLineAsync();
            IpcRequest asked = System.Text.Json.JsonSerializer.Deserialize(ping!, IpcJsonContext.Default.IpcRequest)!;
            await writer.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(new IpcResponse(asked.Id, true, "pong"), IpcJsonContext.Default.IpcResponse));

            // Held open until the client is done.
            await reader.ReadLineAsync();
        });

        await using IpcClient client = await ConnectAsync(pipe);
        using var budget = new CancellationTokenSource(Budget);

        await client.BeginSubscriptionAsync("*", budget.Token);

        // Answered, which is the proof the loop did not stop to wait for the consumer,
        // and that every event has been through it.
        Assert.Equal("pong", (await client.SendAsync("ping", null, budget.Token)).Data);

        var heard = new List<IpcEvent>();

        await foreach (IpcEvent raised in client.ReadEventsAsync(budget.Token))
        {
            heard.Add(raised);
            if (raised.Data == $"{{\"id\":{Published - 1}}}") break;
        }

        int resync = heard.FindIndex(e => e.Topic == IpcProtocol.ResyncTopic);

        Assert.True(resync >= 0, $"no resync was delivered to a consumer that fell behind; heard {heard.Count}");
        Assert.True(heard.Count < Published, "the whole backlog was kept, which is not bounded");

        // Everything after the resync is contiguous and ends on the last event.
        for (int i = resync + 1; i < heard.Count - 1; i++)
        {
            int here = int.Parse(heard[i].Data.AsSpan(6, heard[i].Data.Length - 7), System.Globalization.CultureInfo.InvariantCulture);
            int next = int.Parse(heard[i + 1].Data.AsSpan(6, heard[i + 1].Data.Length - 7), System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(here + 1, next);
        }

        await client.DisposeAsync();
        try { await serving.WaitAsync(budget.Token); } catch (Exception) { /* the pipe closing under the server's last read is the end it waits for */ }
    }

    [Fact]
    public async Task TheServerLeavingFailsTheWaitingRequestAndEndsTheStream()
    {
        // A request waiting on a subscribed connection must not wait out its timeout
        // to learn what the loop already knows. The server answers the handshake and
        // nothing after it; then it leaves. The request fails where it is awaited and
        // the event stream ends, both promptly.
        string pipe = IsolatedPipe();

        var neverAnswers = new TaskCompletionSource<IpcResponse>();

        IpcServer server = StartServer(pipe, request =>
            request.Method == "subscribe"
                ? Task.FromResult(new IpcResponse(request.Id, Ok: true))
                : neverAnswers.Task);

        await using IpcClient client = await ConnectAsync(pipe);
        using var budget = new CancellationTokenSource(Budget);

        await client.BeginSubscriptionAsync("*", budget.Token);

        IAsyncEnumerator<IpcEvent> events = client.ReadEventsAsync(budget.Token).GetAsyncEnumerator(budget.Token);
        ValueTask<bool> nextEvent = events.MoveNextAsync();

        Task<IpcResponse> waiting = client.SendAsync("query", "stalls", budget.Token);
        await Task.Delay(50, budget.Token);
        Assert.False(waiting.IsCompleted, "the stalled request completed before the server left");

        await server.DisposeAsync();

        await Assert.ThrowsAsync<IOException>(() => waiting.WaitAsync(budget.Token));
        Assert.False(await nextEvent.AsTask().WaitAsync(budget.Token), "the event stream did not end when the server left");

        // The handler is still holding its task; let it go so nothing is left pending.
        neverAnswers.TrySetCanceled();
    }
}
