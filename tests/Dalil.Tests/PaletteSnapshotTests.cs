using System.Text.Json;
using Shubbak.Ipc;

namespace Dalil.Tests;

public sealed class PaletteSnapshotTests
{
    [Theory]
    [InlineData(false, "palette")]
    [InlineData(true, "palette-all")]
    public async Task OpeningReadsOneCoherentSnapshot(bool everyWindow, string query)
    {
        string pipe = $"shubbak-test-{Guid.NewGuid():N}";
        int calls = 0;
        var state = new StateSnapshot(
            [new(1, "screen", true, 96, 0, 0, 1000, 800, "work")],
            [new(2, "work", "Work", true, false, "screen", "grid", 0, 0, 0, true)],
            [], null, "resize", true, true);
        var snapshot = new PaletteSnapshot(state, [], [], [], ["grid"], [], [], []);
        await using var server = new IpcServer { PipeName = pipe };
        server.Start(request =>
        {
            Interlocked.Increment(ref calls);
            Assert.Equal("query", request.Method);
            Assert.Equal(query, request.Payload);
            return Task.FromResult(new IpcResponse(request.Id, true, JsonSerializer.Serialize(snapshot, IpcJsonContext.Default.PaletteSnapshot)));
        });
        await using var connection = new WmConnection { PipeName = pipe };
        PaletteSources sources = await connection.ReadAsync(false, everyWindow: everyWindow).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, calls);
        Assert.Equal("work", sources.FocusedWorkspace);
        Assert.Equal("Work", sources.WorkspaceLabels!["work"]);
        Assert.True(sources.Status.Paused);
        Assert.True(sources.Status.Suspended);
        Assert.Equal("resize", sources.Status.BindingMode);
        Assert.Single(sources.Workspaces);
    }
}
