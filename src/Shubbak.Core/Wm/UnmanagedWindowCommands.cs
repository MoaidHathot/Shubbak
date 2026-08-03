namespace Shubbak.Core.Wm;

/// <summary>
/// What a command that targets a window does when the focused window is one Shubbak
/// does not manage.
/// </summary>
/// <remarks>
/// <para>
/// Focus is frequently on something outside Shubbak's care: a dialog, a tray popup, a
/// screenshot overlay, or an application the filter passed over. Shubbak's own idea of
/// the focused window is whatever was focused before, and it has no way to know from
/// the window alone whether the user meant that one.
/// </para>
/// <para>
/// Acting on it anyway is the behaviour this type exists to replace. Pressing the
/// float key over an unmanaged window untiled a different window entirely, and the
/// close key would have closed one - silently, and with no way to tell which.
/// </para>
/// </remarks>
public enum UnmanagedWindowCommands
{
    /// <summary>
    /// Do nothing, and report which window was in front and why it was not eligible.
    /// </summary>
    /// <remarks>
    /// The default. A command that does nothing and says so is recoverable; one that
    /// acts on the wrong window may not be.
    /// </remarks>
    Refuse,

    /// <summary>
    /// Take the window under management first, then run the command against it.
    /// </summary>
    /// <remarks>
    /// For a desktop where most windows should be tiled and the filter is simply
    /// wrong now and again. It overrules the same heuristics a <c>manage</c> rule
    /// does, and the same absolute exclusions still hold - the desktop and the shell
    /// are never adopted, whatever is asked.
    /// </remarks>
    Adopt,
}
