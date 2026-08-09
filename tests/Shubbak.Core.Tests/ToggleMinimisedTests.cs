using Shubbak.Core.Tree;
using Shubbak.Core.Wm;

namespace Shubbak.Core.Tests;

/// <summary>
/// Putting a window away and getting it back with the same key.
/// </summary>
/// <remarks>
/// <para>
/// The obvious implementation cannot undo itself. Minimising moves focus to a
/// neighbour - focus cannot stay on a window that is no longer on screen - so the
/// second press lands on a different window and minimises that one too. Pressing a
/// toggle twice left two windows away and neither back, which was found by pressing
/// it twice.
/// </para>
/// <para>
/// So the command remembers what it put away. The tests below are mostly about when
/// it must forget: a memory that outlives the situation that made it is worse than
/// no memory, because the key then does something entirely unrelated to the window
/// in front of you.
/// </para>
/// </remarks>
public sealed class ToggleMinimisedTests
{
    [Fact]
    public void PressingItTwiceBringsTheSameWindowBack()
    {
        // The whole point. Between the two presses focus has moved to the neighbour,
        // so a version that only looks at the focused window minimises that instead.
        WindowManager wm = WmFixture.Create();
        WindowNode a = wm.Open("a");
        WindowNode b = wm.Open("b");
        wm.FocusWindow(b);

        wm.ToggleMinimised();

        Assert.Equal(WindowState.Minimised, b.State);
        Assert.Same(a, wm.FocusedWindow);

        wm.ToggleMinimised();

        Assert.Equal(WindowState.Tiling, b.State);
        Assert.Equal(WindowState.Tiling, a.State);
    }

    [Fact]
    public void TheRestoredWindowIsFocused()
    {
        // It was brought back to be used. Leaving focus on whatever inherited it makes
        // the press feel like it half worked.
        WindowManager wm = WmFixture.Create();
        WindowNode a = wm.Open("a");
        WindowNode b = wm.Open("b");
        wm.FocusWindow(b);

        wm.ToggleMinimised();
        wm.ToggleMinimised();

        Assert.Same(b, wm.FocusedWindow);
        _ = a;
    }

    [Fact]
    public void AWindowRestoredSomeOtherWayIsForgotten()
    {
        // The taskbar, or the window's own button. The memory is stale, so the next
        // press must mean what it plainly says rather than acting on a window the
        // user has already dealt with.
        WindowManager wm = WmFixture.Create();
        WindowNode a = wm.Open("a");
        WindowNode b = wm.Open("b");
        wm.FocusWindow(b);

        wm.ToggleMinimised();

        // As the daemon does when Windows reports the window restored.
        wm.SetWindowState(b, WindowState.Tiling);
        wm.FocusWindow(a);

        wm.ToggleMinimised();

        Assert.Equal(WindowState.Minimised, a.State);
        Assert.Equal(WindowState.Tiling, b.State);
    }

    [Fact]
    public void AClosedWindowIsForgotten()
    {
        // Minimise, then close it from the taskbar. Offering back a window that no
        // longer exists would reject or, worse, resurrect a detached node.
        WindowManager wm = WmFixture.Create();
        WindowNode a = wm.Open("a");
        WindowNode b = wm.Open("b");
        wm.FocusWindow(b);

        wm.ToggleMinimised();
        wm.UnmanageWindow(b);

        wm.ToggleMinimised();

        Assert.Equal(WindowState.Minimised, a.State);
    }

    [Fact]
    public void MinimisingTheOnlyWindowStillLeavesItRecoverable()
    {
        // Nothing inherits focus, and the existing rule leaves it where it is rather
        // than clearing it - SetWindowState only reassigns focus when it has a
        // successor to give it to. So focus stays on a window that is no longer on
        // screen, which is odd but is the lesser of the two: clearing it strands the
        // keyboard, as the empty-workspace case showed.
        //
        // Either way the key that hid the last window on a workspace has to be the
        // one that brings it back.
        WindowManager wm = WmFixture.Create();
        WindowNode only = wm.Open("only");
        wm.FocusWindow(only);

        wm.ToggleMinimised();

        Assert.Equal(WindowState.Minimised, only.State);

        WmResult result = wm.ToggleMinimised();

        Assert.True(result.Succeeded);
        Assert.Equal(WindowState.Tiling, only.State);
        Assert.Same(only, wm.FocusedWindow);
    }

    [Fact]
    public void AFocusedWindowThatIsAlreadyMinimisedIsSimplyRestored()
    {
        // Reachable when something else minimised it and focus stayed - adoption of an
        // already-minimised window, or a restore that did not settle. The direct
        // reading of the command still has to hold.
        WindowManager wm = WmFixture.Create();
        WindowNode a = wm.Open("a");

        wm.SetWindowState(a, WindowState.Minimised);
        wm.FocusWindow(a);

        wm.ToggleMinimised();

        Assert.Equal(WindowState.Tiling, a.State);
    }

    [Fact]
    public void ItRejectsWhenThereIsNothingToPutAwayOrBringBack()
    {
        WindowManager wm = WmFixture.Create();

        WmResult result = wm.ToggleMinimised();

        Assert.False(result.Succeeded);
        Assert.NotNull(result.RejectionReason);
    }
}
