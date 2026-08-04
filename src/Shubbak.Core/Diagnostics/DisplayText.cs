namespace Shubbak.Core.Diagnostics;

/// <summary>
/// Shortening text for logs, reports and the window tree.
/// </summary>
/// <remarks>
/// A window title can be arbitrarily long, and several of them per line makes a log
/// unreadable in exactly the situation the log exists for. Every caller wants the
/// same thing - the first few characters and a mark saying there was more - so it is
/// written once.
/// </remarks>
public static class DisplayText
{
    /// <summary>
    /// Shortens <paramref name="text"/> to <paramref name="max"/> characters,
    /// ending with an ellipsis when anything was removed.
    /// </summary>
    /// <remarks>
    /// The ellipsis is counted, so the result never exceeds <paramref name="max"/> -
    /// which is the point of asking for a maximum. A single character is the shortest
    /// request that can be honoured, and anything below that returns empty rather than
    /// throwing: this runs while building a diagnostic, and a report that dies because
    /// a width was miscalculated is worse than one with a blank in it.
    /// </remarks>
    public static string Truncate(this string text, int max)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (max <= 0) return string.Empty;
        if (text.Length <= max) return text;
        if (max == 1) return "\u2026";

        return string.Concat(text.AsSpan(0, max - 1), "\u2026");
    }
}
