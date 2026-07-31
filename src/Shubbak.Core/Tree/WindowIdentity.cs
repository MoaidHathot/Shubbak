namespace Shubbak.Core.Tree;

/// <summary>
/// The attributes of a window that rules can match on, and that the bar displays.
/// </summary>
/// <remarks>
/// <para>
/// A record so that a title change produces a new value rather than a hidden
/// mutation - which makes "did anything actually change?" a cheap equality check.
/// That matters because S4 measured <c>EVENT_OBJECT_NAMECHANGE</c> firing far more
/// often than the title genuinely changes, and because the bar must debounce
/// title updates rather than redraw on every event
/// (docs/adr/0001-language-choice.md, S4 finding 3).
/// </para>
/// <para>
/// <see cref="ProcessName"/> and <see cref="ClassName"/> are stable for a window's
/// lifetime; <see cref="Title"/> is not.
/// </para>
/// </remarks>
public sealed record WindowIdentity
{
    public required string ProcessName { get; init; }

    /// <summary>Full path to the executable, when obtainable.</summary>
    public string? ProcessPath { get; init; }

    public required string ClassName { get; init; }

    public required string Title { get; init; }

    public int ProcessId { get; init; }

    /// <summary>
    /// True when the owning process runs at a higher integrity level than Shubbak.
    /// Such windows cannot be manipulated unless Shubbak itself is elevated, so the
    /// WM surfaces this rather than failing silently.
    /// </summary>
    public bool IsElevated { get; init; }

    public WindowIdentity WithTitle(string title) =>
        string.Equals(Title, title, StringComparison.Ordinal) ? this : this with { Title = title };

    public override string ToString() => $"{ProcessName}/{ClassName} \"{Title}\"";
}
