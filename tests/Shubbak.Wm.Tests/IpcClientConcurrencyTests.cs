using System.Collections.Concurrent;
using Shubbak.Ipc;

namespace Shubbak.Wm.Tests;

/// <summary>
/// One connection used by two threads at once.
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
    public async Task ASubscribedConnectionRefusesRequestsRatherThanRacingItsOwnEventLoop()
    {
        // The one case the turnstile cannot cover: a subscription reads the stream
        // directly and forever, so a request sent on the same connection would compete
        // with the event loop for lines. Refusing says so; the alternative is the two
        // of them quietly stealing each other's.
        string pipe = IsolatedPipe();

        await using IpcServer server = StartServer(pipe, request =>
            Task.FromResult(new IpcResponse(request.Id, Ok: true)));

        await using IpcClient client = await ConnectAsync(pipe);
        using var stop = new CancellationTokenSource();

        IAsyncEnumerator<IpcEvent> events = client.SubscribeAsync("*", stop.Token).GetAsyncEnumerator();

        // The subscription only registers once it is enumerated: SubscribeAsync is an
        // async iterator, so its body does not run until something asks for an element.
        ValueTask<bool> pending = events.MoveNextAsync();

        Assert.True(
            SpinWait.SpinUntil(() => server.HasSubscribers(IpcProtocol.Topics.First()), Timeout),
            "the server never registered the subscription");

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync("ping"));

        await stop.CancelAsync();

        try { await pending; } catch (OperationCanceledException) { }
    }
}
