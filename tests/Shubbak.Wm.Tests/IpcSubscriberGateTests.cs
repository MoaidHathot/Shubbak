using Shubbak.Ipc;

namespace Shubbak.Wm.Tests;

/// <summary>
/// Whether an event is worth building before anyone can receive it.
/// </summary>
/// <remarks>
/// <para>
/// <c>Publish</c> takes the serialised body as an argument, so the caller has already
/// paid for it by the time the client list is checked. That payload is a full JSON
/// serialisation and it was being produced on every event, whether or not anybody was
/// connected and whether or not anybody connected had asked for that topic.
/// </para>
/// <para>
/// Measured on the daemon thread it was the largest single allocator - a p99 of about
/// 64 KB per call, ahead of the layout pass - and a workspace switch emits a dozen
/// events. The gate exists to be asked first.
/// </para>
/// <para>
/// These run against a real named pipe, because the counting is spread across the
/// server and its client connections and a test that stubbed the connection would be
/// testing the stub. It is also the first test this assembly has had.
/// </para>
/// </remarks>
[Collection(SharedIpcServer.Name)]
public sealed class IpcSubscriberGateTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Refuses to run while the real daemon holds the pipe.
    /// </summary>
    /// <remarks>
    /// The pipe name is fixed, so a running window manager already owns it. The client
    /// then connects to <i>that</i> server, subscribes to it, and the server under
    /// test never sees anything - which presents as every assertion timing out for no
    /// visible reason. Worth five seconds of confusion once; not worth it twice.
    /// </remarks>
    private static void FailIfAWindowManagerIsRunning()
    {
        if (System.Diagnostics.Process.GetProcessesByName("shubbak-wm").Length == 0) return;

        throw new InvalidOperationException(
            "shubbak-wm is running and already owns the IPC pipe. A client would " +
            "connect to it rather than to the server under test, so every " +
            "subscription would be registered somewhere these tests cannot see it. " +
            "Stop it and run them again.");
    }

    private static IpcServer StartServer()
    {
        FailIfAWindowManagerIsRunning();

        var server = new IpcServer();

        // Nothing under test sends requests; the handler exists because Start demands
        // one.
        server.Start(request => Task.FromResult(new IpcResponse(request.Id, Ok: true)));

        return server;
    }

    private static async Task<IpcClient> ConnectAsync()
    {
        var client = new IpcClient();
        await client.ConnectAsync(Timeout);
        return client;
    }

    /// <summary>Subscribes and waits until the server has registered it.</summary>
    /// <remarks>
    /// The subscription has to be enumerated, not merely called. <c>SubscribeAsync</c>
    /// is an async iterator, so its body - including the request that actually
    /// subscribes - does not run until something asks for the first element. Assigning
    /// the return value and moving on registers nothing at all, which is a good way to
    /// spend five seconds wondering why the server disagrees with you.
    /// </remarks>
    private static async Task<CancellationTokenSource> SubscribeAsync(
        IpcClient client, IpcServer server, string topics, string probe)
    {
        var stop = new CancellationTokenSource();

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await foreach (IpcEvent _ in client.SubscribeAsync(topics, stop.Token))
                    {
                        // Nothing under test reads the events; the enumeration exists
                        // to start and hold the subscription.
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
                catch (IOException)
                {
                    // The pipe going away is how these tests end.
                }
            },
            stop.Token);

        bool registered = SpinWait.SpinUntil(() => server.HasSubscribers(probe), Timeout);

        Assert.True(registered, $"the server never registered a subscription to '{topics}'");

        return stop;
    }

    [Fact]
    public async Task NobodyConnectedMeansNobodyToBuildFor()
    {
        // The case that costs the most and gains the least: a desktop running without
        // a bar was serialising every event for nobody at all.
        await using IpcServer server = StartServer();

        foreach (string topic in IpcProtocol.Topics)
            Assert.False(server.HasSubscribers(topic), $"'{topic}' had a subscriber with no clients");
    }

    [Fact]
    public async Task AConnectedClientThatAsksForNothingStillCountsForNothing()
    {
        // Connecting is not subscribing. The CLI connects to send one request and
        // never subscribes, and it must not switch the payload back on for everybody.
        await using IpcServer server = StartServer();
        await using IpcClient client = await ConnectAsync();

        Assert.All(
            IpcProtocol.Topics,
            topic => Assert.False(server.HasSubscribers(topic)));
    }

    [Fact]
    public async Task OnlyTheTopicsAskedForCount()
    {
        await using IpcServer server = StartServer();
        await using IpcClient client = await ConnectAsync();

        string wanted = IpcProtocol.Topics.First();

        using CancellationTokenSource stop = await SubscribeAsync(client, server, wanted, probe: wanted);

        Assert.True(server.HasSubscribers(wanted));

        foreach (string other in IpcProtocol.Topics.Where(t => t != wanted))
            Assert.False(server.HasSubscribers(other), $"'{other}' was never asked for");
    }

    [Fact]
    public async Task SubscribingToEverythingCountsForEveryTopic()
    {
        await using IpcServer server = StartServer();
        await using IpcClient client = await ConnectAsync();

        using CancellationTokenSource stop = await SubscribeAsync(client, server, "*", probe: IpcProtocol.Topics.First());

        Assert.All(IpcProtocol.Topics, topic => Assert.True(server.HasSubscribers(topic)));
    }

    [Fact]
    public async Task AClientLeavingTakesItsInterestWithIt()
    {
        // The leak that would quietly undo the whole saving: a bar closed hours ago
        // and the payload still being built for it.
        await using IpcServer server = StartServer();

        string topic = IpcProtocol.Topics.First();

        IpcClient client = await ConnectAsync();
        using CancellationTokenSource stop = await SubscribeAsync(client, server, topic, probe: topic);

        await stop.CancelAsync();
        await client.DisposeAsync();

        bool released = SpinWait.SpinUntil(() => !server.HasSubscribers(topic), Timeout);

        Assert.True(released, "the topic still had a subscriber after the only client left");
    }

    [Fact]
    public async Task OneOfTwoLeavingDoesNotTakeTheTopicWithIt()
    {
        // Why the server counts rather than keeping a set. Taj holds one connection
        // per monitor, so on a two-monitor desktop closing one bar window would
        // otherwise switch the other one's events off.
        await using IpcServer server = StartServer();

        string topic = IpcProtocol.Topics.First();

        IpcClient first = await ConnectAsync();
        using CancellationTokenSource stopFirst = await SubscribeAsync(first, server, topic, probe: topic);

        await using IpcClient second = await ConnectAsync();
        using CancellationTokenSource stopSecond = await SubscribeAsync(second, server, topic, probe: topic);

        await stopFirst.CancelAsync();
        await first.DisposeAsync();

        // Give the server a chance to get it wrong before asserting it did not.
        await Task.Delay(200);

        Assert.True(
            server.HasSubscribers(topic),
            "the second client was still subscribed when the first one left");
    }
}

/// <summary>
/// Serialises tests that bind the window manager's named pipe.
/// </summary>
/// <remarks>
/// The pipe name is fixed, so two servers cannot be listening at once and xUnit runs
/// test classes in parallel unless told otherwise.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class SharedIpcServer
{
    public const string Name = "ipc server";
}
