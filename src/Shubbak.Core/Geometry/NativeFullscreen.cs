namespace Shubbak.Core.Geometry;

/// <summary>
/// Whether a window has taken itself full-screen.
/// </summary>
/// <remarks>
/// <para>
/// Applications go full-screen without asking and without telling. A browser
/// playing a video, a game in borderless mode, a slideshow: each strips its own
/// frame and resizes its own window to the monitor. Windows announces that through
/// <c>EVENT_OBJECT_LOCATIONCHANGE</c> and nothing else, and Shubbak deliberately
/// does not subscribe to that event - see <c>WinEventSource</c> - so the only way
/// to know is to look at the rectangle.
/// </para>
/// <para>
/// It matters because a focus change re-runs the layout, and a layout pass that
/// does not know the window is full-screen puts it straight back in its tile while
/// the application still believes otherwise. The window is then neither: too small
/// to be full-screen, and drawn without the frame it needs to be a window.
/// </para>
/// <para>
/// The obvious objection to a geometric test is that it also describes Shubbak's
/// own <see cref="Tree.WindowState.MonitorFullscreen"/> exactly, and that was the
/// stated reason for not attempting one (see <c>DisplayPreferences</c>). The answer
/// is that the question is only ever asked of a window Shubbak has <i>not</i> put
/// there: a tiled or floating window, never one already in a full-screen or
/// maximised state of Shubbak's own making. What the test cannot distinguish, it is
/// never shown.
/// </para>
/// </remarks>
public static class NativeFullscreen
{
    /// <summary>
    /// How far short of the monitor's edge a window may fall and still count as
    /// covering it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Small on purpose, and much smaller than <see cref="PlacementDrift"/>'s
    /// tolerance, because the two are answering opposite questions. That one asks
    /// "has this window gone somewhere else", where an application rounding to a
    /// character cell must not count; this one asks "is this window exactly the size
    /// of the display", where anything but a very close match is a normal window and
    /// must be left alone.
    /// </para>
    /// <para>
    /// Two pixels covers a window whose frame lands a pixel off after a DPI
    /// conversion, and nothing else. In particular it is nowhere near large enough to
    /// catch a maximised window on a display with a bar: that one stops at the work
    /// area, which the bar has already taken tens of pixels out of.
    /// </para>
    /// <para>
    /// It does not save a maximised window on a display without one. The compositor
    /// draws a maximised window oversized - its frame is put deliberately off the edge
    /// of the screen - so where the work area is the whole panel, as it is with an
    /// auto-hiding taskbar and nothing docked, the overhang covers the monitor and no
    /// tolerance can tell the two apart. Geometry has run out at that point, and the
    /// caller asks <c>IsZoomed</c> instead.
    /// </para>
    /// </remarks>
    public const int ToleranceInPixels = 2;

    /// <summary>
    /// Whether a window's rectangle covers the whole of its monitor.
    /// </summary>
    /// <param name="window">
    /// The window rectangle as Windows reports it - <c>GetWindowRect</c>, not the
    /// visible frame. A window that has gone full-screen has dropped its shadow, but
    /// the margins measured for it while it was an ordinary window are still cached,
    /// so subtracting them here would shrink the rectangle by a shadow that is no
    /// longer there and the window would read as a few pixels short of the display.
    /// </param>
    /// <param name="monitor">
    /// The monitor's bounds - the whole panel, not the work area. Using the work area
    /// would make every maximised window look full-screen.
    /// </param>
    public static bool CoversMonitor(Rect window, Rect monitor)
    {
        // Nothing to compare. A window with no extent is minimised, being created, or
        // gone; a monitor with none has not been read yet. Neither is full-screen, and
        // guessing would mean handing the display to a window that is disappearing.
        if (window.IsEmpty || monitor.IsEmpty) return false;

        return window.Left <= monitor.Left + ToleranceInPixels
            && window.Top <= monitor.Top + ToleranceInPixels
            && window.Right >= monitor.Right - ToleranceInPixels
            && window.Bottom >= monitor.Bottom - ToleranceInPixels;
    }
}
