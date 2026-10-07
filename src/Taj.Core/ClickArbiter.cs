namespace Taj.Core;

/// <summary>
/// Decides what a press of the left button means on a widget that may also take a
/// double click.
/// </summary>
/// <remarks>
/// <para>
/// Windows reports a double click as a second press, and a widget with both
/// <c>on-click</c> and <c>on-double-click</c> would otherwise run the single click
/// and then the double: open the mixer, then also mute. The usual answer, and the
/// one every desktop gives, is to hold the single click for the double-click time
/// and let it go only if no second press arrives. A widget with no double click is
/// not made to wait: its click runs at once, as it always did, since there is nothing
/// a second press could mean that the first did not.
/// </para>
/// <para>
/// The timing itself is the host's - Windows knows the double-click time and tells
/// the window when a second press was one - so this holds no clock. It holds one
/// thing: the click that is waiting. Pulled out of the window so the three outcomes
/// can be checked without one.
/// </para>
/// </remarks>
public sealed class ClickArbiter
{
    private string? _held;

    /// <summary>Whether a single click is waiting for the double-click time to pass.</summary>
    public bool IsHolding => _held is not null;

    /// <summary>
    /// The left button went down over a widget. Returns the command to run now, or
    /// null - either because there is none, or because the click is being held; the
    /// host checks <see cref="IsHolding"/> and starts the double-click timer if so.
    /// </summary>
    /// <param name="click">The widget's <c>on-click</c>, if any.</param>
    /// <param name="doubleClick">The widget's <c>on-double-click</c>, if any.</param>
    public string? Press(string? click, string? doubleClick)
    {
        _held = null;

        if (string.IsNullOrEmpty(click)) return null;
        if (string.IsNullOrEmpty(doubleClick)) return click;

        _held = click;
        return null;
    }

    /// <summary>
    /// Windows reported the press as the second of a double click. The held click is
    /// dropped. Returns the double-click command or, for a widget without one, the
    /// click itself: the second press was an ordinary press and runs as one, which is
    /// what two quick clicks on a workspace have always done.
    /// </summary>
    public string? DoubleClick(string? click, string? doubleClick)
    {
        _held = null;

        if (!string.IsNullOrEmpty(doubleClick)) return doubleClick;

        return string.IsNullOrEmpty(click) ? null : click;
    }

    /// <summary>The double-click time passed with no second press: the held click, if any.</summary>
    public string? Elapsed()
    {
        string? held = _held;
        _held = null;
        return held;
    }
}
