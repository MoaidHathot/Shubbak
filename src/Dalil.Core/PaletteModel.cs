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
    /// The displays, and which workspace each is showing.
    /// </summary>
    /// <remarks>
    /// The window manager has always known this and nothing has ever shown it. On one
    /// monitor it is uninteresting; on two it answers "which screen is that on", which
    /// is half of "where did it go".
    /// </remarks>
    Monitors,

    /// <summary>
    /// Windows put away in a scratchpad.
    /// </summary>
    /// <remarks>
    /// Its own mode rather than a filter on the window list, because a scratchpad
    /// window is retrieved by naming its slot rather than by being focused - and
    /// because deliberately stashed windows are the ones most likely to be forgotten
    /// about entirely.
    /// </remarks>
    Scratchpad,

    /// <summary>
    /// The keys and prefixes themselves.
    /// </summary>
    /// <remarks>
    /// A mode rather than a separate window, so it is reachable by the same Tab that
    /// reaches everything else - and so someone who lands in it by accident can Tab
    /// straight back out.
    /// </remarks>
    Help,

    /// <summary>
    /// The windows Shubbak is not managing, and why not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A filter rather than a second window list, which is what earns it a prefix of
    /// its own. The window list shows everything and is where you go to find a window;
    /// this shows only what has been passed over and is where you go when a window is
    /// on screen and refusing to tile.
    /// </para>
    /// <para>
    /// Deliberately ignores <c>show-unmanaged</c>. That setting keeps unmanaged windows
    /// out of the ordinary list, which is a reasonable thing to want and would make
    /// this mode permanently empty - a mode that exists to show what is excluded
    /// cannot honour a setting that excludes it.
    /// </para>
    /// </remarks>
    Inspect,
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
/// <param name="Actions">
/// What else can be done to this row besides choosing it. Empty for rows that are
/// only ever chosen - a command, a layout, a line of help.
/// </param>
/// <param name="Explains">
/// When set, choosing this row asks the window manager to describe that window rather
/// than doing anything to it. The answer has to be fetched, so the palette stays open
/// until it arrives.
/// </param>
/// <param name="Expands">
/// The row's full text, when the row is showing a clipped version of it. Choosing such
/// a row opens the whole thing as a list of its own, and Ctrl+C copies it.
/// <para>
/// A row is one line, drawn once, ellipsised where it runs out of width - which is
/// fine for a title and not fine for a path, a regular expression, or the sentence
/// explaining that a window cannot be moved and what to do about it. Those are
/// precisely the values somebody opened a report to read.
/// </para>
/// </param>
public sealed record PaletteEntry(
    string Primary,
    string Secondary,
    IReadOnlyList<string> Badges,
    string Command,
    long Rank = 0,
    PaletteMode? SwitchesTo = null,
    IReadOnlyList<PaletteAction>? Actions = null,
    long? Explains = null,
    string? Expands = null);

/// <summary>
/// What the window manager is currently doing, beyond the lists themselves.
/// </summary>
/// <remarks>
/// Both of these make the window manager look broken when they are set and nothing
/// says so. A paused one has stopped arranging windows; a swallowing binding mode has
/// made the keyboard inert. Either way the palette is where somebody goes to find out
/// what is wrong, so it is the last place that should stay quiet about it.
/// </remarks>
/// <param name="Paused">Whether tiling is suspended.</param>
/// <param name="BindingMode">The active binding mode, or null for the default.</param>
public readonly record struct WmStatus(bool Paused, string? BindingMode)
{
    public static WmStatus Unknown => default;
}

/// <summary>An entry that survived filtering, with where it matched.</summary>
/// <param name="Entry">The underlying entry.</param>
/// <param name="Score">How well it matched.</param>
/// <param name="Positions">Indices in <c>Entry.Primary</c> to highlight.</param>
public sealed record PaletteRow(PaletteEntry Entry, int Score, IReadOnlyList<int> Positions);

/// <summary>
/// Supplies rows derived from the query itself rather than filtered from a list.
/// </summary>
/// <remarks>
/// The typed text in commands mode is not something to search for - it is the thing
/// to run. Rows from here are not matched or scored, because they already are the
/// query, and they sort above everything.
/// </remarks>
/// <param name="mode">Which mode the palette is in.</param>
/// <param name="term">What has been typed, with the mode prefix removed.</param>
public delegate IReadOnlyList<PaletteEntry> QueryAugmenter(PaletteMode mode, string term);

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

    /// <summary>
    /// Supplies rows derived from the query, shown above the filtered list.
    /// </summary>
    /// <remarks>
    /// Set once by the host. Null means the palette only ever offers things from its
    /// entry list, which is what every mode but commands wants.
    /// </remarks>
    public QueryAugmenter? Augmenter { get; set; }

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
            ['%'] = PaletteMode.Monitors,
            ['$'] = PaletteMode.Scratchpad,
            ['?'] = PaletteMode.Help,

            // Chosen because it is the punctuation of negation, and this is the list
            // of windows Shubbak said no to.
            ['!'] = PaletteMode.Inspect,
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
        PaletteMode.Monitors => "monitors",
        PaletteMode.Scratchpad => "scratchpad",
        PaletteMode.Help => "help",
        PaletteMode.Inspect => "inspect",
        _ => "windows",
    };

    /// <summary>What the user typed, prefix included.</summary>
    public string Query => _query;

    /// <summary>
    /// The mode a name refers to, or null when it names none.
    /// </summary>
    /// <remarks>
    /// Derived from <see cref="NameOf"/> rather than written out again, so a mode
    /// added to the enum is addressable by name the moment it is named. The list this
    /// replaced was maintained by hand and had already fallen behind: it knew about
    /// commands, workspaces and layouts, and silently opened the window list for
    /// anything else - including modes that existed and were simply missing from it.
    /// <para>
    /// The singular is accepted alongside the plural because a keybinding that says
    /// <c>signal "palette" "workspace"</c> is expressing an intention that is not in
    /// any doubt, and refusing it would be pedantry with no upside.
    /// </para>
    /// </remarks>
    public static PaletteMode? ModeNamed(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        string wanted = name.Trim();

        foreach (PaletteMode mode in Enum.GetValues<PaletteMode>())
        {
            string proper = NameOf(mode);

            if (string.Equals(proper, wanted, StringComparison.OrdinalIgnoreCase) ||
                (proper.EndsWith('s') &&
                 string.Equals(proper[..^1], wanted, StringComparison.OrdinalIgnoreCase)))
            {
                return mode;
            }
        }

        return null;
    }

    /// <summary>The mode the current query selects.</summary>
    public PaletteMode Mode => ModeOf(_query);

    /// <summary>The query with any mode prefix removed.</summary>
    public string Term => TermOf(_query);

    /// <summary>Matching entries, best first.</summary>
    public IReadOnlyList<PaletteRow> Rows => _rows;

    /// <summary>
    /// What the window manager is currently doing, for the renderer to report.
    /// </summary>
    /// <remarks>
    /// Held on the model rather than passed to the renderer, because it changes with
    /// the lists and on the same refresh - a status read at one moment and rows read
    /// at another would eventually disagree in a way nobody could reproduce.
    /// </remarks>
    public WmStatus Status { get; private set; } = WmStatus.Unknown;

    /// <summary>Records what the window manager last said about itself.</summary>
    public void SetStatus(WmStatus status) => Status = status;

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

    /// <summary>Selects one row by index, ignoring anything out of range.</summary>
    /// <remarks>
    /// For the mouse, which names a row directly rather than moving by steps. Out of
    /// range is ignored rather than clamped: a click that misses should do nothing,
    /// not act on whichever row happens to be nearest.
    /// </remarks>
    public void SelectAt(int index)
    {
        if (index >= 0 && index < _rows.Count) SelectedIndex = index;
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

        // Prepended after sorting rather than sorted in. These rows are not matches
        // and have no score to compare; giving them an enormous one and hoping would
        // work until something else legitimately scored higher.
        if (Augmenter?.Invoke(Mode, term) is { Count: > 0 } derived)
            _rows.InsertRange(0, derived.Select(e => new PaletteRow(e, int.MaxValue, [])));

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
