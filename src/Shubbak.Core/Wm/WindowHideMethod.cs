namespace Shubbak.Core.Wm;

/// <summary>
/// How windows on inactive workspaces are taken off screen.
/// </summary>
/// <remarks>
/// <para>
/// The choice matters more than it appears. Concealment must be reversible: if Shubbak
/// exits, crashes or is killed, every window it was hiding has to come back. The
/// original implementation could not do this, and windows were stranded off screen with
/// their processes still running, unreachable until the application was restarted.
/// </para>
/// <para>
/// Exposed as configuration because <see cref="Cloak"/> depends on an undocumented
/// shell interface. If that ever changes shape, a user needs a way out that is not a
/// rebuild.
/// </para>
/// </remarks>
public enum WindowHideMethod
{
    /// <summary>
    /// Ask the shell to cloak the window. The default, and the only fully reversible
    /// option.
    /// </summary>
    /// <remarks>
    /// A cloaked window is still visible to <c>IsWindowVisible</c>, so a restarted
    /// Shubbak enumerates it, adopts it and un-cloaks it through the ordinary path.
    /// Nothing has to be remembered across runs for recovery to work. This is what
    /// both GlazeWM and komorebi use.
    /// </remarks>
    Cloak,

    /// <summary>
    /// <c>SW_MINIMIZE</c>. Reversible, documented, but visible to the user.
    /// </summary>
    /// <remarks>
    /// Minimised windows stay in the taskbar, and Shubbak cannot distinguish a window
    /// it minimised from one the user minimised, so switching workspaces frequently
    /// tends to lose that distinction. The honest middle option when
    /// <see cref="Cloak"/> is unavailable and <see cref="Hide"/> is unacceptable.
    /// </remarks>
    Minimise,

    /// <summary>
    /// <c>SW_HIDE</c>. A last resort.
    /// </summary>
    /// <remarks>
    /// A hidden window fails <c>IsWindowVisible</c>, so the window filter rejects it
    /// and a restarted Shubbak cannot see it at all. Recovery is then only possible by
    /// matching it against the recorded session, and if that record is lost the window
    /// is stranded. Some applications - Electron ones especially - also treat
    /// <c>WM_SHOWWINDOW(FALSE)</c> as the user dismissing them and behave oddly
    /// afterwards. Kept because it works where nothing else does.
    /// </remarks>
    Hide,
}
