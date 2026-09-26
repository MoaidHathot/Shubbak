using Shubbak.Ipc;

namespace Shubbak.Cli.Tests;

/// <summary>
/// <c>shubbak context ... --hold</c>: a pin with a lease, held for as long as the
/// process runs.
/// </summary>
/// <remarks>
/// <para>
/// A lease dies with the connection that made it, and the command line's connection
/// closed the moment the reply arrived - so <c>--lease</c> from a script was refused
/// with a hint to "hold a pipe connection open from your own process", which is a
/// provider, and was left as an exercise. This is that process: a script starts it
/// and stops it, and the context is held exactly in between.
/// </para>
/// <para>
/// The pipe half is driven against a real server here, as the watcher's is, because
/// what matters is what the window manager sees: the pin on connecting, the pin again
/// when the window manager comes back or reloads, the connection gone the moment the
/// hold ends, and nothing at all after <c>exit-all</c>.
/// </para>
/// </remarks>
public sealed class HoldCommandTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static string IsolatedPipe() => $"shubbak-test-{Guid.NewGuid():N}";

    /// <summary>A window manager that remembers every command and refuses the ones it is told to.</summary>
    private static IpcServer StartServer(string pipe, List<string> commands, Func<string, string?>? refuse = null)
    {
        var server = new IpcServer { PipeName = pipe };

        server.Start(request =>
        {
            if (request.Method == "command" && request.Payload is { } command)
            {
                lock (commands) commands.Add(command);

                if (refuse?.Invoke(command) is { } reason)
                    return Task.FromResult(new IpcResponse(request.Id, Ok: false, Error: reason));
            }

            return Task.FromResult(new IpcResponse(request.Id, Ok: true));
        });

        Assert.True(IpcClient.IsServerRunning(pipe), "the server is not listening after Start");
        return server;
    }

    private static int CommandCount(List<string> commands)
    {
        lock (commands) return commands.Count;
    }

    // ---- reading the arguments -----------------------------------------------------

    [Fact]
    public void WithoutTheFlagThereIsNothingToHold()
    {
        Assert.Null(HoldArguments.Parse(["context", "--set", "meeting"], out string? problem));
        Assert.Null(problem);

        Assert.Null(HoldArguments.Parse(["focus", "--workspace", "2", "--hold"], out problem));
        Assert.Null(problem);
    }

    [Fact]
    public void TheFlagIsTakenOutAndTheLeasePutIn()
    {
        HoldArguments? hold = HoldArguments.Parse(["context", "--set", "meeting", "--hold"], out string? problem);

        Assert.Null(problem);
        Assert.NotNull(hold);
        Assert.Equal("context --set meeting --lease", hold.Command);
        Assert.Equal("meeting", hold.Context);
    }

    [Fact]
    public void ALeaseAlreadyWrittenIsNotDoubledAndATimeToLiveIsKept()
    {
        HoldArguments? hold = HoldArguments.Parse(["context", "--set", "meeting", "--lease", "--ttl", "5s", "--hold"], out _);

        Assert.Equal("context --set meeting --lease --ttl 5s", hold!.Command);
    }

    [Fact]
    public void ANameWithASpaceIsSpelledForThePipe()
    {
        HoldArguments? hold = HoldArguments.Parse(["context", "--set", "in a call", "--hold"], out _);

        Assert.Equal("context --set \"in a call\" --lease", hold!.Command);
        Assert.Equal("in a call", hold.Context);
    }

    [Fact]
    public void ClearingCanBeHeldTooSinceAClearIsAPin()
    {
        HoldArguments? hold = HoldArguments.Parse(["context", "--clear", "docked", "--hold"], out _);

        Assert.Equal("context --clear docked --lease", hold!.Command);
    }

    [Fact]
    public void AutoCannotBeHeldBecauseItTakesThePinOff()
    {
        Assert.Null(HoldArguments.Parse(["context", "--auto", "meeting", "--hold"], out string? problem));
        Assert.Contains("--auto", problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void AMistakeIsReportedInTheParsersWords()
    {
        Assert.Null(HoldArguments.Parse(["context", "--set", "--hold"], out string? problem));
        Assert.Contains("does not name a context", problem!, StringComparison.Ordinal);
    }

    // ---- the hold itself -------------------------------------------------------------

    [Fact]
    public async Task ThePinIsSentOnConnectingAndTheConnectionEndsWithTheHold()
    {
        string pipe = IsolatedPipe();
        List<string> commands = [];
        await using IpcServer server = StartServer(pipe, commands);

        HoldArguments hold = HoldArguments.Parse(["context", "--set", "meeting", "--hold"], out _)!;
        var output = new StringWriter();
        var error = new StringWriter();
        using var stop = new CancellationTokenSource();

        Task<int> running = HoldCommand.RunAsync(hold, output, error, pipe, stop.Token);

        Assert.True(SpinWait.SpinUntil(() => CommandCount(commands) == 1, Timeout), "the pin was never sent");
        Assert.Equal("context --set meeting --lease", commands[0]);
        Assert.True(SpinWait.SpinUntil(() => output.ToString().Contains("holding \"meeting\"", StringComparison.Ordinal), Timeout));

        // Two connections while holding - the subscription and the one the lease lives on.
        Assert.True(SpinWait.SpinUntil(() => server.ClientCount == 2, Timeout), $"expected 2 clients, saw {server.ClientCount}");

        stop.Cancel();

        Assert.Equal(0, await running.WaitAsync(Timeout));

        // Letting go is the connection closing, which is what releases the lease.
        Assert.True(SpinWait.SpinUntil(() => server.ClientCount == 0, Timeout), "the connections did not close when the hold ended");
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task ARefusedPinEndsTheHoldWithTheReason()
    {
        string pipe = IsolatedPipe();
        List<string> commands = [];
        await using IpcServer server = StartServer(pipe, commands, refuse: _ => "No context called 'meeting'.");

        HoldArguments hold = HoldArguments.Parse(["context", "--set", "meeting", "--hold"], out _)!;
        var error = new StringWriter();

        int exit = await HoldCommand.RunAsync(hold, new StringWriter(), error, pipe, CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(1, exit);
        Assert.Contains("No context called 'meeting'", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoWindowManagerIsSaidAtOnceRatherThanWaitedFor()
    {
        HoldArguments hold = HoldArguments.Parse(["context", "--set", "meeting", "--hold"], out _)!;
        var error = new StringWriter();

        int exit = await HoldCommand.RunAsync(hold, new StringWriter(), error, IsolatedPipe(), CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(2, exit);
        Assert.Contains("no window manager is running", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWindowManagerThatComesBackIsHoldingAgain()
    {
        string pipe = IsolatedPipe();
        List<string> first = [];
        IpcServer server = StartServer(pipe, first);

        HoldArguments hold = HoldArguments.Parse(["context", "--set", "meeting", "--hold"], out _)!;
        var output = new StringWriter();
        using var stop = new CancellationTokenSource();

        Task<int> running = HoldCommand.RunAsync(hold, output, new StringWriter(), pipe, stop.Token);
        Assert.True(SpinWait.SpinUntil(() => CommandCount(first) == 1, Timeout));

        // The window manager restarts: the leases it held died with it.
        await server.DisposeAsync();

        List<string> second = [];
        await using IpcServer returned = StartServer(pipe, second);

        Assert.True(SpinWait.SpinUntil(() => CommandCount(second) == 1, Timeout), "the pin was not sent to the window manager that came back");
        Assert.Equal("context --set meeting --lease", second[0]);
        Assert.True(SpinWait.SpinUntil(() => output.ToString().Contains("again", StringComparison.Ordinal), Timeout));

        stop.Cancel();
        Assert.Equal(0, await running.WaitAsync(Timeout));
    }

    [Fact]
    public async Task AReloadHasThePinAssertedAgain()
    {
        // The window manager drops the pins of contexts the reloaded file no longer
        // declares and tells nobody; asserting again is one command.
        string pipe = IsolatedPipe();
        List<string> commands = [];
        await using IpcServer server = StartServer(pipe, commands);

        HoldArguments hold = HoldArguments.Parse(["context", "--set", "meeting", "--hold"], out _)!;
        using var stop = new CancellationTokenSource();

        Task<int> running = HoldCommand.RunAsync(hold, new StringWriter(), new StringWriter(), pipe, stop.Token);
        Assert.True(SpinWait.SpinUntil(() => CommandCount(commands) == 1, Timeout));
        Assert.True(SpinWait.SpinUntil(() => server.HasSubscribers("config.reloaded"), Timeout));

        server.Publish("config.reloaded", "{}");

        Assert.True(SpinWait.SpinUntil(() => CommandCount(commands) == 2, Timeout), "the pin was not asserted again after the reload");

        stop.Cancel();
        Assert.Equal(0, await running.WaitAsync(Timeout));
    }

    [Fact]
    public async Task ExitAllLetsGoWhereARestartWaits()
    {
        string pipe = IsolatedPipe();
        List<string> commands = [];
        await using IpcServer server = StartServer(pipe, commands);

        HoldArguments hold = HoldArguments.Parse(["context", "--set", "meeting", "--hold"], out _)!;
        var output = new StringWriter();

        Task<int> running = HoldCommand.RunAsync(hold, output, new StringWriter(), pipe, CancellationToken.None);
        Assert.True(SpinWait.SpinUntil(() => server.HasSubscribers(IpcProtocol.ShutdownTopic), Timeout));

        // A restart is waited out.
        server.Publish(IpcProtocol.ShutdownTopic, ShutdownNotice.Alone);
        Assert.True(SpinWait.SpinUntil(() => output.ToString().Contains("restarting", StringComparison.Ordinal), Timeout));
        Assert.False(running.IsCompleted);

        // Everything going is the end of the hold.
        server.Publish(IpcProtocol.ShutdownTopic, ShutdownNotice.Everything);

        Assert.Equal(0, await running.WaitAsync(Timeout));
        Assert.Contains("letting go", output.ToString(), StringComparison.Ordinal);
    }
}
