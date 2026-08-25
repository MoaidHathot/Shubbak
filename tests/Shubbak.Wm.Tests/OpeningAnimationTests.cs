using Shubbak.Core.Geometry;
using Shubbak.Wm;

namespace Shubbak.Wm.Tests;

/// <summary>
/// Which newly adopted windows are animated into their first rectangle when
/// <c>animate-new-windows</c> is on.
/// </summary>
/// <remarks>
/// <para>
/// A new window is placed rather than animated by default, because the rectangle it
/// would travel from is whatever size the application opened at and was never part of
/// the arrangement. <c>animate-new-windows</c> asks for the motion anyway, which is
/// reasonable over a short distance - it reads as the window arriving in its tile.
/// </para>
/// <para>
/// It was applied unconditionally, and the distance is not always short. Firefox was
/// killed with two windows on two monitors and restarted; it reopened both at its own
/// remembered positions, one per display, and both were adopted onto the same
/// workspace within half a second of each other. The second was therefore animated
/// from full-screen on one monitor into half of the other. The user saw an empty tile
/// beside a half-width window, then a window flying in from the next monitor - two
/// symptoms of one motion, 120 ms apart.
/// </para>
/// <para>
/// These tests fix the boundary: the opening animation survives on the monitor it was
/// added for and is withheld across monitors.
/// </para>
/// </remarks>
public sealed class OpeningAnimationTests
{
    /// <summary>The left-hand 4K display, at the virtual-desktop origin.</summary>
    private static readonly Rect Display1 = new(0, 0, 3840, 2160);

    /// <summary>The right-hand 4K display, as the reported setup had it.</summary>
    private static readonly Rect Display2 = new(3840, 0, 3840, 2160);

    [Fact]
    public void AWindowOpeningOnTheMonitorItIsTiledOntoIsAnimated()
    {
        // The case the setting was added for: Win+E on the display you are working on.
        // The travel is short and reads as the window arriving in its tile.
        Assert.True(WmDaemon.OpeningIsWorthAnimating(new Rect(400, 300, 1200, 900), Display1));
    }

    [Fact]
    public void AWindowOpeningOnAnotherMonitorIsPlaced()
    {
        // The reported fault. Firefox reopened this window full-screen on the second
        // display; Shubbak adopted it onto a workspace belonging to the first. Nothing
        // about a 3840-pixel flight describes what happened, so it is placed instead.
        Assert.False(WmDaemon.OpeningIsWorthAnimating(Display2, Display1));
    }

    [Fact]
    public void TheJudgementIsSymmetricAcrossTheMonitors()
    {
        // The same window manager runs both displays, and neither is special. Stated
        // because the primary monitor sits at the virtual-desktop origin and so is the
        // one an off-by-one in the containment test would happen to get right.
        Assert.False(WmDaemon.OpeningIsWorthAnimating(Display1, Display2));
        Assert.True(WmDaemon.OpeningIsWorthAnimating(new Rect(4240, 300, 1200, 900), Display2));
    }

    [Fact]
    public void AWindowStraddlingTheEdgeIsJudgedByWhereMostOfItIs()
    {
        // Centre rather than containment, deliberately. A window the user has dragged
        // across a monitor boundary still opened on the display it is mostly on, and
        // demanding full containment would withhold the animation from a window that
        // has barely moved.
        //
        // 3440..4640 straddles the seam at 3840, centred at 4040 - on the second
        // display.
        var straddling = new Rect(3440, 300, 1200, 900);

        Assert.False(WmDaemon.OpeningIsWorthAnimating(straddling, Display1));
        Assert.True(WmDaemon.OpeningIsWorthAnimating(straddling, Display2));
    }

    [Fact]
    public void AWindowWithNoRectangleYetIsPlaced()
    {
        // An empty origin is not a position. Animating from it starts the window at
        // the top-left of the virtual desktop, which is the same long flight the
        // monitor test exists to prevent - arrived at by a different route, and on a
        // window that has not even been drawn yet.
        Assert.False(WmDaemon.OpeningIsWorthAnimating(Rect.Empty, Display1));
        Assert.False(WmDaemon.OpeningIsWorthAnimating(Rect.Empty, null));
        Assert.False(WmDaemon.OpeningIsWorthAnimating(new Rect(100, 100, 0, 0), Display1));
    }

    [Fact]
    public void AnUnknownMonitorWithholdsNothing()
    {
        // The node is not under a monitor, which the tree should not permit. Whatever
        // has gone wrong there, it is not evidence about this window, and suppressing
        // a configured animation on the strength of a fact we do not have would make
        // the setting look intermittent.
        Assert.True(WmDaemon.OpeningIsWorthAnimating(new Rect(400, 300, 1200, 900), null));
        Assert.True(WmDaemon.OpeningIsWorthAnimating(new Rect(400, 300, 1200, 900), Rect.Empty));
    }

    [Fact]
    public void AMonitorAtNegativeCoordinatesIsHandled()
    {
        // A display left of or above the primary one occupies negative virtual-desktop
        // coordinates. Nothing here may assume the origin is the top-left of anything.
        var leftOfPrimary = new Rect(-3840, 0, 3840, 2160);

        Assert.True(WmDaemon.OpeningIsWorthAnimating(new Rect(-3440, 300, 1200, 900), leftOfPrimary));
        Assert.False(WmDaemon.OpeningIsWorthAnimating(new Rect(400, 300, 1200, 900), leftOfPrimary));
    }
}
