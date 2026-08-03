using Shubbak.Core.Commands;
using Shubbak.Core.Geometry;
using Shubbak.Core.Tree;
using Shubbak.Core.Wm;

namespace Shubbak.Core.Tests;

/// <summary>
/// Which window a command acts on after focus has just moved.
/// </summary>
/// <remarks>
/// <para>
/// Shubbak decides focus; the desktop follows a moment later, because
/// SetForegroundWindow is asynchronous. Anything that re-reads the desktop to decide
/// what a command meant is therefore reading a stale answer for as long as that takes.
/// </para>
/// <para>
/// The symptom was precise: press a focus key and then a move key quickly, and the
/// window focus had just <i>left</i> was the one that moved. Pressing the same key
/// again worked, which made it look like the windows had been swapped rather than
/// like a race.
/// </para>
/// </remarks>
public sealed class FocusThenActTests
{
    private static WindowManager Create()
    {
        var wm = new WindowManager();

        wm.AddMonitor(TreeBuilder.Monitor(@"\\.\DISPLAY1", x: 0, width: 1920, height: 1080));
        wm.AddMonitor(TreeBuilder.Monitor(@"\\.\DISPLAY2", x: 1920, width: 1920, height: 1080));

        wm.AddWorkspace(new WorkspaceNode("1"), wm.Root.Monitors[0]);
        wm.AddWorkspace(new WorkspaceNode("/"), wm.Root.Monitors[1]);

        wm.ActivateWorkspace(wm.Root.Monitors[0].Workspaces[0]);

        return wm;
    }

    [Fact]
    public void MovingAfterFocusingActsOnTheNewlyFocusedWindow()
    {
        // The reported sequence: two windows side by side, focus moves to the second,
        // and the move must take that one.
        WindowManager wm = Create();

        wm.FocusWorkspace("/");
        WindowNode alreadyThere = wm.Open("firefox that was there");
        WindowNode justMoved = wm.Open("firefox just moved over");

        wm.FocusWindow(justMoved);
        wm.FocusDirection(Direction.Left);

        Assert.Same(alreadyThere, wm.FocusedWindow);

        wm.MoveToWorkspace("1");

        Assert.Equal("1", alreadyThere.Workspace!.Name);
        Assert.Equal("/", justMoved.Workspace!.Name);
    }

    [Fact]
    public void TheFirstPressIsEnough()
    {
        // Pressing it twice was the workaround, and the second press moved a second
        // window - so the workaround made things worse than it appeared.
        WindowManager wm = Create();

        wm.FocusWorkspace("/");
        WindowNode a = wm.Open("a");
        WindowNode b = wm.Open("b");

        wm.FocusWindow(b);
        wm.FocusDirection(Direction.Left);
        wm.MoveToWorkspace("1");

        Assert.Equal("1", a.Workspace!.Name);
        Assert.Equal("/", b.Workspace!.Name);
        Assert.Single(wm.Root.FindWorkspace("1")!.DescendantWindows());
    }

    [Fact]
    public void FocusFollowsTheWindowAcrossAMoveAndBack()
    {
        // The whole sequence from the report, in order.
        WindowManager wm = Create();

        WindowNode first = wm.Open("firefox one");

        wm.FocusWorkspace("/");
        WindowNode second = wm.Open("firefox two");

        // Back to workspace 1, send its window across.
        wm.FocusWorkspace("1");
        wm.FocusWindow(first);
        wm.MoveToWorkspace("/");

        Assert.Equal("/", first.Workspace!.Name);

        // Now on the second monitor, focus the one that was always there.
        wm.FocusWorkspace("/");
        wm.FocusWindow(second);

        Assert.Same(second, wm.FocusedWindow);

        WmResult moved = wm.MoveToWorkspace("1");

        Assert.True(moved.Succeeded, moved.RejectionReason ?? "rejected with no reason");


        Assert.Equal("1", second.Workspace!.Name);
        Assert.Equal("/", first.Workspace!.Name);
    }

    [Fact]
    public void AWindowArrivesOnTheWorkspaceItWasSentTo()
    {
        // The workspace remembers what was last focused on it, so an arriving window
        // can be put beside it. That reference is not cleared when the window leaves,
        // and following it led into a container belonging to whichever workspace the
        // window had moved to - so the arriving window was inserted over there, and
        // the one that was sent appeared not to have moved.
        WindowManager wm = Create();

        WindowNode leaves = wm.Open("was on 1");

        wm.FocusWorkspace("/");
        WindowNode arrives = wm.Open("on the other monitor");

        // Send the first window away, which leaves workspace 1 remembering it.
        wm.FocusWorkspace("1");
        wm.FocusWindow(leaves);
        wm.MoveToWorkspace("/");

        // Now send something back the other way.
        wm.FocusWorkspace("/");
        wm.FocusWindow(arrives);
        wm.MoveToWorkspace("1");

        Assert.Equal("1", arrives.Workspace!.Name);

        Assert.Contains(arrives, wm.Root.FindWorkspace("1")!.DescendantWindows());
        Assert.DoesNotContain(arrives, wm.Root.FindWorkspace("/")!.DescendantWindows());
    }

    [Fact]
    public void TheStaleReferenceIsForgottenRatherThanFollowed()
    {
        WindowManager wm = Create();

        WindowNode leaves = wm.Open("was on 1");
        WorkspaceNode one = wm.Root.FindWorkspace("1")!;

        wm.FocusWindow(leaves);
        Assert.Same(leaves, one.LastFocused);

        wm.MoveToWorkspace("/");

        // Whatever the workspace remembers, it must not name a window that is no
        // longer on it by the time the next one arrives.
        wm.FocusWorkspace("/");
        WindowNode arrives = wm.Open("newcomer");
        wm.FocusWindow(arrives);
        wm.MoveToWorkspace("1");

        Assert.NotSame(leaves, one.LastFocused);
        Assert.Equal("1", arrives.Workspace!.Name);
    }
}
