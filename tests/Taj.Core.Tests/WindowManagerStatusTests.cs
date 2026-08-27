using Taj.Core;

namespace Taj.Core.Tests;

/// <summary>
/// What the bar says when the window manager has stopped doing something.
/// </summary>
/// <remarks>
/// <para>
/// Both states are otherwise invisible. Pausing stops windows being arranged;
/// suspending releases the keyboard hook, which is what somebody does before a game.
/// Neither changes anything on screen, so without the bar the only way to discover
/// either is to try something and reason about why it did not work.
/// </para>
/// <para>
/// Suspended is the worse of the two to be uninformed about, because it looks exactly
/// like a crash: the windows stay where they are and no key does anything.
/// </para>
/// </remarks>
public sealed class WindowManagerStatusTests
{
    /// <summary>
    /// Nothing is said when nothing is wrong.
    /// </summary>
    /// <remarks>
    /// Empty, not "running". A template widget hides itself when its result is empty,
    /// so this is what keeps the pill off the bar for the whole time it has nothing to
    /// report - which is nearly always.
    /// </remarks>
    [Fact]
    public void NormallyThereIsNothingToSay()
    {
        Assert.Equal(string.Empty, WindowManagerStatus.Combined(suspended: false, paused: false));
        Assert.Equal(string.Empty, WindowManagerStatus.SuspendedLabel(false));
        Assert.Equal(string.Empty, WindowManagerStatus.PausedLabel(false));
    }

    [Fact]
    public void EachStateHasItsOwnLabel()
    {
        Assert.Equal("suspended", WindowManagerStatus.Combined(suspended: true, paused: false));
        Assert.Equal("paused", WindowManagerStatus.Combined(suspended: false, paused: true));
    }

    /// <summary>
    /// The precedence, which is the only real decision here.
    /// </summary>
    /// <remarks>
    /// Both can hold at once - they are independent toggles - and a bar with room for
    /// one pill has to choose. Suspended wins: not arranging windows is inconvenient,
    /// but a released keyboard is why none of your keys work, and that is what the
    /// person staring at the bar is trying to find out.
    /// </remarks>
    [Fact]
    public void SuspendedWinsWhenBothHold()
    {
        Assert.Equal("suspended", WindowManagerStatus.Combined(suspended: true, paused: true));
    }

    /// <summary>
    /// The separate labels stay independent, so two pills can each be right.
    /// </summary>
    /// <remarks>
    /// A config showing both gives each the click that undoes it. Collapsing them
    /// would make one pill claim a state the other owns.
    /// </remarks>
    [Fact]
    public void TheSeparateLabelsDoNotInterfere()
    {
        Assert.Equal("suspended", WindowManagerStatus.SuspendedLabel(true));
        Assert.Equal("paused", WindowManagerStatus.PausedLabel(true));

        Assert.Equal(string.Empty, WindowManagerStatus.SuspendedLabel(false));
        Assert.Equal(string.Empty, WindowManagerStatus.PausedLabel(false));
    }

    /// <summary>
    /// The labels are what a template renders, so they are pinned rather than assumed.
    /// </summary>
    [Fact]
    public void TheLabelsAreTheOnesTheBarShows()
    {
        Assert.Equal("suspended", WindowManagerStatus.Suspended);
        Assert.Equal("paused", WindowManagerStatus.Paused);
    }
}
