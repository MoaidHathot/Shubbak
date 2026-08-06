using Shubbak.Core.Geometry;
using Shubbak.Core.Tree;
using Shubbak.Wm;

namespace Shubbak.Wm.Tests;

/// <summary>
/// When a window that is leaving the screen actually leaves it.
/// </summary>
/// <remarks>
/// <para>
/// Everything not on a displayed workspace used to be concealed at the top of the
/// layout pass, before anything else happened. That is right when the pass changes
/// nothing's position - revealing the arriving workspace while the departing one was
/// still up showed both at once for a frame - and wrong when the pass starts a motion.
/// </para>
/// <para>
/// Moving a workspace to the other monitor animates its windows across, and the point
/// of the animation is that they arrive over the windows they are replacing. Hiding
/// those at the start left the destination bare for the whole flight, so the arriving
/// window slid onto empty desktop and the one it was covering had vanished a tenth of
/// a second earlier. Reported from use, and visible in the log as a conceal one
/// millisecond after the keypress against a motion that ended 121 ms later.
/// </para>
/// <para>
/// So the conceal waits for the motion. Waiting introduces the risk these tests are
/// about: an instruction queued a tenth of a second ago, carried out against a desktop
/// that has moved on.
/// </para>
/// </remarks>
public sealed class DeferredConcealTests
{
    private static WindowNode NewWindow() => new(
        handle: 1,
        new WindowIdentity { ProcessName = "process", ClassName = "Class", Title = "a" });

    private static WindowNode WindowOn(WorkspaceNode workspace)
    {
        WindowNode window = NewWindow();
        workspace.Add(window);
        return window;
    }

    private static (MonitorNode Monitor, WorkspaceNode Shown, WorkspaceNode Hidden) Setup()
    {
        var bounds = new Rect(0, 0, 1920, 1080);
        var monitor = new MonitorNode("\\\\.\\DISPLAY1", bounds, bounds);

        var shown = new WorkspaceNode("1");
        var hidden = new WorkspaceNode("2");

        monitor.AddWorkspace(shown);
        monitor.AddWorkspace(hidden);
        monitor.ActiveWorkspace = shown;

        return (monitor, shown, hidden);
    }

    [Fact]
    public void AWindowStillOffScreenIsConcealed()
    {
        (_, _, WorkspaceNode hidden) = Setup();

        Assert.True(WmDaemon.ShouldStillConceal(WindowOn(hidden)));
    }

    [Fact]
    public void AWorkspaceSwitchedBackToDuringTheMotionCancelsIt()
    {
        // The instruction was correct when it was queued and is stale by the time it
        // runs. Carrying it out would hide a workspace the user is now looking at.
        (MonitorNode monitor, WorkspaceNode shown, WorkspaceNode hidden) = Setup();
        WindowNode window = WindowOn(hidden);

        monitor.ActiveWorkspace = hidden;

        Assert.False(WmDaemon.ShouldStillConceal(window));
        _ = shown;
    }

    [Fact]
    public void AWindowReleasedDuringTheMotionIsLeftAlone()
    {
        // Null is what the registry returns for a handle it has stopped tracking.
        // Concealing it anyway would cloak a window nothing is managing, which is the
        // stranded state `shubbak restore` exists to clean up - strictly worse than
        // the flicker the deferral was added to fix.
        Assert.False(WmDaemon.ShouldStillConceal(null));
    }

    [Fact]
    public void AWindowDetachedFromEveryWorkspaceIsLeftAlone()
    {
        // Detached but still in the registry: mid-move between workspaces, where the
        // node has been taken out of one tree and not yet put into another. It has no
        // monitor, so it cannot be said to be off screen.
        var window = NewWindow();

        Assert.False(window.IsOnADisplayedWorkspace);

        // Deliberately asserting the opposite of the line above: "not displayed" and
        // "should be hidden" are different questions, and only the second one may
        // touch a window whose place in the tree is unknown.
        Assert.True(WmDaemon.ShouldStillConceal(window));
    }
}
