using Shubbak.Core.Tree;

namespace Shubbak.Core.Wm;

/// <summary>What the desktop's maximise flag asks the tree to do about a window.</summary>
public enum MaximiseTransition
{
    /// <summary>Nothing: the flag and the tree agree, or the window is in a state this does not touch.</summary>
    None,

    /// <summary>The window was maximised outside the tree - Win+Up, a double-clicked title bar - and the tree should say so.</summary>
    Enter,

    /// <summary>The window was un-maximised outside the tree - Win+Down, the restore button - and should go back to what it was.</summary>
    Leave,
}

/// <summary>
/// The rule that keeps <see cref="WindowState.Maximised"/> and the desktop's own
/// maximise flag telling the same story.
/// </summary>
/// <remarks>
/// <para>
/// A native maximise used to be undone on the next layout pass as drift: the tree
/// had a Maximised state that nothing set, and the committer cleared the flag on
/// every window it moved. Win+Up therefore did nothing that lasted. Now the flag is
/// read on the same timer that watches for full-screen, and a window found
/// maximised in a state that is not Maximised enters it, while one found
/// un-maximised in Maximised leaves it for the state it came from - so Win+Up,
/// Win+Down, the title-bar buttons and <c>toggle-maximized</c> are one thing.
/// </para>
/// <para>
/// The grace exists because the two sides move at different speeds. When the tree
/// changes the state, the committer zooms or restores the window on the next pass
/// and Windows applies that a frame later; a look in between would see the flag
/// disagreeing with a state that was set deliberately a moment ago, and undo it. A
/// change the tree made is therefore not second-guessed for a moment afterwards.
/// </para>
/// <para>
/// Pure, so the rule is a table a test can hold to account without a window.
/// </para>
/// </remarks>
public static class NativeMaximise
{
    /// <summary>How long a state change the tree made is trusted over the flag.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(1000);

    /// <param name="state">The tree's state for the window.</param>
    /// <param name="zoomed">Whether Windows says the window is maximised.</param>
    /// <param name="inGrace">Whether the tree changed this window's maximised state within <see cref="Grace"/>.</param>
    public static MaximiseTransition Decide(WindowState state, bool zoomed, bool inGrace)
    {
        if (inGrace) return MaximiseTransition.None;

        return (state, zoomed) switch
        {
            (WindowState.Tiling or WindowState.Floating, true) => MaximiseTransition.Enter,
            (WindowState.Maximised, false) => MaximiseTransition.Leave,
            _ => MaximiseTransition.None,
        };
    }
}
