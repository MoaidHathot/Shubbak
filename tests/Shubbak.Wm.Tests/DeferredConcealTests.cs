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

/// <summary>
/// Whether the system's choice of foreground window becomes Shubbak's.
/// </summary>
/// <remarks>
/// <para>
/// Switch to an empty workspace, launch something, and it opened on the workspace you
/// just left - taking the active workspace back with it. An empty workspace has
/// nothing to hold the foreground, so a launcher taking it and handing it back put it
/// on the old window; Shubbak followed, and because focusing a window on a hidden
/// workspace activates that workspace, the entire switch unwound.
/// </para>
/// <para>
/// Measured on a live session, the reversion arrived about four seconds after the
/// keypress. Long enough to have started typing, and far enough from the cause that it
/// reads as the window manager acting on its own.
/// </para>
/// <para>
/// komorebi carries the same bug as issue #1676, closed as a fault in the launcher
/// that happened to trigger it. Any launcher will do.
/// </para>
/// </remarks>
public sealed class ForegroundFollowTests
{
    private static WindowNode WindowOn(WorkspaceNode workspace)
    {
        WindowNode window = new(
            handle: 1,
            new WindowIdentity { ProcessName = "process", ClassName = "Class", Title = "a" });

        workspace.Add(window);
        return window;
    }

    private static (WorkspaceNode Shown, WorkspaceNode Hidden) Setup()
    {
        var bounds = new Rect(0, 0, 1920, 1080);
        var monitor = new MonitorNode("\\\\.\\DISPLAY1", bounds, bounds);

        var shown = new WorkspaceNode("1");
        var hidden = new WorkspaceNode("2");

        monitor.AddWorkspace(shown);
        monitor.AddWorkspace(hidden);
        monitor.ActiveWorkspace = shown;

        return (shown, hidden);
    }

    [Fact]
    public void AWindowTheUserCanSeeIsFollowed()
    {
        (WorkspaceNode shown, _) = Setup();

        Assert.True(WmDaemon.ShouldFollowForeground(WindowOn(shown), concealed: false));
    }

    [Fact]
    public void AWindowOnAHiddenWorkspaceIsStillFollowed()
    {
        // Clicking a taskbar button is how you reach a window when you cannot
        // remember which workspace it is on, and a concealed window keeps its taskbar
        // button so that you can. Following the foreground is what makes that work:
        // focusing the window activates its workspace and reveals it.
        (_, WorkspaceNode hidden) = Setup();

        Assert.True(WmDaemon.ShouldFollowForeground(WindowOn(hidden), concealed: true));
    }

    [Fact]
    public void ConcealmentDoesNotDecideIt()
    {
        // The obvious discriminator, pinned as not being one. SwitchToThisWindow on a
        // concealed window raises the foreground event without uncloaking, so a
        // taskbar activation and a post-conceal fallback are identical at the moment
        // the event arrives. Measured on a live desktop, not reasoned.
        (_, WorkspaceNode hidden) = Setup();

        WindowNode window = WindowOn(hidden);

        Assert.Equal(
            WmDaemon.ShouldFollowForeground(window, concealed: true),
            WmDaemon.ShouldFollowForeground(window, concealed: false));
    }

    [Fact]
    public void NothingIsFollowedWhenThereIsNoWindow()
    {
        // The one case that is not a window Shubbak manages. Everything else is.
        Assert.False(WmDaemon.ShouldFollowForeground(null, concealed: false));
        Assert.False(WmDaemon.ShouldFollowForeground(null, concealed: true));
    }
}

/// <summary>
/// Who is allowed to hold the system foreground while the tree has nothing focused.
/// </summary>
/// <remarks>
/// <para>
/// The empty-workspace bug, fixed at the cause rather than by judging the ambiguous
/// event it produces. Switch to an empty workspace and there is nothing to focus, so
/// the system's foreground stays where it was; a launcher opening and closing hands it
/// straight back, which reads as "go to that window" and undoes the switch.
/// </para>
/// <para>
/// The first version of this rule released the foreground only from a window that was
/// off screen, and a second monitor makes that exemption swallow the bug whole: every
/// monitor displays a workspace at all times, so the window on the other display is on
/// a displayed workspace, is genuinely visible, and is the likeliest thing for Windows
/// to fall back to. Reported as an application launched from an empty workspace on one
/// monitor opening on the other monitor's workspace.
/// </para>
/// </remarks>
public sealed class StaleForegroundTests
{
    private static WindowNode Window(WorkspaceNode workspace, nint handle)
    {
        WindowNode window = new(
            handle,
            new WindowIdentity { ProcessName = "process", ClassName = "Class", Title = "a" });

        workspace.Add(window);
        return window;
    }

    /// <summary>Two monitors, each displaying a workspace, as a desktop always is.</summary>
    private static (WorkspaceNode Empty, WorkspaceNode Other, WorkspaceNode Hidden) Setup()
    {
        var first = new Rect(0, 0, 1920, 1080);
        var second = new Rect(1920, 0, 1920, 1080);

        var left = new MonitorNode("\\\\.\\DISPLAY1", first, first);
        var right = new MonitorNode("\\\\.\\DISPLAY2", second, second);

        // The workspace the user just switched to, with nothing on it to focus.
        var empty = new WorkspaceNode("'");
        var hidden = new WorkspaceNode("2");
        left.AddWorkspace(empty);
        left.AddWorkspace(hidden);
        left.ActiveWorkspace = empty;

        // The other monitor, displaying its workspace as it always is.
        var other = new WorkspaceNode("/");
        right.AddWorkspace(other);
        right.ActiveWorkspace = other;

        return (empty, other, hidden);
    }

    [Fact]
    public void AVisibleWindowOnTheOtherMonitorIsStale()
    {
        // The reported bug. The window is on a displayed workspace and entirely
        // visible, which is exactly why the off-screen test missed it - and exactly
        // why Windows picks it to hand the foreground back to.
        (_, WorkspaceNode other, _) = Setup();

        Assert.True(WmDaemon.IsStaleForeground(Window(other, 1), focused: null));
    }

    [Fact]
    public void AConcealedWindowIsStale()
    {
        // The single-monitor case the first version of the rule did cover. Still does.
        (_, _, WorkspaceNode hidden) = Setup();

        Assert.True(WmDaemon.IsStaleForeground(Window(hidden, 1), focused: null));
    }

    [Fact]
    public void BeingOnScreenDoesNotDecideIt()
    {
        // Pinned, because being on screen is the plausible-sounding test that let the
        // bug back in. The rule is about the tree: nothing focused means no managed
        // window may hold the foreground, wherever it happens to be drawn.
        (_, WorkspaceNode other, WorkspaceNode hidden) = Setup();

        WindowNode visible = Window(other, 1);
        WindowNode offScreen = Window(hidden, 2);

        Assert.True(visible.IsOnADisplayedWorkspace);
        Assert.False(offScreen.IsOnADisplayedWorkspace);

        Assert.Equal(
            WmDaemon.IsStaleForeground(visible, focused: null),
            WmDaemon.IsStaleForeground(offScreen, focused: null));
    }

    [Fact]
    public void TheFocusedWindowIsNeverStale()
    {
        // The window the tree focused is the one that should hold the foreground.
        // Releasing it would be Shubbak taking focus off the user mid-sentence.
        (_, WorkspaceNode other, _) = Setup();

        WindowNode window = Window(other, 1);

        Assert.False(WmDaemon.IsStaleForeground(window, focused: window));
    }

    [Fact]
    public void AnotherWindowIsStaleEvenWhenSomethingIsFocused()
    {
        // Asked after the tick's events are drained: had the user chosen this window,
        // its foreground event would have been followed and it would be the focused
        // one. That it is not is the evidence that nothing put it there on purpose.
        (WorkspaceNode empty, WorkspaceNode other, _) = Setup();

        Assert.True(WmDaemon.IsStaleForeground(Window(other, 1), focused: Window(empty, 2)));
    }

    [Fact]
    public void NothingIsStaleWhenNoManagedWindowHasIt()
    {
        // An unmanaged window or the desktop holding the foreground is not Shubbak's
        // to take away - that is a launcher or a dialog the user is working in.
        Assert.False(WmDaemon.IsStaleForeground(null, focused: null));
    }
}
