namespace Shubbak.Core.Geometry;

/// <summary>
/// Whether a window has wandered away from where the window manager put it.
/// </summary>
/// <remarks>
/// <para>
/// Applications move their own windows. A browser restoring the geometry it
/// remembered from last time does it a fraction of a second after its window first
/// appears - after the window manager has already placed it. Windows announces that
/// through <c>EVENT_OBJECT_LOCATIONCHANGE</c>, which Shubbak deliberately does not
/// subscribe to, for reasons written up in <c>WinEventSource</c>: the callbacks
/// arrive on the same message queue the pump waits on, so every frame the animation
/// path committed came straight back as a wake-up, and a single dragged window
/// produced 122 of them a second.
/// </para>
/// <para>
/// So the displacement is noticed by looking rather than by being told. That only
/// works if the question is asked coarsely. An earlier attempt compared the window's
/// real rectangle against its target exactly, and had to be reverted: a terminal that
/// snaps to whole character cells never lands precisely where it was put, so the
/// comparison failed on every pass and the window was re-placed every time focus
/// moved. The fix and the fault were the same code.
/// </para>
/// <para>
/// This is therefore deliberately blunt. It answers "has this window gone somewhere
/// else", not "is this window exactly where I asked". A window that is a few pixels
/// out has not gone anywhere; a window on the wrong monitor has.
/// </para>
/// </remarks>
public static class PlacementDrift
{
    /// <summary>
    /// How far a window may sit from its target before it counts as displaced.
    /// </summary>
    /// <remarks>
    /// Chosen to clear the largest thing that legitimately moves a window a little:
    /// a terminal rounding to a character cell, which is tens of pixels at most on a
    /// large font. Small enough that a window landing here is visibly wrong, and far
    /// enough above the noise that ordinary snapping never reaches it.
    /// </remarks>
    public const int ToleranceInPixels = 120;

    /// <summary>
    /// Whether a window has materially left the placement it was given.
    /// </summary>
    /// <param name="actual">Where the window is now.</param>
    /// <param name="target">Where it was last told to be.</param>
    /// <param name="monitor">
    /// The bounds of the monitor the target belongs to, when known. This is the test
    /// that matters: it is the case the whole thing exists for, it cannot be tripped
    /// by an application rounding its own size, and it is the one a user notices,
    /// because the window is not on the screen they are looking at.
    /// </param>
    public static bool HasEscaped(Rect actual, Rect target, Rect? monitor)
    {
        // Nothing useful to compare. A window with no extent is minimised, in the
        // middle of being created, or gone; none of those are displacement, and
        // guessing would mean moving windows that are busy disappearing.
        if (actual.IsEmpty || target.IsEmpty) return false;

        // On another display entirely. Judged by the centre rather than by overlap,
        // so a window straddling a seam is attributed to wherever most of it is
        // instead of counting as escaped from both.
        if (monitor is { IsEmpty: false } bounds && !bounds.Contains(actual.CenterX, actual.CenterY))
            return true;

        // Same monitor, but a long way from where it was put. Position only: a
        // window that keeps its corner and adjusts its own size is doing what
        // applications with fixed aspect ratios or character grids do, and pulling
        // it back would restart the fight that made the exact comparison untenable.
        return Math.Abs(actual.X - target.X) > ToleranceInPixels
            || Math.Abs(actual.Y - target.Y) > ToleranceInPixels;
    }
}
