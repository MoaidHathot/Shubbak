using Shubbak.Core.Wm;

namespace Shubbak.Core.Tests;

/// <summary>
/// How a foreground event is read for a window that arrived under <c>no-focus</c>.
/// </summary>
/// <remarks>
/// The rule put focus back the moment the window arrived and left it at that, and it
/// held about half the time: a freshly launched program takes the foreground itself a
/// beat later, and the window manager followed that as if the user had chosen the
/// window. These are the readings that close the gap.
/// </remarks>
public sealed class NoFocusArrivalTests
{
    [Fact]
    public void AnActivationSoonAfterArrivingIsTheWindowsOwn()
    {
        // Notepad, measured: some 150 ms after it was managed.
        Assert.True(NoFocusArrival.IsOwnActivation(TimeSpan.FromMilliseconds(150), mouseButtonDown: false));
    }

    [Fact]
    public void ALateActivationIsTheUsers()
    {
        // Past the grace, the window is any other window: a foreground event for it is
        // followed, whatever brought it.
        Assert.False(NoFocusArrival.IsOwnActivation(NoFocusArrival.Grace, mouseButtonDown: false));
        Assert.False(NoFocusArrival.IsOwnActivation(TimeSpan.FromSeconds(10), mouseButtonDown: false));
    }

    [Fact]
    public void AClickIsTheUsersHoweverSoonItComes()
    {
        // A button held as the event arrives is a click on the window, and a click is
        // a decision the rule has no business undoing.
        Assert.False(NoFocusArrival.IsOwnActivation(TimeSpan.FromMilliseconds(150), mouseButtonDown: true));
    }

    [Fact]
    public void TheGraceOutlastsALateSecondActivation()
    {
        // Electron shells show a frame, then activate again when the content has
        // loaded; a second was not always enough for the second one.
        Assert.True(NoFocusArrival.Grace > TimeSpan.FromSeconds(1));
        Assert.True(NoFocusArrival.IsOwnActivation(TimeSpan.FromMilliseconds(1200), mouseButtonDown: false));
    }
}
