using System.Collections.Concurrent;
using Shubbak.Ipc;
using Taj.Core;
using Taj.Core.Sources;

namespace Taj.Tests;

/// <summary>
/// The bar's connection and the <c>signal</c> topic, against a real pipe.
/// </summary>
/// <remarks>
/// <para>
/// The bar subscribes to <c>signal</c> only when a source reads one. Every subscriber
/// costs the window manager two calls on its own thread per signal - and the palette's
/// key is a signal - and takes from it the one line that says a signal was raised with
/// nobody listening, which is how a palette key that does nothing is diagnosed. A bar
/// with no signal source must therefore not be on the list; one with a signal source
/// must be, and must ask for the values to be said again once its subscription is in
/// place, since a signal is fire-and-forget and the window manager keeps none of it.
/// </para>
/// <para>
/// And a reload that adds the first signal source or removes the last must move the
/// bar from one list to the other without the bar noticing: that is
/// <see cref="WmConnection.Restart"/>.
/// </para>
/// </remarks>
public sealed class WmConnectionSignalTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static string IsolatedPipe() => $"shubbak-test-{Guid.NewGuid():N}";

    /// <summary>A window manager that has no state to give and remembers every command.</summary>
    private static IpcServer StartServer(string pipe, ConcurrentQueue<string> commands)
    {
        var server = new IpcServer { PipeName = pipe };

        server.Start(request =>
        {
            if (request.Method == "command" && request.Payload is { } command) commands.Enqueue(command);

            // No snapshot: the refresh returns without one, which is the shape of a
            // window manager that is there but has nothing to say yet.
            return Task.FromResult(request.Method == "query"
                ? new IpcResponse(request.Id, Ok: false, Error: "no state in this test")
                : new IpcResponse(request.Id, Ok: true));
        });

        Assert.True(IpcClient.IsServerRunning(pipe), "the server is not listening after Start");
        return server;
    }

    private static BarModel Model() => new(TajConfigLoader.CreateDefault().Default);

    [Fact]
    public async Task ABarWithNoSignalSourceDoesNotSubscribeToSignals()
    {
        string pipe = IsolatedPipe();
        var commands = new ConcurrentQueue<string>();
        await using IpcServer server = StartServer(pipe, commands);

        using BarModel model = Model();
        await using var connection = new WmConnection(model, @"\\.\DISPLAY1") { PipeName = pipe };
        connection.Start(listenForSignals: false);

        Assert.True(SpinWait.SpinUntil(() => server.HasSubscribers("workspace.activated"), Timeout), "the pump never subscribed");
        Assert.True(SpinWait.SpinUntil(() => connection.IsConnected, Timeout));

        Assert.False(server.HasSubscribers(IpcProtocol.SignalTopic));
        Assert.False(connection.ListensForSignals);

        // And it asked nobody to announce anything: the only traffic is the snapshot.
        await Task.Delay(100);
        Assert.Empty(commands);
    }

    [Fact]
    public async Task ABarWithASignalSourceSubscribesAndAsksForTheValuesOnceConnected()
    {
        string pipe = IsolatedPipe();
        var commands = new ConcurrentQueue<string>();
        await using IpcServer server = StartServer(pipe, commands);

        using BarModel model = Model();
        await using var connection = new WmConnection(model, @"\\.\DISPLAY1") { PipeName = pipe };
        connection.Start(listenForSignals: true);

        Assert.True(SpinWait.SpinUntil(() => server.HasSubscribers(IpcProtocol.SignalTopic), Timeout), "the pump never subscribed to signals");
        Assert.True(SpinWait.SpinUntil(() => commands.Count == 1, Timeout), "the bar did not ask for the values");

        Assert.True(commands.TryDequeue(out string? announce));
        Assert.Equal(SignalSource.AnnounceCommand, announce);
        Assert.True(connection.ListensForSignals);
    }

    [Fact]
    public async Task ASignalIsRaisedWithItsNameAndArguments()
    {
        string pipe = IsolatedPipe();
        var commands = new ConcurrentQueue<string>();
        await using IpcServer server = StartServer(pipe, commands);

        using BarModel model = Model();
        await using var connection = new WmConnection(model, @"\\.\DISPLAY1") { PipeName = pipe };

        var received = new ConcurrentQueue<(string Name, string[] Arguments)>();
        connection.SignalReceived += (name, arguments) => received.Enqueue((name, [.. arguments]));

        connection.Start(listenForSignals: true);
        Assert.True(SpinWait.SpinUntil(() => server.HasSubscribers(IpcProtocol.SignalTopic), Timeout));

        server.Publish(IpcProtocol.SignalTopic, """{"name":"battery","arguments":["87"]}""");
        server.Publish(IpcProtocol.SignalTopic, "not a signal at all");
        server.Publish(IpcProtocol.SignalTopic, """{"name":"weather","arguments":["Sunny","21C"]}""");

        Assert.True(SpinWait.SpinUntil(() => received.Count == 2, Timeout), "the signals did not arrive");

        Assert.True(received.TryDequeue(out (string Name, string[] Arguments) battery));
        Assert.Equal("battery", battery.Name);
        Assert.Equal(["87"], battery.Arguments);

        Assert.True(received.TryDequeue(out (string Name, string[] Arguments) weather));
        Assert.Equal("weather", weather.Name);
        Assert.Equal(["Sunny", "21C"], weather.Arguments);
    }

    [Fact]
    public async Task RestartingMovesTheBarOntoTheSignalListAndOffItAgain()
    {
        string pipe = IsolatedPipe();
        var commands = new ConcurrentQueue<string>();
        await using IpcServer server = StartServer(pipe, commands);

        using BarModel model = Model();
        await using var connection = new WmConnection(model, @"\\.\DISPLAY1") { PipeName = pipe };
        connection.Start(listenForSignals: false);

        Assert.True(SpinWait.SpinUntil(() => connection.IsConnected, Timeout));
        Assert.False(server.HasSubscribers(IpcProtocol.SignalTopic));

        // The file gained its first signal source.
        connection.Restart(listenForSignals: true);

        Assert.True(SpinWait.SpinUntil(() => server.HasSubscribers(IpcProtocol.SignalTopic), Timeout), "the restart did not subscribe to signals");
        Assert.True(SpinWait.SpinUntil(() => commands.Count == 1, Timeout), "the restart did not ask for the values");
        Assert.Equal(SignalSource.AnnounceCommand, commands.Single());

        // And lost its last.
        connection.Restart(listenForSignals: false);

        Assert.True(SpinWait.SpinUntil(() => connection.IsConnected && !server.HasSubscribers(IpcProtocol.SignalTopic), Timeout), "the restart did not leave the signal list");
        Assert.False(connection.ListensForSignals);

        // The connection pill was never touched: a restart is not a loss.
        Assert.Equal(WindowManagerStatus.ConnectionLabel(connected: true, everConnected: true), model.GetValue(WindowManagerStatus.ConnectionKey));
    }
}
