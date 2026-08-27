using Shubbak.Core.Geometry;

namespace Shubbak.Core.Tests;

/// <summary>
/// When a window counts as having moved itself away from where it was put.
/// </summary>
/// <remarks>
/// <para>
/// Applications reposition their own windows. Firefox restores the geometry it
/// remembered from last time a fraction of a second after its window appears - after
/// Shubbak has already placed it - and it announces that only through
/// <c>EVENT_OBJECT_LOCATIONCHANGE</c>, which Shubbak does not subscribe to. So the
/// displacement has to be noticed by looking.
/// </para>
/// <para>
/// The danger in looking is well documented in this codebase, because it was done
/// once and reverted: an exact comparison between where a window is and where it was
/// told to be fails permanently for any application that adjusts its own size. A
/// terminal snapping to whole character cells never lands precisely on its target, so
/// every layout pass decided it had moved and moved it again - and since a focus
/// change runs a layout, that was a visible twitch on every focus change.
/// </para>
/// <para>
/// These tests fix the boundary between the two: far enough to catch a window that
/// has genuinely gone somewhere else, blunt enough that self-adjustment never trips
/// it.
/// </para>
/// </remarks>
public sealed class PlacementDriftTests
{
    /// <summary>The left-hand 4K display, at the virtual-desktop origin.</summary>
    private static readonly Rect Display1 = new(0, 0, 3840, 2160);

    /// <summary>The right-hand 4K display.</summary>
    private static readonly Rect Display2 = new(3840, 0, 3840, 2160);

    /// <summary>A tile on the right-hand display.</summary>
    private static readonly Rect TargetOnDisplay2 = new(3840, 0, 1920, 2160);

    /// <summary>
    /// The reported bug: assigned to a workspace on one monitor, opened on the other.
    /// </summary>
    /// <remarks>
    /// Firefox was closed and reopened. Shubbak assigned it to a workspace on the
    /// second display and placed it there; Firefox then restored its own remembered
    /// position on the first display, over the window already tiled there. Nothing
    /// noticed, and it stayed there until the user pressed wm-redraw.
    /// </remarks>
    [Fact]
    public void AWindowThatWentToAnotherMonitorHasEscaped()
    {
        Rect whereFirefoxPutItself = new(0, 0, 1920, 2160);

        Assert.True(PlacementDrift.HasEscaped(
            whereFirefoxPutItself, TargetOnDisplay2, Display2));
    }

    [Fact]
    public void AWindowStillOnItsOwnMonitorHasNot()
    {
        Assert.False(PlacementDrift.HasEscaped(
            TargetOnDisplay2, TargetOnDisplay2, Display2));
    }

    /// <summary>
    /// The case that made the exact comparison untenable.
    /// </summary>
    /// <remarks>
    /// A terminal rounds its size to whole character cells, so it settles a few pixels
    /// short of the rectangle it was given. That is not displacement, and treating it
    /// as such is what produced a twitch on every focus change.
    /// </remarks>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 0)]
    [InlineData(0, 7)]
    [InlineData(9, 14)]
    [InlineData(19, 19)]
    public void CharacterCellRoundingIsNotEscape(int dx, int dy)
    {
        Rect settled = TargetOnDisplay2 with
        {
            X = TargetOnDisplay2.X + dx,
            Y = TargetOnDisplay2.Y + dy,
        };

        Assert.False(PlacementDrift.HasEscaped(settled, TargetOnDisplay2, Display2));
    }

    /// <summary>
    /// A window that changes only its own size stays put.
    /// </summary>
    /// <remarks>
    /// Position is compared and size is not, deliberately. An application with a fixed
    /// aspect ratio or a character grid decides its own dimensions, and pulling it back
    /// to a size it will immediately reject is the fight this is designed to avoid.
    /// </remarks>
    [Fact]
    public void ResizingItselfIsNotEscape()
    {
        Rect narrower = TargetOnDisplay2 with { Width = 640, Height = 480 };

        Assert.False(PlacementDrift.HasEscaped(narrower, TargetOnDisplay2, Display2));
    }

    /// <summary>
    /// A long way from its tile, but on the right monitor, still counts.
    /// </summary>
    [Theory]
    [InlineData(600, 0)]
    [InlineData(0, 400)]
    [InlineData(-500, -500)]
    public void ALargeMoveOnTheSameMonitorIsEscape(int dx, int dy)
    {
        Rect target = new(3840 + 800, 400, 900, 700);
        Rect moved = target with { X = target.X + dx, Y = target.Y + dy };

        Assert.True(PlacementDrift.HasEscaped(moved, target, Display2));
    }

    /// <summary>The threshold is a boundary, so it is pinned on both sides.</summary>
    [Fact]
    public void TheToleranceIsExclusive()
    {
        Rect target = new(3840 + 800, 400, 900, 700);

        Rect atTolerance = target with { X = target.X + PlacementDrift.ToleranceInPixels };
        Rect pastTolerance = target with { X = target.X + PlacementDrift.ToleranceInPixels + 1 };

        Assert.False(PlacementDrift.HasEscaped(atTolerance, target, Display2));
        Assert.True(PlacementDrift.HasEscaped(pastTolerance, target, Display2));
    }

    /// <summary>
    /// A window straddling the seam belongs to whichever display holds most of it.
    /// </summary>
    /// <remarks>
    /// Judged by the centre rather than by overlap. Any window wide enough to touch two
    /// displays overlaps both, so an overlap test would call it escaped from each of
    /// them and move it for ever.
    ///
    /// The target here is where the window actually is, so the distance rule cannot
    /// fire and the only thing under test is which monitor the window is attributed to.
    /// </remarks>
    [Fact]
    public void AWindowStraddlingTheSeamIsJudgedByItsCentre()
    {
        // Crosses the boundary, but its centre - 4600 - is on display 2.
        Rect straddling = new(3840 - 200, 0, 1920, 2160);

        Assert.False(PlacementDrift.HasEscaped(straddling, straddling, Display2));
        Assert.True(PlacementDrift.HasEscaped(straddling, straddling, Display1));
    }

    /// <summary>
    /// With no monitor known, only the distance test applies.
    /// </summary>
    [Fact]
    public void WithoutAMonitorOnlyDistanceCounts()
    {
        Assert.False(PlacementDrift.HasEscaped(
            TargetOnDisplay2, TargetOnDisplay2, monitor: null));

        Rect farAway = TargetOnDisplay2 with { X = TargetOnDisplay2.X + 1000 };

        Assert.True(PlacementDrift.HasEscaped(farAway, TargetOnDisplay2, monitor: null));
    }

    /// <summary>
    /// A window with no extent is not displaced; it is minimised, closing, or new.
    /// </summary>
    /// <remarks>
    /// Reading it as escape would mean re-placing windows that are in the middle of
    /// disappearing, which is both pointless and a good way to fight the shell over a
    /// window that is going away regardless.
    /// </remarks>
    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(100, 100, 0, 500)]
    [InlineData(100, 100, 500, 0)]
    public void AnEmptyRectangleIsNotEscape(int x, int y, int width, int height)
    {
        Rect empty = new(x, y, width, height);

        Assert.False(PlacementDrift.HasEscaped(empty, TargetOnDisplay2, Display2));
        Assert.False(PlacementDrift.HasEscaped(TargetOnDisplay2, empty, Display2));
    }

    /// <summary>An empty monitor rectangle is treated as not knowing, not as nowhere.</summary>
    [Fact]
    public void AnEmptyMonitorIsIgnoredRatherThanFailed()
    {
        Assert.False(PlacementDrift.HasEscaped(
            TargetOnDisplay2, TargetOnDisplay2, new Rect(0, 0, 0, 0)));
    }
}
