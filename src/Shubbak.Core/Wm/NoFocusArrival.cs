namespace Shubbak.Core.Wm;

/// <summary>
/// What to make of a window taking the foreground shortly after it arrived under a
/// rule that said <c>no-focus</c>.
/// </summary>
/// <remarks>
/// <para>
/// The rule puts focus back the moment the window is managed, and that was the whole
/// of it. Measured live, it held about half the time. A freshly launched program has
/// the right to the foreground and takes it a beat after its window is shown - Notepad
/// did so some 150 ms after being managed - and the window manager follows the
/// system's foreground as a rule, because the foreground moving is how it learns of
/// a click or a taskbar button. So the arrival's own activation was read as the user
/// choosing the window, and the tree followed it; whether the layout pass that
/// re-asserts the tree's focus ran before or after decided which window won.
/// </para>
/// <para>
/// For a window under the rule, the first moments are therefore read differently: a
/// foreground event inside <see cref="Grace"/> of the arrival is the window's own
/// doing and is put back, not followed. A mouse button held as the event arrives is
/// the exception, because that is a click on the window, and a click is the user's
/// decision however soon it comes. The keyboard has no such tell, so Alt+Tab to the
/// window within the grace bounces once; the second attempt holds.
/// </para>
/// </remarks>
public static class NoFocusArrival
{
    /// <summary>How long after arriving a window's own activation is put back.</summary>
    /// <remarks>
    /// Longer than the maximise grace because programs activate late: an Electron shell
    /// shows a frame, then activates again when its content has loaded, and a second is
    /// not always enough for the second one.
    /// </remarks>
    public static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// Whether a foreground event for the window is its own activation, to be put
    /// back, rather than the user's choice, to be followed.
    /// </summary>
    /// <param name="sinceArrival">How long ago the window arrived under the rule.</param>
    /// <param name="mouseButtonDown">Whether a mouse button is held as the event arrives.</param>
    public static bool IsOwnActivation(TimeSpan sinceArrival, bool mouseButtonDown) =>
        sinceArrival < Grace && !mouseButtonDown;
}
