using Shubbak.Ipc;
using Taj.Core.Widgets;

namespace Taj.Core.Tests;

/// <summary>
/// What a bar on one display reads off the window manager's state.
/// </summary>
/// <remarks>
/// Every decision here was once inside the pipe client, untestable and, at one time or
/// another, wrong: the layout indicator showed the first monitor's layout on every
/// display; a per-monitor bar listed everything; "null" appeared on the bar when the
/// binding mode was cleared.
/// </remarks>
public sealed class SnapshotProjectionTests
{
    private const string Left = @"\\.\DISPLAY1";
    private const string Right = @"\\.\DISPLAY2";

    private static MonitorInfoDto Monitor(long id, string device, params string[] names) =>
        new(id, device, Primary: id == 1, Dpi: 96, X: 0, Y: 0, Width: 1920, Height: 1080, ActiveWorkspace: null, Names: names);

    private static WorkspaceInfo Workspace(
        string name, string monitor, int sortIndex, bool active = false, bool hasWindows = false, string layout = "splith", bool focused = false) =>
        new(sortIndex, name, name, active, hasWindows, monitor, layout, WindowCount: hasWindows ? 1 : 0, sortIndex, MonitorIndex: 0, focused);

    private static StateSnapshot Snapshot(IReadOnlyList<MonitorInfoDto> monitors, IReadOnlyList<WorkspaceInfo> workspaces) =>
        new(monitors, workspaces, Windows: [], FocusedWindow: null, BindingMode: null, Paused: false);

    [Fact]
    public void OnlyThisDisplaysWorkspacesAreListedAndInDeclaredOrder()
    {
        StateSnapshot state = Snapshot(
            [Monitor(1, Left), Monitor(2, Right)],
            [
                Workspace("3", Left, sortIndex: 2, active: true),
                Workspace("1", Left, sortIndex: 0, hasWindows: true),
                Workspace("2", Right, sortIndex: 1, active: true),
                Workspace("__scratch", Left, sortIndex: 99),
            ]);

        BarReading reading = SnapshotProjection.Read(state, Left, ownMonitorOnly: true);

        WorkspacesWidget.WorkspaceEntry[] entries = [.. WorkspacesWidget.Decode(reading.Workspaces)];

        Assert.Equal(["1", "3"], entries.Select(e => e.Name));
        Assert.Equal("3", reading.ActiveWorkspace);
        Assert.Equal(0, reading.MonitorIndex);
    }

    [Fact]
    public void EveryWorkspaceIsListedWhenAskedToNotFilter()
    {
        StateSnapshot state = Snapshot(
            [Monitor(1, Left), Monitor(2, Right)],
            [Workspace("1", Left, 0, active: true), Workspace("2", Right, 1, active: true)]);

        BarReading reading = SnapshotProjection.Read(state, Left, ownMonitorOnly: false);

        Assert.Equal(["1", "2"], WorkspacesWidget.Decode(reading.Workspaces).Select(e => e.Name));

        // The active workspace is still this display's, whatever is listed.
        Assert.Equal("1", reading.ActiveWorkspace);
    }

    [Fact]
    public void TheLayoutIsTheOneOnThisDisplayNotTheFirstActiveOne()
    {
        StateSnapshot state = Snapshot(
            [Monitor(1, Left), Monitor(2, Right)],
            [
                Workspace("1", Left, 0, active: true, layout: "fibonacci"),
                Workspace("2", Right, 1, active: true, layout: "grid"),
            ]);

        Assert.Equal("fibonacci", SnapshotProjection.Read(state, Left, true).Layout);
        Assert.Equal("grid", SnapshotProjection.Read(state, Right, true).Layout);
        Assert.Equal("grid", SnapshotProjection.ActiveLayout(state, Right));
    }

    [Fact]
    public void ADisplayTheWindowManagerDoesNotListHasNoIndexAndNoNames()
    {
        StateSnapshot state = Snapshot([Monitor(1, Left, "dell-left")], [Workspace("1", Left, 0, active: true)]);

        BarReading reading = SnapshotProjection.Read(state, Right, true);

        Assert.Equal(-1, reading.MonitorIndex);
        Assert.Empty(reading.MonitorNames);
        Assert.Equal(string.Empty, reading.ActiveWorkspace);
        Assert.Equal(string.Empty, reading.Layout);
        Assert.Empty(WorkspacesWidget.Decode(reading.Workspaces));
    }

    [Fact]
    public void TheNamesTheConfigurationGivesADisplayComeAlong()
    {
        StateSnapshot state = Snapshot(
            [Monitor(1, Left, "dell-left", "primary"), Monitor(2, Right, "dell-right")],
            []);

        Assert.Equal(["dell-right"], SnapshotProjection.Read(state, Right, true).MonitorNames);
        Assert.Equal(1, SnapshotProjection.Read(state, Right, true).MonitorIndex);
        Assert.Equal(1, SnapshotProjection.IndexOf(state.Monitors, Right.ToUpperInvariant()));
    }

    [Fact]
    public void MonitorsDifferOnIdentityAndRectangleOnly()
    {
        MonitorInfoDto a = Monitor(1, Left);

        Assert.True(SnapshotProjection.MonitorsDiffer(null, [a]));
        Assert.True(SnapshotProjection.MonitorsDiffer([a], [a, Monitor(2, Right)]));
        Assert.True(SnapshotProjection.MonitorsDiffer([a], [a with { X = 10 }]));
        Assert.True(SnapshotProjection.MonitorsDiffer([a], [a with { DeviceId = Right }]));

        // DPI, the friendly name and the active workspace change without the bar
        // windows needing to move.
        Assert.False(SnapshotProjection.MonitorsDiffer([a], [a with { Dpi = 144, FriendlyName = "x", ActiveWorkspace = "9" }]));
        Assert.False(SnapshotProjection.MonitorsDiffer([a], [a with { DeviceId = Left.ToUpperInvariant() }]));
    }

    [Theory]
    [InlineData("\"resize\"", "resize")]
    [InlineData("  \"resize\"  ", "resize")]
    [InlineData("null", "")]
    [InlineData(" null ", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("bare", "bare")]
    [InlineData("\"\"", "")]
    public void UnquoteReadsAJsonStringAsText(string? json, string expected)
    {
        Assert.Equal(expected, SnapshotProjection.Unquote(json));
    }
}
