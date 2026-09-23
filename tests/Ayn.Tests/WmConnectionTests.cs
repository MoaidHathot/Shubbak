using Ayn.Core;
using Shubbak.Ipc;

namespace Ayn.Tests;

/// <summary>
/// The watcher's two connections, against a real pipe.
/// </summary>
public sealed class WmConnectionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static string IsolatedPipe() => $"shubbak-test-{Guid.NewGuid():N}";

    /// <summary>A window manager that accepts every context command and remembers them.</summary>
    private static async ValueTask<(IpcServer Server, List<string> Commands)> StartServerAsync(string pipe, string? refuse = null)
    {
        List<string> commands = [];
        var server = new IpcServer { PipeName = pipe };

        server.Start(request =>
        {
            if (request.Method == "command" && request.Payload is { } command)
            {
                lock (commands) commands.Add(command);

                if (refuse is not null && command.Contains(refuse, StringComparison.Ordinal))
                    return Task.FromResult(new IpcResponse(request.Id, Ok: false, Error: $"No context called '{refuse}'."));
            }

            return Task.FromResult(new IpcResponse(request.Id, Ok: true));
        });

        // Start creates its listener pipes before it returns, so the connection
        // below finds one at once; this is the assertion of that, not a wait for it.
        Assert.True(IpcClient.IsServerRunning(pipe), "the server is not listening after Start");

        await Task.Yield();
        return (server, commands);
    }

    [Fact]
    public async Task AHoldIsAcceptedRefusedOrUnreachableAsTheWindowManagerAnswers()
    {
        string pipe = IsolatedPipe();

        await using var connection = new WmConnection(pipe);

        // Nothing there yet: unreachable, and nothing thrown.
        Assert.Equal(SendOutcome.Unreachable, connection.Send("context --set \"camera-in-use\" --lease"));

        (IpcServer server, List<string> commands) = await StartServerAsync(pipe, refuse: "undeclared");
        await using IpcServer keep = server;

        Assert.Equal(SendOutcome.Accepted, connection.Send("context --set \"camera-in-use\" --lease"));
        Assert.Equal(SendOutcome.Refused, connection.Send("context --set \"undeclared\" --lease"));

        lock (commands) Assert.Equal(2, commands.Count);
    }

    [Fact]
    public async Task TheWindowManagerGoingRaisesLostOnceAndComingBackIsFound()
    {
        string pipe = IsolatedPipe();

        (IpcServer first, List<string> _) = await StartServerAsync(pipe);

        await using var connection = new WmConnection(pipe);
        connection.Start();

        // Wait until the pump is subscribed: the server sees the subscription.
        Assert.True(SpinWait.SpinUntil(() => first.HasSubscribers("config.reloaded"), Timeout), "the pump never subscribed");

        Assert.Equal(SendOutcome.Accepted, connection.Send("context --set \"camera-in-use\" --lease"));

        await first.DisposeAsync();

        Assert.True(connection.Lost.WaitOne(Timeout), "Lost was not raised when the window manager went");

        // The loop does this on Lost; here, so the next send opens a new connection.
        connection.Drop();

        (IpcServer second, List<string> commands) = await StartServerAsync(pipe);
        await using IpcServer keep = second;

        Assert.True(SpinWait.SpinUntil(() => second.HasSubscribers("config.reloaded"), Timeout), "the pump never reconnected");
        Assert.Equal(SendOutcome.Accepted, connection.Send("context --set \"camera-in-use\" --lease"));

        lock (commands) Assert.Single(commands);
    }

    [Fact]
    public async Task AReloadAndASignalArriveOnTheirOwnEvents()
    {
        string pipe = IsolatedPipe();

        (IpcServer server, List<string> _) = await StartServerAsync(pipe);
        await using IpcServer keep = server;

        await using var connection = new WmConnection(pipe);
        connection.Start();

        Assert.True(SpinWait.SpinUntil(() => server.HasSubscribers(IpcProtocol.SignalTopic), Timeout));

        server.Publish("config.reloaded", "{}");
        Assert.True(connection.Reloaded.WaitOne(Timeout), "Reloaded was not raised");

        // Somebody else's signal is ignored; ours is queued, parsed.
        server.Publish(IpcProtocol.SignalTopic, """{"name":"palette","arguments":[]}""");
        server.Publish(IpcProtocol.SignalTopic, """{"name":"ayn","arguments":["microphone","toggle"]}""");

        Assert.True(connection.Signalled.WaitOne(Timeout), "Signalled was not raised");
        Assert.True(connection.Requests.TryDequeue(out SignalRequest? request));
        Assert.Equal("toggle-mute", request!.Verb);
        Assert.False(connection.Requests.TryDequeue(out _));
    }

    [Fact]
    public async Task ExitAllDismissesRatherThanLoses()
    {
        string pipe = IsolatedPipe();

        (IpcServer server, List<string> _) = await StartServerAsync(pipe);
        await using IpcServer keep = server;

        await using var connection = new WmConnection(pipe);
        connection.Start();

        Assert.True(SpinWait.SpinUntil(() => server.HasSubscribers(IpcProtocol.ShutdownTopic), Timeout));

        server.Publish(IpcProtocol.ShutdownTopic, """{"everything":true}""");

        Assert.True(connection.Dismissed.WaitOne(Timeout), "Dismissed was not raised for exit-all");
    }
}

/// <summary>What the watcher says about itself.</summary>
public sealed class DescriptionTests
{
    [Fact]
    public void TheFactsAreNamedAndRenamesAreSaidAsSuch()
    {
        Assert.Equal(
            "camera-in-use, microphone-in-use, microphone-muted",
            Program.Describe(new AynConfig()));

        Assert.Equal(
            "camera-in-use as \"on-camera\", microphone-muted",
            Program.Describe(new AynConfig(CameraInUse: "on-camera", MicrophoneInUse: null)));

        Assert.Equal("nothing", Program.Describe(new AynConfig(null, null, null)));
    }

    [Fact]
    public void AConsentStoreTimeIsADateOrNever()
    {
        Assert.Equal("never", Program.Describe(0));
        Assert.Equal("never", Program.Describe(-1));

        // 2026-09-22 17:30:58 UTC as a FILETIME, shown in local time.
        long fileTime = new DateTime(2026, 9, 22, 17, 30, 58, DateTimeKind.Utc).ToFileTime();
        string expected = DateTime.FromFileTime(fileTime).ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(expected, Program.Describe(fileTime));
    }
}
