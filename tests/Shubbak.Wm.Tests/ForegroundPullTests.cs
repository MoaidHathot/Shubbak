namespace Shubbak.Wm.Tests;

/// <summary>
/// Whose foreground a layout pass may take.
/// </summary>
/// <remarks>
/// <para>
/// The layout pass ends by bringing the tree's focused window to the front. Every
/// focus key and workspace switch depends on that step to deliver the keyboard, and
/// it has to work from an unmanaged window too - moving focus out of a dialog is how
/// a dialog is left. But the step ran on every pass, whatever had caused it, and took
/// the foreground from whatever had it. On a desktop still settling after logon the
/// second bar registering its strip a second or two in was enough: the pass that
/// re-laid the windows under it took the keyboard back from the command palette within
/// a second of it opening, every time. It would have done the same to an
/// application's own dialog, the Start menu and an elevated Task Manager.
/// </para>
/// <para>
/// The rule is pure so it can be stated. A pass serving a change of focus may take
/// the foreground from anything. A pass that changes nothing about focus may take it
/// from nothing, from the shell, and from a window of Shubbak's own - never from a
/// window somebody else put in front.
/// </para>
/// </remarks>
public sealed class ForegroundPullTests
{
    private const nint SomebodyElse = 0x100;

    [Fact]
    public void ServingAFocusChangeTakesItFromAnything()
    {
        // The workspace switch or focus key issued from a dialog, the palette, or an
        // elevated window: the user asked for focus to move, so it moves.
        Assert.True(WmDaemon.MayTakeForegroundFrom(SomebodyElse, servingAFocusChange: true, managed: false, shell: false));
    }

    [Fact]
    public void HousekeepingTakesItFromNothing()
    {
        Assert.True(WmDaemon.MayTakeForegroundFrom(0, servingAFocusChange: false, managed: false, shell: false));
    }

    [Fact]
    public void HousekeepingTakesItFromAWindowShubbakManages()
    {
        // The tree and the system disagree about which of our windows is in front,
        // and the tree is right.
        Assert.True(WmDaemon.MayTakeForegroundFrom(SomebodyElse, servingAFocusChange: false, managed: true, shell: false));
    }

    [Fact]
    public void HousekeepingTakesItFromTheShell()
    {
        // The desktop after logon, or after the foreground was released to it. Nothing
        // the user is interacting with lives there, and a tiled window with the
        // keyboard is what a window manager owes them.
        Assert.True(WmDaemon.MayTakeForegroundFrom(SomebodyElse, servingAFocusChange: false, managed: false, shell: true));
    }

    [Fact]
    public void HousekeepingLeavesAWindowSomebodyElsePutInFront()
    {
        // The palette, a dialog, a menu, a flyout, an elevated application: unmanaged,
        // not the shell, and in front because the user put it there or asked for it.
        Assert.False(WmDaemon.MayTakeForegroundFrom(SomebodyElse, servingAFocusChange: false, managed: false, shell: false));
    }
}
