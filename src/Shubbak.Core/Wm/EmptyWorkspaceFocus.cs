namespace Shubbak.Core.Wm;

/// <summary>
/// Where the keyboard goes when the workspace being looked at has no window to give
/// it to.
/// </summary>
/// <remarks>
/// <para>
/// The system's foreground has to be somewhere, and wherever it is left is where
/// Windows returns it when whatever takes it next - a launcher, most often - lets go.
/// On a single monitor that hardly matters. On two, the difference is whether the
/// application the launcher started opens on the empty workspace or on the other
/// display.
/// </para>
/// </remarks>
public enum EmptyWorkspaceFocus
{
    /// <summary>
    /// An invisible window of Shubbak's own, on the monitor whose workspace is empty.
    /// </summary>
    /// <remarks>
    /// Windows hands the foreground back to the window that had it before, so a
    /// launcher closing - or the last window on the workspace closing after it -
    /// leaves the keyboard where the user is looking rather than on the other monitor.
    /// </remarks>
    Hold,

    /// <summary>
    /// The desktop.
    /// </summary>
    /// <remarks>
    /// What Shubbak did before it had a window to hold the keyboard with, kept for
    /// anyone who would rather it created none. The desktop is never chosen as a
    /// fallback, so on a multi-monitor desktop a launcher closing sends the keyboard
    /// to whichever application window Windows finds first.
    /// </remarks>
    Desktop,
}
