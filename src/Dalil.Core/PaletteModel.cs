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

    /// <summary>
    /// The displays, and which workspace each is showing.
    /// </summary>
    /// <remarks>
    /// The window manager has always known this and nothing has ever shown it. On one
    /// monitor it is uninteresting; on two it answers "which screen is that on", which
    /// is half of "where did it go".
    /// </remarks>
    Monitors,

    /// <summary>Every layout.</summary>
    Layouts,

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

    /// <summary>
    /// The keys and prefixes themselves.
    /// </summary>
    /// <remarks>
    /// A mode rather than a separate window, so it is reachable by the same keys that
    /// reach everything else - and so someone who lands in it by accident can get
    /// straight back out.
    /// <para>
    /// Last in the enum, and last on purpose. The declaration order is the order the
    /// hint bar and the jump keys use, and help is a destination somebody goes to
    /// deliberately rather than somewhere they should have to pass through.
    /// </para>
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
/// <param name="Actions">
/// What else can be done to this row besides choosing it, when the list is already
/// built. Empty for rows that are only ever chosen - a command, a layout, a line of
/// help - and for the rows that build theirs on demand; see <paramref name="ActionsFactory"/>.
/// </param>
/// <param name="ActionsFactory">
/// How to build the list, for rows whose actions are expensive and rarely looked at.
/// <para>
/// Every window row carries a dozen actions, two of which are pickers with a row per
/// workspace and one of which composes a fragment of KDL - and on a desktop with two
/// hundred windows and nineteen workspaces that is several thousand records built on
/// every refresh, to answer a question about exactly one of them. The list is only
/// ever read for the selected row: by Ctrl+Enter, by a chord, and by the hint bar
/// asking whether to advertise Ctrl+Enter at all.
/// </para>
/// <para>
/// A row with a factory is treated as having actions without the factory being called,
/// which is what keeps that hint bar check free.
/// </para>
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
/// <param name="Chord">
/// The key that acts on this row directly, when it has one.
/// <para>
/// Carried rather than left in <see cref="Badges"/>, where it was only a caption. A
/// row that advertises a key has to be findable by that key, or the badge is a promise
/// nothing keeps.
/// </para>
/// </param>
/// <param name="Target">
/// The command that puts focus on whatever this row is about, for rows that are about
/// a window.
/// <para>
/// Carried explicitly rather than recovered from <see cref="Command"/> by parsing a
/// handle back out of it. That worked and was the kind of thing that breaks quietly
/// the day the command format changes - and it could never have worked for a stashed
/// window, whose command names a scratchpad slot rather than a handle. It is also what
/// makes a row markable: acting on several rows at once means knowing how to aim at
/// each of them, one at a time, in one message.
/// </para>
/// </param>
/// <param name="IconHandle">
/// The window this row is about, for fetching its application icon. Null for rows that
/// are not windows, which then draw no icon and take no space for one.
/// </param>
/// <param name="Destructive">
/// Whether choosing this costs something that cannot be undone. Drawn differently, and
/// confirmed before it happens.
/// </param>
/// <param name="Unavailable">
/// Whether this cannot do anything useful right now - a command that does not apply in
/// the state the window manager is currently in. Still listed, because hiding it would
/// leave somebody searching for a verb the palette denies exists.
/// </param>
/// <param name="Prompts">
/// Whether Enter should open this row's own list rather than doing nothing.
/// <para>
/// A row with no command normally cannot be run, and at the top level its list is what
/// Ctrl+Enter is for - Enter has to keep meaning "go to this window". This is the
/// exception: a row that exists in order to ask a question has nothing else Enter could
/// mean, and requiring Ctrl+Enter to reach the question would hide the answer behind a
/// key nobody has a reason to try.
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
    string? Expands = null,
    string? Chord = null,
    string? Target = null,
    long? IconHandle = null,
    bool Destructive = false,
    bool Unavailable = false,
    Func<IReadOnlyList<PaletteAction>>? ActionsFactory = null,
    bool Prompts = false)
{
    /// <summary>Whether there is anything Ctrl+Enter could show, without working out what.</summary>
    public bool HasActions => Actions is { Count: > 0 } || ActionsFactory is not null;

    /// <summary>
    /// What Ctrl+Enter would show.
    /// </summary>
    /// <remarks>
    /// Built on the spot for a row that defers them. Not memoised: it is called when a
    /// person presses a key, the result is pushed onto the frame stack and read from
    /// there, and caching it on the entry would keep a list of actions alive for every
    /// window that had ever been selected - which is the leak this was meant to avoid,
    /// arrived at from the other direction.
    /// </remarks>
    public IReadOnlyList<PaletteAction> ResolveActions() =>
        Actions ?? ActionsFactory?.Invoke() ?? [];
}

/// <summary>
/// What the window manager is currently doing, beyond the lists themselves.
/// </summary>
/// <remarks>
/// All of these make the window manager look broken when they are set and nothing
/// says so. A paused one has stopped arranging windows; a swallowing binding mode has
/// made the keyboard inert; a suspended one has let go of the keyboard entirely; and
/// one that cannot be reached at all is indistinguishable from one that is merely
/// slow. Either way the palette is where somebody goes to find out what is wrong, so
/// it is the last place that should stay quiet about it.
/// </remarks>
/// <param name="Paused">Whether tiling is suspended.</param>
/// <param name="BindingMode">The active binding mode, or null for the default.</param>
/// <param name="Suspended">Whether the window manager has let go of the keyboard.</param>
/// <param name="Connected">
/// Whether the last read reached the window manager at all. False is not the same as
/// "nothing to show": a dead daemon and a slow one looked identical before this
/// existed, and the empty list confidently blamed the wrong one.
/// </param>
public readonly record struct WmStatus(
    bool Paused,
    string? BindingMode,
    bool Suspended = false,
    bool Connected = true)
{
    /// <summary>Before anything has been read, which is not the same as being offline.</summary>
    public static WmStatus Unknown => new(false, null, false, Connected: true);

    /// <summary>The window manager could not be reached.</summary>
    public static WmStatus Offline => new(false, null, false, Connected: false);
}

/// <summary>An entry that survived filtering, with where it matched.</summary>
/// <param name="Entry">The underlying entry.</param>
/// <param name="Score">How well it matched.</param>
/// <param name="Positions">Indices in <c>Entry.Primary</c> to highlight.</param>
/// <param name="SecondaryPositions">
/// Indices in <c>Entry.Secondary</c> to highlight, when that is where the match was.
/// <para>
/// Carried because finding a window by its application - "code", "chrome" - is most of
/// what the dim half of a row is for, and until now a row matched that way appeared in
/// the list with nothing at all to say why. Highlighting the wrong characters would be
/// worse than highlighting none, so the answer was to highlight the right ones rather
/// than to keep highlighting nothing.
/// </para>
/// </param>
public sealed record PaletteRow(
    PaletteEntry Entry,
    int Score,
    IReadOnlyList<int> Positions,
    IReadOnlyList<int>? SecondaryPositions = null);

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
/// under it, which row is highlighted after backspacing, where the caret lands after a
/// word delete - is decided here and tested without anything on screen.
/// </para>
/// <para>
/// The host owns the entries and replaces them when the window manager says something
/// changed; the model owns the query, the caret, the mode, the marks and the
/// selection.
/// </para>
/// </remarks>
public sealed class PaletteModel
{
    private readonly List<PaletteEntry> _entries = [];
    private readonly List<PaletteRow> _rows = [];

    /// <summary>
    /// The rows the user has marked, by their <see cref="PaletteEntry.Target"/>.
    /// </summary>
    /// <remarks>
    /// Keyed by target rather than held as entries, because entries are rebuilt from
    /// scratch every time the window manager reports a change - so anything holding
    /// them by reference loses its marks the moment a window opens somewhere else on
    /// the desktop. The target is a command naming one window, which is exactly as
    /// stable as the window is.
    /// <para>
    /// The entry is kept alongside so that acting on the marked set does not require
    /// the rows to still be on screen. Marking six windows and then typing something
    /// that filters all six away must still act on six windows.
    /// </para>
    /// </remarks>
    private readonly Dictionary<string, PaletteEntry> _marked = [];

    private string _query = string.Empty;
    private int _caret;

    /// <summary>
    /// Supplies rows derived from the query, shown above the filtered list.
    /// </summary>
    /// <remarks>
    /// Set once by the host. Null means the palette only ever offers things from its
    /// entry list.
    /// </remarks>
    public QueryAugmenter? Augmenter { get; set; }

    /// <summary>
    /// Which character selects which mode.
    /// </summary>
    /// <remarks>
    /// Replaced wholesale when the configuration is reloaded. Everything derived from
    /// it - the mode, the term, the caret's idea of where the text starts - is
    /// computed on demand rather than cached, so there is nothing to invalidate.
    /// </remarks>
    public PalettePrefixes Prefixes { get; set; } = PalettePrefixes.Default;

    /// <summary>
    /// The order Tab walks, and the order the jump keys number.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Frequency, not declaration order, and help is not in it. Tab used to walk the
    /// enum, which put monitors between scratchpad and inspect for no reason anybody
    /// chose, and made help a stop on the way to somewhere else. Help is a place you
    /// go on purpose: it has a prefix, it has a jump key, and it is one Escape from
    /// anywhere - it does not also need to be in everybody's way.
    /// </para>
    /// <para>
    /// Windows first because it is what the palette opens as. Commands second because
    /// it is the only other mode that is used constantly. The rest are ordered by how
    /// often somebody actually needs them, which is roughly how often they answer a
    /// question that has just gone wrong.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<PaletteMode> TabRing => s_tabRing;

    private static readonly PaletteMode[] s_tabRing =
    [
        PaletteMode.Windows,
        PaletteMode.Commands,
        PaletteMode.Workspaces,
        PaletteMode.Inspect,
        PaletteMode.Scratchpad,
        PaletteMode.Layouts,
        PaletteMode.Monitors,
    ];

    /// <summary>
    /// Every mode a jump key can reach, in the order the keys number them.
    /// </summary>
    /// <remarks>
    /// The Tab ring, then help. So Ctrl+1 is the window list and Ctrl+8 is the key
    /// reference, and the digits agree with the order of the hint bar rather than
    /// with the order of a C# enum.
    /// <para>
    /// This is the answer to prefixes being unreachable on some keyboards and to Tab
    /// being seven presses from one end of the ring to the other. A digit is one
    /// keystroke, it is in the same place on every layout in the world, and it needs
    /// no memory beyond the bar already on screen.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<PaletteMode> JumpOrder { get; } = [.. TabRing, PaletteMode.Help];

    /// <summary>The mode a jump key selects, or null when there is none at that position.</summary>
    /// <param name="oneBased">The digit that was pressed: 1 through 9.</param>
    public static PaletteMode? ModeAtJump(int oneBased) =>
        oneBased >= 1 && oneBased <= JumpOrder.Count ? JumpOrder[oneBased - 1] : null;

    /// <summary>The default prefix table, for callers with no configuration to hand.</summary>
    public static IReadOnlyDictionary<char, PaletteMode> DefaultPrefixes =>
        PalettePrefixes.Default.ByCharacter;

    /// <summary>The prefix that selects a mode by default, or nothing for the window list.</summary>
    public static char PrefixFor(PaletteMode mode) => PalettePrefixes.Default.PrefixFor(mode);

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
    /// Where the caret sits in <see cref="Query"/>.
    /// </summary>
    /// <remarks>
    /// The palette was append-only: a typo in the middle of <c>resize --width +5%</c>
    /// cost the whole line, because the only way back to it was to delete everything
    /// after it. That is tolerable in the window list, where the query is three
    /// letters and retyping is faster than aiming - and not tolerable in commands
    /// mode, which is not a filter but a text field somebody is composing in.
    /// </remarks>
    public int Caret => _caret;

    /// <summary>
    /// Where the caret sits within <see cref="Term"/>, which is what is drawn.
    /// </summary>
    /// <remarks>
    /// The prefix is a character of the query and is not drawn, so the two indices
    /// differ by exactly its length. Clamped, because the caret is allowed to sit on
    /// the prefix itself - pressing Home in commands mode puts it there - and the
    /// renderer has nowhere to draw that.
    /// </remarks>
    public int TermCaret => Math.Max(0, _caret - Prefixes.PrefixLengthOf(_query));

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
    public PaletteMode Mode => Prefixes.ModeOf(_query);

    /// <summary>The query with any mode prefix removed.</summary>
    public string Term => Prefixes.TermOf(_query);

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

    /// <summary>How many rows are marked.</summary>
    public int MarkedCount => _marked.Count;

    /// <summary>
    /// The marked rows, in the order they were marked.
    /// </summary>
    /// <remarks>
    /// Insertion order, which a dictionary preserves in practice and which is
    /// preserved deliberately here by never removing and re-adding. Somebody who
    /// marked four windows and asked to move them expects them to arrive in the order
    /// they picked, because that is the order they will be tiled in.
    /// </remarks>
    public IReadOnlyCollection<PaletteEntry> Marked => _marked.Values;

    /// <summary>Whether a particular row is marked.</summary>
    public bool IsMarked(PaletteEntry entry) =>
        entry?.Target is { Length: > 0 } target && _marked.ContainsKey(target);

    /// <summary>
    /// Marks or unmarks the selected row, and says whether anything happened.
    /// </summary>
    /// <remarks>
    /// Only rows that name a window can be marked. Marking a layout or a line of help
    /// would be marking something there is no way to act on as a set, and a key that
    /// silently does nothing on two rows out of three is worse than one that is
    /// honestly unavailable.
    /// </remarks>
    public bool ToggleMark()
    {
        if (Selected?.Entry is not { Target.Length: > 0 } entry) return false;

        if (!_marked.Remove(entry.Target!)) _marked[entry.Target!] = entry;

        return true;
    }

    /// <summary>Forgets every mark.</summary>
    public void ClearMarks() => _marked.Clear();

    /// <summary>Replaces the candidates and re-filters.</summary>
    /// <remarks>
    /// <para>
    /// The selection is preserved where it can be. Entries arrive again whenever the
    /// window manager reports a change, and a list that jumped back to the top each
    /// time would be unusable while anything was happening on screen.
    /// </para>
    /// <para>
    /// By identity first and by what the row is second. Identity alone was not enough
    /// and quietly never had been: a refresh rebuilds every entry from the wire, so
    /// nothing on screen is ever the same object afterwards, and the selection went
    /// back to the top on every window event regardless. Matching on the command and
    /// the title finds the same row in the new list.
    /// </para>
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
    /// <remarks>
    /// The caret goes to the end, which is where somebody who has just been handed a
    /// query wants to carry on from. Editing keys move it from there.
    /// </remarks>
    public void SetQuery(string query)
    {
        _query = query ?? string.Empty;
        _caret = _query.Length;

        // Deliberately not preserved. Typing is the user narrowing the list, and the
        // whole point of narrowing is that the best answer moves to the top - keeping
        // the old selection would leave them pressing Enter on something that is no
        // longer what they were aiming at.
        Refilter(keep: null);
    }

    /// <summary>Switches mode, keeping whatever has been typed.</summary>
    public void SetMode(PaletteMode mode)
    {
        char prefix = Prefixes.PrefixFor(mode);
        SetQuery(prefix == '\0' ? Term : prefix + Term);
    }

    /// <summary>
    /// Steps around the Tab ring.
    /// </summary>
    /// <remarks>
    /// A mode outside the ring - help, reached by its prefix or its jump key - steps
    /// back into it rather than being stuck. Tabbing out of help lands on the window
    /// list going forwards and on the last mode going back, which is what somebody who
    /// pressed Tab to leave expects either way.
    /// </remarks>
    public PaletteMode NextMode(bool forward)
    {
        int at = Array.IndexOf(s_tabRing, Mode);

        if (at < 0) return forward ? s_tabRing[0] : s_tabRing[^1];

        int next = ((at + (forward ? 1 : -1)) % s_tabRing.Length + s_tabRing.Length) % s_tabRing.Length;

        return s_tabRing[next];
    }

    // ---- typing ----------------------------------------------------------------

    /// <summary>
    /// The query and caret a typed character produces.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Inserting at the caret, except when a prefix is typed against a query that has
    /// no search term in it yet - then it replaces whatever prefix was there. Without
    /// that, prefixes only ever worked from the window list: in any other mode the
    /// query already began with one, so typing <c>!</c> produced <c>&gt;!</c>, which
    /// is still the command list searching for an exclamation mark. Every mode but the
    /// first was a one-way door reachable again only with Tab or Backspace.
    /// </para>
    /// <para>
    /// Only while the term is empty, so a prefix character stays literal once there is
    /// something to search. Typing <c>#</c> after <c>&gt;foo</c> is somebody spelling a
    /// search, not somebody changing their mind about the mode.
    /// </para>
    /// <para>
    /// Static and pure so the rule can be tested without a model, and so the host's
    /// own tests can keep asking it directly.
    /// </para>
    /// </remarks>
    public static (string Query, int Caret) AfterTyping(
        PalettePrefixes prefixes, string query, int caret, char typed)
    {
        ArgumentNullException.ThrowIfNull(prefixes);
        ArgumentNullException.ThrowIfNull(query);

        if (prefixes.IsPrefix(typed) && prefixes.TermOf(query).Length == 0)
            return (typed.ToString(), 1);

        int at = Math.Clamp(caret, 0, query.Length);

        return (query[..at] + typed + query[at..], at + 1);
    }

    /// <summary>Inserts a character at the caret.</summary>
    public void Insert(char typed)
    {
        (string query, int caret) = AfterTyping(Prefixes, _query, _caret, typed);

        _query = query;
        Refilter(keep: null);

        _caret = Math.Clamp(caret, 0, _query.Length);
    }

    /// <summary>
    /// Deletes backwards from the caret, by character or by word.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A word delete stops at the prefix rather than eating it. Deleting the last word
    /// of <c>&gt;focus</c> used to clear the whole query, which drops the prefix, which
    /// silently moves the palette back to the window list - so a key that says it
    /// deletes a word changed what Enter was going to do.
    /// </para>
    /// <para>
    /// A plain Backspace <em>at</em> the prefix deletes it, because that is the other
    /// thing Backspace means here and the only way out of a mode by typing. The term is
    /// kept, exactly as it is when a mode is changed any other way: somebody backing
    /// out of the command list has not changed their mind about what they were
    /// searching for.
    /// </para>
    /// </remarks>
    public void DeleteBack(bool wholeWord)
    {
        int floor = Prefixes.PrefixLengthOf(_query);

        if (_caret <= floor)
        {
            // Nothing before the caret but the prefix. Leaving the mode is the useful
            // reading, and doing nothing at all is how this got stuck: the guard that
            // stops a word delete eating the prefix would otherwise stop Backspace
            // leaving the mode too, and the only way out would be Tab.
            if (floor == 0) return;

            _query = _query[floor..];
            Refilter(keep: null);

            _caret = 0;
            return;
        }

        int from = wholeWord ? WordStart(_query, _caret, floor) : _caret - 1;

        _query = _query[..from] + _query[_caret..];
        Refilter(keep: null);

        _caret = Math.Clamp(from, 0, _query.Length);
    }

    /// <summary>Deletes the character after the caret.</summary>
    public void DeleteForward()
    {
        if (_caret >= _query.Length) return;

        int caret = _caret;

        _query = _query[..caret] + _query[(caret + 1)..];
        Refilter(keep: null);

        _caret = Math.Clamp(caret, 0, _query.Length);
    }

    /// <summary>
    /// Clears what was typed, keeping the mode.
    /// </summary>
    /// <remarks>
    /// The prefix survives. Clearing the whole query drops it, which moves the palette
    /// to the window list - so a key documented as "clear what you typed" also changed
    /// which list Enter would act on, silently, and the user had asked for neither.
    /// Backspace on an empty term is still how a mode is left.
    /// </remarks>
    public void ClearTerm()
    {
        char prefix = Prefixes.PrefixFor(Mode);

        _query = prefix == '\0' ? string.Empty : prefix.ToString();
        Refilter(keep: null);

        _caret = _query.Length;
    }

    /// <summary>Moves the caret, clamped to the text and never onto the prefix.</summary>
    public void MoveCaret(int delta)
    {
        int floor = Prefixes.PrefixLengthOf(_query);
        _caret = Math.Clamp(_caret + delta, floor, _query.Length);
    }

    /// <summary>Puts the caret at the start or the end of what was typed.</summary>
    public void CaretToEdge(bool end) =>
        _caret = end ? _query.Length : Prefixes.PrefixLengthOf(_query);

    /// <summary>Where the word before <paramref name="caret"/> begins.</summary>
    /// <remarks>
    /// Trailing spaces are crossed first, so deleting a word from
    /// <c>focus --workspace </c> removes the flag rather than only the space it ends
    /// with - which is what every other text field on the machine does and what makes
    /// the key worth pressing twice.
    /// </remarks>
    private static int WordStart(string text, int caret, int floor)
    {
        int at = caret;

        while (at > floor && text[at - 1] == ' ') at--;
        while (at > floor && text[at - 1] != ' ') at--;

        return Math.Max(floor, at);
    }

    // ---- selection --------------------------------------------------------------

    /// <summary>
    /// Moves the selection, wrapping at both ends.
    /// </summary>
    /// <remarks>
    /// Wrapping because the list is short and reaching the last entry by pressing up
    /// once is worth more than the theoretical confusion of it. Every palette the user
    /// has met behaves this way.
    /// <para>
    /// A step larger than the list is clamped rather than wrapped round several times.
    /// PageDown on five rows is somebody asking for the bottom, not for two and a half
    /// laps.
    /// </para>
    /// </remarks>
    public void MoveSelection(int delta)
    {
        if (_rows.Count == 0)
        {
            SelectedIndex = -1;
            return;
        }

        if (Math.Abs(delta) >= _rows.Count)
        {
            SelectedIndex = delta > 0 ? _rows.Count - 1 : 0;
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

    /// <summary>
    /// How many rows the window should make room for.
    /// </summary>
    /// <remarks>
    /// So a search that matched two things is drawn two rows tall rather than two rows
    /// of text above ten rows of nothing. The empty state is given room for its two
    /// lines, because "No matches" and the sentence under it are themselves worth
    /// showing properly.
    /// </remarks>
    public int RowsToShow(int capacity) =>
        _rows.Count == 0 ? Math.Min(2, capacity) : Math.Clamp(_rows.Count, 1, capacity);

    private void Refilter(PaletteEntry? keep)
    {
        _rows.Clear();

        string term = Term;
        Span<int> positions = stackalloc int[FuzzyMatcher.MaxPositions];
        Span<int> secondary = stackalloc int[FuzzyMatcher.MaxPositions];

        foreach (PaletteEntry entry in _entries)
        {
            MatchResult best = FuzzyMatcher.Match(term, entry.Primary, positions);

            if (!best.IsMatch)
            {
                // The dim half is searched too. Finding a window by its application
                // when the title says nothing about it - "Untitled document" - is most
                // of what this is for.
                MatchResult fallback = FuzzyMatcher.Match(term, entry.Secondary, secondary);
                if (!fallback.IsMatch) continue;

                // Scored below any title match, because a title is what the user is
                // picturing when they type. Highlighted all the same: a row in the list
                // with nothing underlined anywhere reads as the palette having matched
                // it by accident.
                _rows.Add(new PaletteRow(
                    entry, fallback.Score / 2, [], Recorded(secondary, fallback)));

                continue;
            }

            _rows.Add(new PaletteRow(entry, best.Score, Recorded(positions, best)));
        }

        _rows.Sort(Compare);

        // Prepended after sorting rather than sorted in. These rows are not matches
        // and have no score to compare; giving them an enormous one and hoping would
        // work until something else legitimately scored higher.
        //
        // Split by whether they can actually do anything. A row that runs what has been
        // typed belongs above the matches - it is the thing being composed. A row that
        // only explains why the text will not parse belongs below them, because Enter
        // lands on the first row and must never land on something inert.
        //
        // That distinction is not cosmetic. Every macro with a space in its name -
        // "Code layout" - put an "unknown command 'Code'" row above itself, so pressing
        // Enter on what looked like the obvious match did nothing at all, and the
        // feature appeared not to work. The diagnostic is still there, at the bottom,
        // and is still the only row when nothing else matched.
        if (Augmenter?.Invoke(Mode, term) is { Count: > 0 } derived)
        {
            _rows.InsertRange(
                0,
                derived.Where(e => e.Command.Length > 0)
                       .Select(e => new PaletteRow(e, int.MaxValue, [])));

            _rows.AddRange(
                derived.Where(e => e.Command.Length == 0)
                       .Select(e => new PaletteRow(e, 0, [])));
        }

        SelectedIndex = _rows.Count == 0 ? -1 : IndexOf(keep);
    }

    /// <summary>
    /// The positions actually written down, which is not always all of them.
    /// </summary>
    /// <remarks>
    /// The matcher reports how many characters matched and writes a position only
    /// while the caller's span has room. Slicing by the count alone therefore reads
    /// past the end for any query longer than the buffer - a latent
    /// <c>ArgumentOutOfRangeException</c> waiting for somebody to paste a sentence
    /// into the search box.
    /// </remarks>
    private static int[] Recorded(ReadOnlySpan<int> positions, MatchResult match) =>
        positions[..Math.Min(match.Matched, positions.Length)].ToArray();

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

    /// <summary>
    /// Where a previously selected entry has ended up, or the top.
    /// </summary>
    /// <remarks>
    /// Identity first, because within one list it is exact and free. Then by what the
    /// row is, because a refresh replaces every entry with an equal one built from the
    /// wire and identity cannot survive that - which is why the selection used to jump
    /// to the top whenever anything at all happened on the desktop.
    /// </remarks>
    private int IndexOf(PaletteEntry? entry)
    {
        if (entry is null) return 0;

        for (int i = 0; i < _rows.Count; i++)
            if (ReferenceEquals(_rows[i].Entry, entry))
                return i;

        for (int i = 0; i < _rows.Count; i++)
        {
            PaletteEntry candidate = _rows[i].Entry;

            if (string.Equals(candidate.Command, entry.Command, StringComparison.Ordinal) &&
                string.Equals(candidate.Primary, entry.Primary, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return 0;
    }
}
