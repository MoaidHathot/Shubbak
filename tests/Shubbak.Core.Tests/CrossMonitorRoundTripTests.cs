using Shubbak.Core.Geometry;
using Shubbak.Core.Tree;
using Shubbak.Core.Wm;

namespace Shubbak.Core.Tests;

/// <summary>
/// Moving the only window on a workspace to another monitor, and back.
/// </summary>
/// <remarks>
/// The sequence a user actually performs: push the last window off a workspace, then
/// change your mind. It leaves the source workspace empty, the focused monitor
/// changed, and the window as the only occupant of somewhere else - three states that
/// each have their own handling, met at once.
/// </remarks>
public sealed class CrossMonitorRoundTripTests
{
    /// <summary>Two monitors, workspace "3" on the first and "/" on the second.</summary>
    private static WindowManager Create()
    {
        WindowManager wm = WmFixture.Create(monitors: 2, workspaceNames: ["3"]);

        wm.AddWorkspace(new WorkspaceNode("/"), wm.Root.Monitors[1]);
        wm.ActivateWorkspace(wm.Root.Monitors[1].Workspaces[0]);
        wm.ActivateWorkspace(wm.Root.Monitors[0].Workspaces[0]);

        return wm;
    }

    [Fact]
    public void TheOnlyWindowCanBePushedToTheOtherMonitor()
    {
        WindowManager wm = Create();
        WindowNode terminal = wm.Open("terminal");
        wm.Arrange();

        Assert.True(wm.MoveDirection(Direction.Right).Succeeded);

        Assert.Equal("/", terminal.Workspace!.Name);
        Assert.Same(terminal, wm.FocusedWindow);
    }

    [Fact]
    public void TheSourceWorkspaceSurvivesBeingEmptied()
    {
        // Declared workspaces are never reaped: an empty one must survive so its
        // keybinding still goes somewhere.
        WindowManager wm = Create();
        wm.Open("terminal");
        wm.Arrange();

        wm.MoveDirection(Direction.Right);

        Assert.NotNull(wm.Root.FindWorkspace("3"));
    }

    [Fact]
    public void ItCanBeSentStraightBack()
    {
        // The reported failure: after the push, sending it home did nothing useful.
        WindowManager wm = Create();
        WindowNode terminal = wm.Open("terminal");
        wm.Arrange();

        wm.MoveDirection(Direction.Right);

        Assert.True(wm.MoveToWorkspace("3").Succeeded);

        Assert.Equal("3", terminal.Workspace!.Name);
    }

    [Fact]
    public void SendingItHomeByNameLeavesFocusBehind()
    {
        // Deliberate, and the opposite of the directional case above.
        //
        // The idiom for "send it there and follow" is two commands on one key -
        // `move --workspace 3; focus --workspace 3`. If the move moved focus, the
        // focus command would be re-focusing the workspace it was already on, and
        // with toggle-workspace-on-refocus that bounces to the previous workspace:
        // the key appeared to send the window to 3 and then show 2.
        WindowManager wm = Create();
        WindowNode terminal = wm.Open("terminal");
        wm.Arrange();

        wm.MoveDirection(Direction.Right);
        wm.MoveToWorkspace("3");

        Assert.Equal("3", terminal.Workspace!.Name);

        // The window went home; the view did not follow it there by itself.
        Assert.NotSame(terminal, wm.FocusedWindow);
    }

    [Fact]
    public void TheFollowUpFocusCommandGoesWhereItSays()
    {
        // The pair as a keybinding actually runs them.
        WindowManager wm = Create();
        WindowNode terminal = wm.Open("terminal");
        wm.Arrange();

        wm.MoveDirection(Direction.Right);

        wm.MoveToWorkspace("3");
        wm.FocusWorkspace("3");

        Assert.Equal("3", wm.FocusedWorkspace!.Name);
        Assert.Same(terminal, wm.FocusedWindow);
    }

    [Fact]
    public void TheMonitorItReturnsToBecomesTheFocusedOne()
    {
        // Otherwise the next directional command is measured from the wrong screen.
        WindowManager wm = Create();
        wm.Open("terminal");
        wm.Arrange();

        wm.MoveDirection(Direction.Right);
        wm.MoveToWorkspace("3");

        Assert.Equal(wm.Root.Monitors[0].DeviceId, wm.FocusedMonitor!.DeviceId);
    }

    [Fact]
    public void ItCanBePushedBackTheWayItCame()
    {
        WindowManager wm = Create();
        WindowNode terminal = wm.Open("terminal");
        wm.Arrange();

        wm.MoveDirection(Direction.Right);

        Assert.True(wm.MoveDirection(Direction.Left).Succeeded);

        Assert.Equal("3", terminal.Workspace!.Name);
        Assert.Same(terminal, wm.FocusedWindow);
    }

    [Fact]
    public void TheRoundTripLeavesOneWindowInOnePlace()
    {
        // A window that ends up counted on two workspaces produces a phantom tile on
        // the one it is not really on.
        WindowManager wm = Create();
        wm.Open("terminal");
        wm.Arrange();

        wm.MoveDirection(Direction.Right);
        wm.MoveToWorkspace("3");

        Assert.Single(wm.Root.DescendantWindows());
        Assert.Empty(wm.Root.FindWorkspace("/")!.DescendantWindows());
        Assert.Single(wm.Root.FindWorkspace("3")!.DescendantWindows());
    }
}
