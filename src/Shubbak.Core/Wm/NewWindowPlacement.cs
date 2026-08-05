namespace Shubbak.Core.Wm;

/// <summary>
/// Which workspace a newly-managed window is placed on.
/// </summary>
/// <remarks>
/// <para>
/// The two answers disagree only on a multi-monitor desktop, and there they disagree
/// often, because Windows reopens most applications wherever they were last rather
/// than wherever the user is now.
/// </para>
/// </remarks>
public enum NewWindowPlacement
{
    /// <summary>
    /// The workspace being looked at.
    /// </summary>
    /// <remarks>
    /// What every other tiling window manager does - i3, sway, komorebi, GlazeWM - and
    /// what someone who has just pressed a launcher key means. A window opening on a
    /// display they are not looking at reads as the window having gone missing.
    /// </remarks>
    FollowFocus,

    /// <summary>
    /// The active workspace of whichever monitor the window opened on.
    /// </summary>
    /// <remarks>
    /// Right for an application that chooses its display deliberately - a presentation
    /// going to the projector, a dashboard restored to the screen it lives on - and
    /// wrong for everything that merely reopened where it last was.
    /// </remarks>
    FollowWindow,
}
