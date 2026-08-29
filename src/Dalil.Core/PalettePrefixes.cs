namespace Dalil.Core;

/// <summary>
/// Which character selects which mode, and what a query means because of it.
/// </summary>
/// <remarks>
/// <para>
/// A type rather than the static table this replaced, because the table is now the
/// user's to change. Punctuation is the fastest way to switch mode and the least
/// portable thing in the interface: on a German layout <c>~</c> is <c>AltGr+Plus</c>
/// and behaves as a dead key, so the character does not arrive until the next
/// keypress and the mode never changes at all. On a UK layout <c>#</c> and <c>~</c>
/// are both somewhere else again. Somebody on either could not reach half the modes
/// by typing, and nothing on screen would explain why.
/// </para>
/// <para>
/// The defaults are exactly what they were, so nobody who was already happy has to
/// do anything. What changed is that being unhappy is now fixable.
/// </para>
/// <para>
/// Immutable, and built once per configuration load. The lookups run on every
/// keystroke - <see cref="ModeOf"/> is called from the model's refilter - so this is
/// two dictionaries rather than a scan.
/// </para>
/// </remarks>
public sealed class PalettePrefixes
{
    private readonly Dictionary<char, PaletteMode> _byCharacter;
    private readonly Dictionary<PaletteMode, char> _byMode;

    private PalettePrefixes(IEnumerable<KeyValuePair<PaletteMode, char>> table)
    {
        _byCharacter = [];
        _byMode = [];

        foreach ((PaletteMode mode, char prefix) in table)
        {
            // A mode can opt out by asking for nothing, which is how the window list
            // is spelled and how any other mode can be made unreachable by prefix
            // without being removed from Tab.
            if (prefix == '\0') continue;

            // First writer wins. Two modes claiming one character is a configuration
            // mistake rather than a crash, and silently letting the later one steal
            // the key would be the harder of the two to notice.
            if (_byCharacter.ContainsKey(prefix)) continue;

            _byCharacter[prefix] = mode;
            _byMode[mode] = prefix;
        }
    }

    /// <summary>
    /// The prefixes as they have always been.
    /// </summary>
    /// <remarks>
    /// Punctuation, because it is typed constantly and must never collide with what
    /// is being searched for - no window title begins with <c>&gt;</c>.
    /// <para>
    /// Being fast is not the same as being findable, and punctuation is the least
    /// discoverable thing a user interface can have. So these are never only
    /// punctuation: the palette shows them along its bottom edge at all times, Tab
    /// reaches every one of them without knowing any, <c>Ctrl</c> and a digit jumps
    /// straight to one, and <c>?</c> lists the lot.
    /// </para>
    /// </remarks>
    public static PalettePrefixes Default { get; } = new(
    [
        new(PaletteMode.Commands, '>'),
        new(PaletteMode.Workspaces, '#'),
        new(PaletteMode.Layouts, '~'),
        new(PaletteMode.Monitors, '%'),
        new(PaletteMode.Scratchpad, '$'),
        new(PaletteMode.Help, '?'),

        // Chosen because it is the punctuation of negation, and this is the list of
        // windows Shubbak said no to.
        new(PaletteMode.Inspect, '!'),
    ]);

    /// <summary>
    /// The defaults with some replaced.
    /// </summary>
    /// <remarks>
    /// Overlaid rather than replaced wholesale. Somebody remapping the one prefix
    /// their keyboard cannot type should not thereby lose the other six.
    /// <para>
    /// An override is applied before the defaults so that it wins the first-writer
    /// rule above - otherwise moving <c>~</c> onto <c>l</c> would be refused only
    /// when some default already held <c>l</c>, which is not a distinction the user
    /// asked for.
    /// </para>
    /// </remarks>
    public static PalettePrefixes With(IReadOnlyDictionary<PaletteMode, char>? overrides)
    {
        if (overrides is not { Count: > 0 }) return Default;

        List<KeyValuePair<PaletteMode, char>> table = [.. overrides];

        // The modes the user did not mention keep what they had, unless the character
        // has since been taken by an override - in which case the explicit wish wins
        // and the untouched mode simply has no prefix. Tab and Ctrl+digit still reach
        // it, so it is never stranded.
        foreach ((PaletteMode mode, char prefix) in Default._byMode)
            if (!overrides.ContainsKey(mode))
                table.Add(new(mode, prefix));

        return new PalettePrefixes(table);
    }

    /// <summary>Every prefix in play, keyed by the character that selects it.</summary>
    public IReadOnlyDictionary<char, PaletteMode> ByCharacter => _byCharacter;

    /// <summary>Whether a character selects a mode.</summary>
    public bool IsPrefix(char value) => _byCharacter.ContainsKey(value);

    /// <summary>The prefix that selects a mode, or nothing for the default.</summary>
    public char PrefixFor(PaletteMode mode) =>
        _byMode.TryGetValue(mode, out char prefix) ? prefix : '\0';

    /// <summary>Which mode a query string selects.</summary>
    public PaletteMode ModeOf(string? query) =>
        !string.IsNullOrEmpty(query) && _byCharacter.TryGetValue(query[0], out PaletteMode mode)
            ? mode
            : PaletteMode.Windows;

    /// <summary>A query string with its mode prefix removed.</summary>
    public string TermOf(string? query) =>
        !string.IsNullOrEmpty(query) && _byCharacter.ContainsKey(query[0])
            ? query[1..]
            : query ?? string.Empty;

    /// <summary>How many characters of a query are its prefix: one, or none.</summary>
    public int PrefixLengthOf(string? query) =>
        !string.IsNullOrEmpty(query) && _byCharacter.ContainsKey(query[0]) ? 1 : 0;
}
