namespace Dalil.Core;

/// <summary>
/// What the palette is currently listing.
/// </summary>
/// <remarks>
/// One surface with modes rather than one window per kind of thing. Almost all of the
/// cost - the window, the input handling, the matcher, the list - is shared, so a
/// second mode is nearly free while a second window is not. It also means the answer
/// to "how do I pause tiling" is the same gesture as "where did my window go", rather
/// than another keybinding to remember.
/// </remarks>
public enum PaletteMode
{
    /// <summary>Every window on the desktop, managed or not.</summary>
    Windows,

    /// <summary>Every command verb.</summary>
    Commands,

    /// <summary>Every workspace.</summary>
    Workspaces,

    /// <summary>Every layout.</summary>
    Layouts,

    /// <summary>
    /// The keys and prefixes themselves.
    /// </summary>
    /// <remarks>
    /// A mode rather than a separate window, so it is reachable by the same Tab that
    /// reaches everything else - and so someone who lands in it by accident can Tab
    /// straight back out.
    /// </remarks>
    Help,
}

/// <summary>One thing the palette can offer.</summary>
/// <param name="Primary">The text searched and shown first.</param>
/// <param name="Secondary">Context shown beside it: a process, a summary.</param>
/// <param name="Badges">Short state markers, shown at the end of the row.</param>
/// <param name="Command">
/// What to send to the window manager when chosen. Empty means the row cannot be run
/// as it stands, and is offered as text to finish typing instead.
/// </param>
/// <param name="Rank">
/// Higher sorts first among equal matches. Focus recency for windows, so the list is
/// useful before anything is typed.
/// </param>
/// <param name="SwitchesTo">
/// When set, choosing this row changes mode rather than running anything. It is what
/// makes the help list do something when a reader presses Enter on it, instead of
/// being a wall of text that ignores the only key they have tried.
/// </param>
public sealed record PaletteEntry(
    string Primary,
    string Secondary,
    IReadOnlyList<string> Badges,
    string Command,
    long Rank = 0,
    PaletteMode? SwitchesTo = null);

/// <summary>An entry that survived filtering, with where it matched.</summary>
/// <param name="Entry">The underlying entry.</param>
/// <param name="Score">How well it matched.</param>
/// <param name="Positions">Indices in <c>Entry.Primary</c> to highlight.</param>
public sealed record PaletteRow(PaletteEntry Entry, int Score, IReadOnlyList<int> Positions);

/// <summary>
/// The palette's state: what was typed, what matched, and what is selected.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately has no window, no renderer and no pipe. Every question worth getting
/// wrong - what the prefix means, what happens to the selection when the list shrinks
/// under it, which row is highlighted after backspacing - is decided here and tested
/// without anything on screen.
/// </para>
/// <para>
/// The host owns the entries and replaces them when the window manager says something
/// changed; the model owns the query, the mode and the selection.
/// </para>
/// </remarks>
public sealed class PaletteModel
{
    private readonly List<PaletteEntry> _entries = [];
    private readonly List<PaletteRow> _rows = [];
    private string _query = string.Empty;

    /// <summary>The mode prefixes, and what they mean.</summary>
    /// <remarks>
    /// Punctuation rather than words because it is typed constantly and must never
    /// collide with what is being searched for. No window title begins with <c>&gt;</c>.
    /// <para>
    /// Being fast is not the same as being findable, and punctuation is the least
    /// discoverable thing a user interface can have. So these are never only
    /// punctuation: the palette shows them along its bottom edge at all times, Tab
    /// reaches every one of them without knowing any, and <c>?</c> lists the lot.
    /// </para>
    /// </remarks>
    public static IReadOnlyDictionary<char, PaletteMode> Prefixes { get; } =
        new Dictionary<char, PaletteMode>
        {
            ['>'] = PaletteMode.Commands,
            ['#'] = PaletteMode.Workspaces,
            ['~'] = PaletteMode.Layouts,
            ['?'] = PaletteMode.Help,
        };

    /// <summary>The prefix that selects a mode, or nothing for the default.</summary>
    public static char PrefixFor(PaletteMode mode)
    {
        foreach ((char prefix, PaletteMode candidate) in Prefixes)
            if (candidate == mode)
                return prefix;

        return '\0';
    }

    /// <summary>A short human name for a mode, for the hint bar and the search box.</summary>
    public static string NameOf(PaletteMode mode) => mode switch
    {
        PaletteMode.Commands => "commands",
        PaletteMode.Workspaces => "workspaces",
        PaletteMode.Layouts => "layouts",
        PaletteMode.Help => "help",
        _ => "windows",
    };

    /// <summary>What the user typed, prefix included.</summary>
    public string Query => _query;

    /// <summary>The mode the current query selects.</summary>
    public PaletteMode Mode => ModeOf(_query);

    /// <summary>The query with any mode prefix removed.</summary>
    public string Term => TermOf(_query);

    /// <summary>Matching entries, best first.</summary>
    public IReadOnlyList<PaletteRow> Rows => _rows;

    /// <summary>Which row is selected, or -1 when there are none.</summary>
    public int SelectedIndex { get; private set; } = -1;

    /// <summary>The selected row, if any.</summary>
    public PaletteRow? Selected =>
        SelectedIndex >= 0 && SelectedIndex < _rows.Count ? _rows[SelectedIndex] : null;

    /// <summary>Replaces the candidates and re-filters.</summary>
    /// <remarks>
    /// The selection is preserved by identity where it can be. Entries arrive again
    /// whenever the window manager reports a change, and a list that jumped back to
    /// the top each time would be unusable while anything was happening on screen.
    /// </remarks>
    public void SetEntries(IEnumerable<PaletteEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        PaletteEntry? wasSelected = Selected?.Entry;

        _entries.Clear();
        _entries.AddRange(entries);

        Refilter(wasSelected);
    }

    /// <summary>Sets the query, prefix included, and re-filters.</summary>
    public void SetQuery(string query)
    {
        _query = query ?? string.Empty;

        // Deliberately not preserved. Typing is the user narrowing the list, and the
        // whole point of narrowing is that the best answer moves to the top - keeping
        // the old selection would leave them pressing Enter on something that is no
        // longer what they were aiming at.
        Refilter(keep: null);
    }

    /// <summary>Switches mode, keeping whatever has been typed.</summary>
    public void SetMode(PaletteMode mode)
    {
        char prefix = PrefixFor(mode);
        SetQuery(prefix == '\0' ? Term : prefix + Term);
    }

    /// <summary>
    /// Moves the selection, wrapping at both ends.
    /// </summary>
    /// <remarks>
    /// Wrapping because the list is short and reaching the last entry by pressing up
    /// once is worth more than the theoretical confusion of it. Every palette the user
    /// has met behaves this way.
    /// </remarks>
    public void MoveSelection(int delta)
    {
        if (_rows.Count == 0)
        {
            SelectedIndex = -1;
            return;
        }

        int next = (SelectedIndex + delta) % _rows.Count;
        if (next < 0) next += _rows.Count;

        SelectedIndex = next;
    }

    /// <summary>Selects the first or last row outright.</summary>
    public void SelectEdge(bool last) =>
        SelectedIndex = _rows.Count == 0 ? -1 : last ? _rows.Count - 1 : 0;

    /// <summary>The window of rows to draw, given how many fit.</summary>
    /// <remarks>
    /// Scrolling is computed rather than stored, so it cannot disagree with the
    /// selection. The host draws only these, which is what keeps a list of several
    /// hundred windows from costing several hundred text measurements per keystroke -
    /// each one a round trip into GDI.
    /// </remarks>
    public (int First, int Count) VisibleWindow(int capacity)
    {
        if (capacity <= 0 || _rows.Count == 0) return (0, 0);
        if (_rows.Count <= capacity) return (0, _rows.Count);

        int first = Math.Max(0, SelectedIndex - (capacity / 2));
        first = Math.Min(first, _rows.Count - capacity);

        return (first, capacity);
    }

    /// <summary>Which mode a query string selects.</summary>
    public static PaletteMode ModeOf(string query) =>
        !string.IsNullOrEmpty(query) && Prefixes.TryGetValue(query[0], out PaletteMode mode)
            ? mode
            : PaletteMode.Windows;

    /// <summary>A query string with its mode prefix removed.</summary>
    public static string TermOf(string query) =>
        !string.IsNullOrEmpty(query) && Prefixes.ContainsKey(query[0])
            ? query[1..]
            : query ?? string.Empty;

    private void Refilter(PaletteEntry? keep)
    {
        _rows.Clear();

        string term = Term;
        Span<int> positions = stackalloc int[64];

        foreach (PaletteEntry entry in _entries)
        {
            MatchResult best = FuzzyMatcher.Match(term, entry.Primary, positions);
            int matched = best.Matched;

            // The secondary text is searched too, but cannot contribute highlights:
            // finding a window by its process name when the title does not contain
            // the query is worth having, and underlining nothing is better than
            // underlining the wrong characters.
            if (!best.IsMatch)
            {
                MatchResult fallback = FuzzyMatcher.Match(term, entry.Secondary);
                if (!fallback.IsMatch) continue;

                // Scored below any title match, because a title is what the user is
                // picturing when they type.
                _rows.Add(new PaletteRow(entry, fallback.Score / 2, []));
                continue;
            }

            _rows.Add(new PaletteRow(entry, best.Score, positions[..matched].ToArray()));
        }

        _rows.Sort(Compare);

        SelectedIndex = _rows.Count == 0 ? -1 : IndexOf(keep);
    }

    /// <remarks>
    /// Score first, then rank, then text. The last is not cosmetic: without a total
    /// order, two equally good matches can swap places between keystrokes and the row
    /// under the user's finger changes as they type.
    /// </remarks>
    private static int Compare(PaletteRow left, PaletteRow right)
    {
        int byScore = right.Score.CompareTo(left.Score);
        if (byScore != 0) return byScore;

        int byRank = right.Entry.Rank.CompareTo(left.Entry.Rank);
        if (byRank != 0) return byRank;

        return string.Compare(left.Entry.Primary, right.Entry.Primary, StringComparison.OrdinalIgnoreCase);
    }

    private int IndexOf(PaletteEntry? entry)
    {
        if (entry is null) return 0;

        for (int i = 0; i < _rows.Count; i++)
            if (ReferenceEquals(_rows[i].Entry, entry))
                return i;

        return 0;
    }
}
