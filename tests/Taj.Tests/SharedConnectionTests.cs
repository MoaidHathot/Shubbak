using System.Text.Json;
using Shubbak.Ipc;
using Taj.Core;

namespace Taj.Tests;

public sealed class SharedConnectionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static StateSnapshot State(string suffix) => new(
        [new(1, "left", true, 96, 0, 0, 1000, 800, "1"), new(2, "right", false, 96, 1000, 0, 1000, 800, "2")],
        [new(3, "1", "Left" + suffix, true, false, "left", "splith", 0, 0, 0, true),
         new(4, "2", "Right" + suffix, true, false, "right", "grid", 0, 1, 1)],
        [], null, null, false);

    [Fact]
    public async Task BarsShareTheStreamAndBurstRefreshButKeepTheirOwnMonitorState()
    {
        string pipe = $"shubbak-test-{Guid.NewGuid():N}";
        int queries = 0;
        string suffix = "";
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool block = false;
        await using var server = new IpcServer { PipeName = pipe };
        server.Start(async request =>
        {
            if (request is not { Method: "query", Payload: "bar-state" }) return new(request.Id, false);
            Interlocked.Increment(ref queries);
            string json = JsonSerializer.Serialize(State(Volatile.Read(ref suffix)), IpcJsonContext.Default.StateSnapshot);
            if (Volatile.Read(ref block))
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(Timeout);
            }
            return new(request.Id, true, json);
        });
        using var left = new BarModel(TajConfigLoader.CreateDefault().Default);
        using var right = new BarModel(TajConfigLoader.CreateDefault().Default);
        await using var first = new WmConnection(left, "left") { PipeName = pipe };
        first.Start();
        Assert.True(SpinWait.SpinUntil(() => left.GetValue("layout") == "splith", Timeout));
        await using var second = new WmConnection(right, "right", first) { PipeName = pipe };
        second.Start();
        Assert.True(SpinWait.SpinUntil(() => right.GetValue("layout") == "grid", Timeout));
        Assert.True(SpinWait.SpinUntil(() => server.ClientCount == 2, Timeout));

        int before = Volatile.Read(ref queries);
        Volatile.Write(ref block, true);
        server.Publish("workspace.activated", "{}");
        await entered.Task.WaitAsync(Timeout);
        try
        {
            Volatile.Write(ref suffix, " changed");
            for (int i = 0; i < 50; i++) server.Publish("workspace.activated", "{}");
            // Hold the first refresh while a real pipe queues the burst.
            await Task.Delay(100);
        }
        finally { Volatile.Write(ref block, false); release.TrySetResult(); }
        Assert.True(SpinWait.SpinUntil(() => right.GetValue("workspaces")?.Contains("Right changed", StringComparison.Ordinal) == true, Timeout));
        Assert.Contains("Left changed", left.GetValue("workspaces"), StringComparison.Ordinal);
        Assert.DoesNotContain("Right changed", left.GetValue("workspaces"), StringComparison.Ordinal);
        Assert.InRange(Volatile.Read(ref queries) - before, 2, 4);

        // Removing the original owner must not close the remaining display's stream.
        await first.DisposeAsync();
        Volatile.Write(ref suffix, " again");
        server.Publish("workspace.activated", "{}");
        Assert.True(SpinWait.SpinUntil(() => right.GetValue("workspaces")?.Contains("Right again", StringComparison.Ordinal) == true, Timeout));
        Assert.Equal(2, server.ClientCount);
    }

    [Fact]
    public async Task OneIconReadUpdatesBothDisplayModels()
    {
        string pipe = $"shubbak-test-{Guid.NewGuid():N}";
        int icons = 0;
        WindowInfo? focused = null;
        const string Icon = "{\"handle\":42,\"width\":1,\"height\":1,\"pixels\":\"AAAAAA==\",\"source\":\"window\"}";
        await using var server = new IpcServer { PipeName = pipe };
        server.Start(request =>
        {
            if (request.Method == WindowIcon.Method)
            {
                Interlocked.Increment(ref icons);
                return Task.FromResult(new IpcResponse(request.Id, true, Icon));
            }
            return Task.FromResult(new IpcResponse(request.Id, true,
                JsonSerializer.Serialize(State("") with { FocusedWindow = Volatile.Read(ref focused) }, IpcJsonContext.Default.StateSnapshot)));
        });
        using var left = new BarModel(TajConfigLoader.CreateDefault().Default);
        using var right = new BarModel(TajConfigLoader.CreateDefault().Default);
        await using var first = new WmConnection(left, "left") { PipeName = pipe };
        first.Start();
        await using var second = new WmConnection(right, "right", first) { PipeName = pipe };
        second.Start();
        Assert.True(SpinWait.SpinUntil(() => right.GetValue("layout") == "grid", Timeout));
        Volatile.Write(ref focused, new WindowInfo(42, 42, "title", "class", "process", "tiling", true, 0, 0, 100, 100));
        server.Publish("window.focused", JsonSerializer.Serialize(focused, IpcJsonContext.Default.WindowInfo));
        Assert.True(SpinWait.SpinUntil(() => left.GetValue(FocusedWindow.IconKey) == Icon && right.GetValue(FocusedWindow.IconKey) == Icon, Timeout));
        Assert.Equal(1, Volatile.Read(ref icons));
    }

    [Fact]
    public async Task ASlowIconDoesNotHoldUpClicksOrStateReads()
    {
        string pipe = $"shubbak-test-{Guid.NewGuid():N}";
        var iconEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseIcon = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commandArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        WindowInfo? focused = null;
        await using var server = new IpcServer { PipeName = pipe };
        server.Start(async request =>
        {
            if (request.Method == WindowIcon.Method)
            {
                iconEntered.TrySetResult();
                await releaseIcon.Task;
                return new(request.Id, false);
            }
            if (request.Method == "command")
            {
                commandArrived.TrySetResult();
                return new(request.Id, true);
            }
            return new(request.Id, true, JsonSerializer.Serialize(State("") with { FocusedWindow = Volatile.Read(ref focused) }, IpcJsonContext.Default.StateSnapshot));
        });
        using var model = new BarModel(TajConfigLoader.CreateDefault().Default);
        await using var connection = new WmConnection(model, "left") { PipeName = pipe };
        connection.Start();
        Assert.True(SpinWait.SpinUntil(() => model.GetValue("layout") == "splith", Timeout));
        Volatile.Write(ref focused, new WindowInfo(42, 42, "title", "class", "process", "tiling", true, 0, 0, 100, 100));
        server.Publish("window.focused", JsonSerializer.Serialize(focused, IpcJsonContext.Default.WindowInfo));
        await iconEntered.Task.WaitAsync(Timeout);
        try
        {
            Task command = connection.SendCommandAsync("focus --direction right");
            await commandArrived.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await command.WaitAsync(Timeout);
        }
        finally { releaseIcon.TrySetResult(); }
    }
}
