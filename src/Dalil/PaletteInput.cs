using Dalil.Core;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Dalil;

/// <summary>What choosing a row should do.</summary>
internal enum PaletteChoice
{
    /// <summary>Nothing at all. A row that is only there to be read.</summary>
    Nothing,

    /// <summary>Change mode, for a row that names one.</summary>
    SwitchMode,

    /// <summary>Ask the window manager to describe a window.</summary>
    Inspect,

    /// <summary>Open the row's whole text, broken across rows.</summary>
    Expand,

    /// <summary>Open the list the row carries.</summary>
    OpenChildren,

    /// <summary>Put the row's text in the search box to be finished.</summary>
    Complete,

    /// <summary>Send the row's command.</summary>
    Run,
}

/// <summary>
/// What a keystroke means, decided without reference to a window.
/// </summary>
/// <remarks>
/// <para>
/// Pulled out of <see cref="PaletteWindow"/> because these are judgements rather than
/// plumbing, and because the window they used to live in cannot be built without a
/// real HWND, a message loop and a device context - so none of them could be held to
/// account. Everything here is a pure function of the row and the state it is being
/// chosen in.
/// </para>
/// <para>
/// It stays in the host rather than moving to Dalil.Core: the questions are about the
/// window's own state - which frame is open, whether the guard is on - and the answers
/// name a key type that only the host knows about.
/// </para>
/// </remarks>
internal static class PaletteInput
{
    /// <summary>The chord that inspects the selected window.</summary>
    internal const string InspectChord = "Ctrl+Shift+I";

    /// <summary>
    /// The chord a keystroke spells, or null when it spells none.
    /// </summary>
    /// <remarks>
    /// Written as the string the action carries rather than as an index, so the two
    /// halves of a chord - the key that produces it and the label that advertises it -
    /// cannot drift apart without the lookup simply failing to find anything.
    /// </remarks>
    internal static string? ChordFor(VIRTUAL_KEY key, bool control, bool shift) =>
        (key, control, shift) switch
        {
            (VIRTUAL_KEY.VK_F, true, true) => "Ctrl+Shift+F",
            (VIRTUAL_KEY.VK_S, true, true) => "Ctrl+Shift+S",
            (VIRTUAL_KEY.VK_M, true, true) => "Ctrl+Shift+M",
            (VIRTUAL_KEY.VK_A, true, true) => "Ctrl+Shift+A",
            (VIRTUAL_KEY.VK_W, true, true) => "Ctrl+Shift+W",
            (VIRTUAL_KEY.VK_I, true, true) => InspectChord,
            _ => null,
        };

    /// <summary>
    /// Whether a chord works even with the action guard on.
    /// </summary>
    /// <remarks>
    /// Only inspecting, and for a reason rather than as a convenience. The guard exists
    /// so that an action cannot be taken by accident, and inspecting takes no action -
    /// it is the one entry in the list that runs no command at all. Guarding it would
    /// put the fastest way to ask "why is this window behaving like this" three
    /// keystrokes behind a door protecting against consequences it does not have.
    /// </remarks>
    internal static bool IsExemptFromGuard(string chord) =>
        string.Equals(chord, InspectChord, StringComparison.Ordinal);

    /// <summary>
    /// Whether a chord should act, given where it was pressed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Inside the action list it always acts. That is the only place a chord is written
    /// down - each row carries its own as a badge - and a key printed beside the thing
    /// it does, which then does nothing, is worse than no key at all. It was also the
    /// only combination that could not work: the guard blocked chords in the main list
    /// and the list itself blocked them everywhere else, so with the shipped default
    /// every chord but one was inert in both places while being advertised in one.
    /// </para>
    /// <para>
    /// The guard is not weakened by this. It exists to stop an action being taken by
    /// accident from a list where the keyboard is busy searching; by the time somebody
    /// has pressed Ctrl+Enter and is looking at a list of verbs, pressing the chord
    /// printed on one of them is no more accidental than pressing Enter on it.
    /// </para>
    /// </remarks>
    /// <param name="chord">The chord that was pressed.</param>
    /// <param name="insideActionList">Whether a list opened from a row is showing.</param>
    /// <param name="guard">The <c>action-guard</c> setting.</param>
    internal static bool ChordActsHere(string chord, bool insideActionList, bool guard)
    {
        ArgumentNullException.ThrowIfNull(chord);

        return insideActionList || !guard || IsExemptFromGuard(chord);
    }

    /// <summary>
    /// The query a typed character produces.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Appending, except when a prefix is typed against a query that has no search term
    /// in it yet - then it replaces whatever prefix was there. Without that, prefixes
    /// only ever worked from the window list: in any other mode the query already began
    /// with one, so typing <c>!</c> produced <c>&gt;!</c>, which is still the command
    /// list searching for an exclamation mark. Every mode but the first was a one-way
    /// door reachable again only with Tab or Backspace.
    /// </para>
    /// <para>
    /// Only while the term is empty, so a prefix character stays literal once there is
    /// something to search. Typing <c>#</c> after <c>&gt;foo</c> is somebody spelling a
    /// search, not somebody changing their mind about the mode.
    /// </para>
    /// </remarks>
    internal static string Typed(string query, char typed)
    {
        ArgumentNullException.ThrowIfNull(query);

        return PaletteModel.Prefixes.ContainsKey(typed) && PaletteModel.TermOf(query).Length == 0
            ? typed.ToString()
            : query + typed;
    }

    /// <summary>
    /// What pressing Enter on a row should do.
    /// </summary>
    /// <param name="entry">The selected row.</param>
    /// <param name="mode">Which list is showing.</param>
    /// <param name="insideOverlay">Whether a frame is open on top of that list.</param>
    internal static PaletteChoice Choose(PaletteEntry entry, PaletteMode mode, bool insideOverlay)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // A row that names a mode changes mode. This is what makes the help list
        // usable: somebody reading a list of keys will press Enter on the line they
        // want, and a help screen that ignores that has taught them the key and then
        // refused to honour it.
        if (entry.SwitchesTo is not null) return PaletteChoice.SwitchMode;

        // Some rows explain rather than do.
        //
        // In the inspect mode this is what Enter means outright: the rows are windows
        // that were skipped, and the question being asked of every one of them is
        // "why?". Going to a window you have just been told is not managed answers
        // nothing. Ctrl+Enter still reaches the full action list, so focusing it - or
        // taking it under management - is one keystroke further on.
        if (entry.Explains is not null &&
            (entry.Command.Length == 0 || (mode is PaletteMode.Inspect && !insideOverlay)))
        {
            return PaletteChoice.Inspect;
        }

        // Some rows are longer than a row. Opening one shows the whole thing rather
        // than the part that fit, which is the only way to read a path or the sentence
        // about elevation without leaving the palette for a shell.
        if (entry.Expands is { Length: > 0 }) return PaletteChoice.Expand;

        // A row carrying its own list opens it instead of running. Only inside an
        // overlay: at the top level a row's list is what Ctrl+Enter is for, and Enter
        // has to keep meaning "go to this window".
        if (entry.Command.Length == 0 && insideOverlay && entry.Actions is { Count: > 0 })
            return PaletteChoice.OpenChildren;

        if (entry.Command.Length > 0) return PaletteChoice.Run;

        // A verb that needs arguments is offered as text to complete rather than run.
        // Running it bare would be rejected by the parser and read as a broken palette.
        // A help row that is only a key reference has nothing to run either, and so
        // does the workspace a window is already on - and both do nothing rather than
        // pretending.
        return mode is PaletteMode.Help || insideOverlay
            ? PaletteChoice.Nothing
            : PaletteChoice.Complete;
    }

    /// <summary>
    /// The text a copy should put on the clipboard.
    /// </summary>
    /// <param name="selected">The row under the selection.</param>
    /// <param name="rows">Everything in the list currently showing.</param>
    /// <param name="frameWhole">
    /// The single value the frame was broken from, when it is an expanded one. Rejoining
    /// its rows would bake in line breaks that belong to this window's width rather than
    /// to the value.
    /// </param>
    /// <param name="everything">Whether the whole list was asked for rather than a row.</param>
    internal static string? CopyText(
        PaletteEntry? selected,
        IEnumerable<PaletteEntry> rows,
        string? frameWhole,
        bool everything)
    {
        ArgumentNullException.ThrowIfNull(rows);

        if (!everything) return selected is null ? null : Full(selected);

        if (frameWhole is { Length: > 0 }) return frameWhole;

        string joined = string.Join(Environment.NewLine, rows.Select(Full));

        return joined.Length == 0 ? null : joined;

        // The label as well as the value. "0x3047A" pasted on its own says nothing
        // about what it was, and the row on screen has the label sitting beside it.
        // Copied whole rather than as drawn, too: what is on screen has been clipped to
        // the width of the window, and a path with an ellipsis in it is not a path.
        static string Full(PaletteEntry entry) =>
            entry.Expands is { Length: > 0 } expands ? expands : entry.Primary;
    }
}
