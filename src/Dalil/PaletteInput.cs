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

    /// <summary>Ask first, then send the row's command.</summary>
    Confirm,

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
/// window's own state - which frame is open, whether marks are set - and the answers
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
    /// cannot drift apart without the lookup simply failing to find anything. A test
    /// holds this table against the one the help screen prints.
    /// </remarks>
    internal static string? ChordFor(VIRTUAL_KEY key, bool control, bool shift, bool alt = false) =>
        (key, control, shift, alt) switch
        {
            (VIRTUAL_KEY.VK_F, true, true, false) => "Ctrl+Shift+F",
            (VIRTUAL_KEY.VK_S, true, true, false) => "Ctrl+Shift+S",
            (VIRTUAL_KEY.VK_M, true, true, false) => "Ctrl+Shift+M",
            (VIRTUAL_KEY.VK_A, true, true, false) => "Ctrl+Shift+A",
            (VIRTUAL_KEY.VK_W, true, true, false) => "Ctrl+Shift+W",
            (VIRTUAL_KEY.VK_I, true, true, false) => InspectChord,

            // Alt+Enter was carried on "Bring it here" from the day that action was
            // written and was never wired to anything: the chord table had no entry
            // for it, so the badge beside the row named a key that did nothing at all.
            (VIRTUAL_KEY.VK_RETURN, false, false, true) => "Alt+Enter",

            _ => null,
        };

    /// <summary>
    /// Which mode a digit jumps to, or null when the key is not a jump.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The route into a mode that works on every keyboard on earth. Prefixes are
    /// faster to type and cannot be typed at all on several layouts - <c>~</c> is a
    /// dead key on German and on the international layouts, so it produces no
    /// character until the next keypress and the mode never changes - and Tab is seven
    /// presses from one end of the ring to the other.
    /// </para>
    /// <para>
    /// The top row rather than the numeric keypad, and both, because a laptop has one
    /// and a desk has the other. Order matches the hint bar, so the digit is read off
    /// the screen rather than remembered.
    /// </para>
    /// </remarks>
    internal static PaletteMode? JumpFor(VIRTUAL_KEY key, bool control)
    {
        if (!control) return null;

        int digit = key switch
        {
            >= VIRTUAL_KEY.VK_1 and <= VIRTUAL_KEY.VK_9 => key - VIRTUAL_KEY.VK_1 + 1,
            >= VIRTUAL_KEY.VK_NUMPAD1 and <= VIRTUAL_KEY.VK_NUMPAD9 => key - VIRTUAL_KEY.VK_NUMPAD1 + 1,
            _ => 0,
        };

        return digit == 0 ? null : PaletteModel.ModeAtJump(digit);
    }

    /// <summary>
    /// What pressing Enter on a row should do.
    /// </summary>
    /// <param name="entry">The selected row.</param>
    /// <param name="mode">Which list is showing.</param>
    /// <param name="insideOverlay">Whether a frame is open on top of that list.</param>
    /// <param name="confirmDestructive">Whether an irreversible action asks first.</param>
    internal static PaletteChoice Choose(
        PaletteEntry entry, PaletteMode mode, bool insideOverlay, bool confirmDestructive = true)
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
        // than the part that fit, which is the only way to read a path, the sentence
        // about elevation, or a composed rule without leaving the palette for a shell.
        if (entry.Expands is { Length: > 0 }) return PaletteChoice.Expand;

        // A row carrying its own list opens it instead of running. Only inside an
        // overlay: at the top level a row's list is what Ctrl+Enter is for, and Enter
        // has to keep meaning "go to this window".
        //
        // Unless the row exists in order to ask. A prompting macro has no command
        // until a value has been chosen, so there is nothing else Enter could mean -
        // and leaving the question behind Ctrl+Enter would hide it behind a key
        // nobody has a reason to press on a row that looks like every other macro.
        if (entry.Command.Length == 0 && (insideOverlay || entry.Prompts) && entry.HasActions)
            return PaletteChoice.OpenChildren;

        if (entry.Command.Length > 0)
        {
            return entry.Destructive && confirmDestructive
                ? PaletteChoice.Confirm
                : PaletteChoice.Run;
        }

        // A verb that needs arguments is offered as text to complete rather than run.
        // Running it bare would be rejected by the parser and read as a broken palette.
        //
        // Only in commands mode, which is the only mode where the search box is a
        // thing being composed rather than a filter. Everywhere else this was
        // confidently wrong: choosing a monitor that had no workspace on it put the
        // display's own name into the command box, as though `\\.\DISPLAY2` were a verb
        // somebody had started typing.
        return mode is PaletteMode.Commands && !insideOverlay
            ? PaletteChoice.Complete
            : PaletteChoice.Nothing;
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

    /// <summary>
    /// The text a copy should put on the clipboard for a window row.
    /// </summary>
    /// <remarks>
    /// A window row's title is rarely what somebody is copying it for. The reason to
    /// copy a row out of the window list is to put its class or its process into a
    /// rule, and both of those live in the dim half - so copying the title alone
    /// handed over the one attribute that is guaranteed to be wrong to match on.
    /// </remarks>
    internal static string DescribeForClipboard(PaletteEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.Expands is { Length: > 0 } expands) return expands;

        return entry.Secondary is { Length: > 0 } secondary
            ? $"{entry.Primary}  \u2014  {secondary}"
            : entry.Primary;
    }
}
