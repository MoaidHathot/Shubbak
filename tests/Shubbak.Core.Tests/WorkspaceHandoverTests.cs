using Shubbak.Core.Geometry;
using Shubbak.Core.Layouts;
using Shubbak.Core.Tree;
using Shubbak.Core.Wm;

namespace Shubbak.Core.Tests;

/// <summary>
/// What a monitor shows after a workspace is taken away from it.
/// </summary>
/// <remarks>
/// <para>
/// Moving a workspace to another monitor leaves the origin needing something to
/// display. Choosing whichever workspace happened to sit first in the list exposed one
/// the user had not selected, so windows appeared that they had not asked for - and the
/// choice bypassed the property that records the previous workspace, leaving
/// toggle-workspace-on-refocus pointing at somewhere they had never been.
/// </para>
/// <para>
/// GlazeWM redraws both the moved workspace and the one it displaced, and guards
/// against a monitor being left with none at all. The same three concerns are pinned
/// here.
/// </para>
/// </remarks>
public sealed class WorkspaceHandoverTests
{
    private static WindowManager Create() =>
        WmFixture.Create(monitors: 2, workspaceNames: ["1", "2", "3"]);

    [Fact]
    public void TheOriginFallsBackToWhereTheUserWasLast()
    {
        WindowManager wm = Create();
        MonitorNode monitor = wm.Root.Monitors[0];

        // Been on 1, then 2, now 3 - so 2 is where we were last.
        wm.FocusWorkspace("1");
        wm.FocusWorkspace("2");
        wm.FocusWorkspace("3");

        wm.MoveWorkspaceToMonitor(Direction.Right);

        Assert.Equal("2", monitor.ActiveWorkspace!.Name);
    }

    [Fact]
    public void TheStaleToggleTargetIsCleared()
    {
        // The workspace we came from is now the one being shown, so there is nothing
        // meaningful left to toggle back to. Leaving it set sent refocus somewhere
        // arbitrary.
        WindowManager wm = Create();
        MonitorNode monitor = wm.Root.Monitors[0];

        wm.FocusWorkspace("2");
        wm.FocusWorkspace("3");

        wm.MoveWorkspaceToMonitor(Direction.Right);

        Assert.Null(monitor.PreviousWorkspace);
    }

    [Fact]
    public void TheMovedWorkspaceIsShownOnItsNewMonitor()
    {
        WindowManager wm = Create();

        wm.FocusWorkspace("3");
        wm.MoveWorkspaceToMonitor(Direction.Right);

        Assert.Equal("3", wm.Root.Monitors[1].ActiveWorkspace!.Name);
        Assert.Same(wm.Root.Monitors[1], wm.Root.FindWorkspace("3")!.Monitor);
    }

    [Fact]
    public void OnlyOneWorkspacePerMonitorIsEverVisible()
    {
        // The property behind "I can see several workspaces' windows at once": a
        // monitor displays exactly one, so everything else must be concealed.
        WindowManager wm = Create();

        wm.FocusWorkspace("3");
        wm.MoveWorkspaceToMonitor(Direction.Right);

        foreach (MonitorNode monitor in wm.Root.Monitors)
        {
            Assert.Equal(
                1,
                monitor.Workspaces.Count(w => ReferenceEquals(w, monitor.ActiveWorkspace)));
        }
    }

    [Fact]
    public void EveryWindowOffTheDisplayedWorkspaceIsPlacedHidden()
    {
        // The same property, stated where it actually bites: the layout's visibility
        // decision, which is what the platform layer acts on.
        WindowManager wm = Create();

        wm.FocusWorkspace("1");
        WindowNode onOne = wm.Open("one");

        wm.FocusWorkspace("2");
        WindowNode onTwo = wm.Open("two");

        wm.FocusWorkspace("3");
        wm.MoveWorkspaceToMonitor(Direction.Right);

        IReadOnlyList<Placement> placements =
            new LayoutEngine().Arrange(wm.Root, ArrangeOptions.Default);

        foreach (Placement placement in placements)
        {
            bool onDisplayed = ReferenceEquals(
                placement.Window.Workspace,
                placement.Window.Workspace?.Monitor?.ActiveWorkspace);

            Assert.Equal(onDisplayed, placement.Visible);
        }

        // Sanity: the two windows really are on different workspaces.
        Assert.NotSame(onOne.Workspace, onTwo.Workspace);
    }

    [Fact]
    public void AMonitorIsNeverLeftWithNothingToShow()
    {
        // GlazeWM guards this explicitly. A monitor with no displayed workspace has
        // no work area to tile into and nowhere to put a new window.
        WindowManager wm = WmFixture.Create(monitors: 2, workspaceNames: ["1"]);

        wm.AddWorkspace(new WorkspaceNode("2"), wm.Root.Monitors[1]);
        wm.ActivateWorkspace(wm.Root.Monitors[1].Workspaces[0]);
        wm.FocusWorkspace("1");

        wm.MoveWorkspaceToMonitor(Direction.Right);

        // Monitor 0 gave away its only workspace; it must not be left showing null
        // while still having workspaces, and must report honestly if it has none.
        MonitorNode origin = wm.Root.Monitors[0];

        Assert.Equal(origin.Workspaces.Count > 0, origin.ActiveWorkspace is not null);
    }
}
