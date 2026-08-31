using Shubbak.Core.Rendering;

namespace Dalil.Core;

/// <summary>
/// Where a macro's prompt gets the values it offers.
/// </summary>
/// <remarks>
/// Named after the thing being chosen rather than after the query that fetches it,
/// because the name is what somebody writes in the config file and "workspaces" is
/// what they are choosing between. The palette already holds every one of these lists
/// for its own argument completion, so a prompt costs a lookup rather than a round
/// trip - and a prompt whose list is empty can be told apart from one that failed.
/// </remarks>
public enum MacroParamSource
{
    /// <summary>Every workspace the window manager knows, by name.</summary>
    Workspaces,

    /// <summary>Every layout in the registry.</summary>
    Layouts,

    /// <summary>Binding modes declared in the configuration.</summary>
    BindingModes,

    /// <summary>Scratchpad slots currently holding a window.</summary>
    Scratchpads,

    /// <summary>The four compass directions, which are the same everywhere.</summary>
    Directions,

    /// <summary>Exactly what was written beside the parameter, and nothing else.</summary>
    Literals,
}

/// <summary>
/// A value a macro asks for before it can run.
/// </summary>
/// <remarks>
/// <para>
/// The difference between nineteen rows and one. Without this, "send this window to
/// the Notes workspace" is a macro, and so are the eighteen others - each with its own
/// name to invent, its own line to maintain, and its own place in a list that has to
/// be scrolled past. With it there is one row that asks which.
/// </para>
/// <para>
/// Deliberately a choice rather than free text. Every argument worth prompting for is
/// drawn from a list the palette already has, and a list can be searched, ranked and
/// shown with what each value means - whereas a text box would have to be typed into
/// blind and would accept a workspace that does not exist as readily as one that does.
/// </para>
/// </remarks>
/// <param name="Name">What the placeholder is called, without the braces.</param>
/// <param name="Source">Which list the choices come from.</param>
/// <param name="Literals">The choices themselves, when they were written out.</param>
public sealed record MacroParam(
    string Name,
    MacroParamSource Source,
    IReadOnlyList<string> Literals)
{
    /// <summary>The token that stands for this value in the commands.</summary>
    /// <remarks>
    /// The same <c>{name}</c> spelling <c>for-each</c> uses in the keybindings section,
    /// so there is one substitution syntax in this file rather than two.
    /// </remarks>
    public string Placeholder => $"{{{Name}}}";
}

/// <summary>
/// A named sequence of commands, offered as one row.
/// </summary>
/// <remarks>
/// <para>
/// The thing keybindings cannot be. A chord has to be memorised and there are only so
/// many left once the window manager has taken the obvious ones, so anything done
/// occasionally never gets bound and is therefore done by hand for ever - four keys to
/// go to the workspace, three to set the layout, one to equalise, every time.
/// </para>
/// <para>
/// A palette row costs nothing to have and nothing to remember. It is found by typing
/// roughly what it is called, which is a thing people are good at, rather than by
/// recalling which modifier it was put behind, which is a thing people are bad at.
/// </para>
/// </remarks>
/// <param name="Name">What it is called, and what is searched for.</param>
/// <param name="Description">One line, or empty to show the commands themselves.</param>
/// <param name="Commands">
/// What to send, already validated against the real parser at load time. Sent as one
/// newline-separated message, so the window manager runs them in order and stops at
/// the first refusal rather than half-applying something.
/// </param>
/// <param name="Problem">
/// Why this cannot run, when one of its commands did not parse.
/// <para>
/// Kept and shown rather than dropped in silence. A macro that vanished from the list
/// because of a typo three lines into it is a macro the user will look for, fail to
/// find, and conclude the feature does not work - whereas a row saying "unknown
/// direction 'lft'" is the same message the config file would have given and points
/// straight at the line.
/// </para>
/// </param>
/// <param name="Parameters">
/// Values to be chosen before it runs, in the order they are asked for.
/// <para>
/// Last, and defaulted, so that every macro written before prompts existed still
/// constructs exactly as it did. An empty list is the ordinary case and means the row
/// runs the moment Enter reaches it.
/// </para>
/// </param>
public sealed record PaletteMacro(
    string Name,
    string Description,
    IReadOnlyList<string> Commands,
    string? Problem = null,
    IReadOnlyList<MacroParam>? Parameters = null)
{
    /// <summary>The values this macro asks for, in order.</summary>
    public IReadOnlyList<MacroParam> Prompts => Parameters ?? [];

    /// <summary>Whether anything has to be chosen before this can run.</summary>
    public bool Asks => Parameters is { Count: > 0 };
}

/// <summary>
/// How the palette looks and where it appears.
/// </summary>
/// <remarks>
/// Lives in the same <c>shubbak.kdl</c> everything else does, under a <c>dalil</c>
/// section. One file, because a user who has to remember which of three files a
/// setting lives in has been given a filing system rather than a configuration.
/// </remarks>
public sealed record DalilConfig
{
    /// <summary>
    /// The signal name that opens the palette.
    /// </summary>
    /// <remarks>
    /// A name rather than a key. The keybinding lives in the user's keybindings block
    /// with every other key, and Shubbak carries the name without knowing what it is
    /// for - which is what keeps the palette out of the window manager.
    /// </remarks>
    public string OpenOnSignal { get; init; } = "palette";

    /// <summary>How wide the palette is, in pixels at 96 DPI.</summary>
    public int Width { get; init; } = 720;

    /// <summary>Height of one result row, in pixels at 96 DPI.</summary>
    public int RowHeight { get; init; } = 38;

    /// <summary>
    /// How many rows are visible at once.
    /// </summary>
    /// <remarks>
    /// A cap on drawing, not on matching: everything is still searched and ranked,
    /// and only this many rows are measured and painted. Measuring text is a round
    /// trip into GDI per row, so a list of several hundred windows would otherwise
    /// cost several hundred of them on every keystroke.
    /// </remarks>
    public int VisibleRows { get; init; } = 12;

    /// <summary>Whether the palette closes when it loses focus.</summary>
    public bool CloseOnBlur { get; init; } = true;

    /// <summary>Whether windows Shubbak does not manage are listed.</summary>
    /// <remarks>
    /// On by default, because an unmanaged window is the one most likely to be lost -
    /// nothing is arranging it, so nothing put it anywhere findable.
    /// </remarks>
    public bool ShowUnmanaged { get; init; } = true;

    /// <summary>
    /// Whether an action that cannot be undone asks before it happens.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On by default, and this is what <c>action-guard</c> became. That setting turned
    /// every direct chord off at once, which is why - with the shipped default - the
    /// action list printed <c>Ctrl+Shift+F</c> beside "Float it" and pressing it in the
    /// main list did nothing at all. The safety was real and it was bought by disabling
    /// eight harmless keys to protect against two dangerous ones.
    /// </para>
    /// <para>
    /// Confirming the two is cheaper and stricter: closing a window now takes a
    /// deliberate second keystroke whichever route was used to ask for it, including
    /// the ones the guard never covered, while floating and tiling and minimising are
    /// a single chord again. <c>action-guard</c> is still accepted and still means
    /// this, so nobody's configuration breaks.
    /// </para>
    /// </remarks>
    public bool ConfirmDestructive { get; init; } = true;

    /// <summary>Where the palette appears.</summary>
    public PalettePlacement Placement { get; init; } = PalettePlacement.FocusedMonitor;

    /// <summary>
    /// Whether a window row shows its application's icon.
    /// </summary>
    /// <remarks>
    /// On, because an icon is recognised faster than a word is read and the window
    /// list is scanned rather than read. It costs one non-blocking read of the window
    /// class per window, cached thereafter, and only for the rows actually on screen.
    /// </remarks>
    public bool ShowIcons { get; init; } = true;

    /// <summary>
    /// Whether the window shrinks to fit a short list.
    /// </summary>
    /// <remarks>
    /// On. A search that matched two things used to be drawn as two rows of text above
    /// ten rows of empty background, which reads as the palette having failed to draw
    /// the rest of something.
    /// </remarks>
    public bool ShrinkToFit { get; init; } = true;

    /// <summary>
    /// Which character selects which mode, where the user has said.
    /// </summary>
    /// <remarks>
    /// Empty means the defaults, which is what almost everybody wants. It exists for
    /// the keyboard layouts on which the defaults cannot be typed at all: <c>~</c> is a
    /// dead key on several, so the character never arrives and the mode is unreachable
    /// by prefix no matter how hard somebody presses it.
    /// </remarks>
    public IReadOnlyDictionary<PaletteMode, char> Prefixes { get; init; } =
        new Dictionary<PaletteMode, char>();

    /// <summary>The user's own named command sequences.</summary>
    public IReadOnlyList<PaletteMacro> Macros { get; init; } = [];

    /// <summary>
    /// The palette's own colours.
    /// </summary>
    /// <remarks>
    /// Six, not ten. The chip behind the mode name, the pill behind a badge, the
    /// accent down the selected row and the hairlines between sections are all
    /// derived from these by blending towards the background, so changing
    /// <c>background</c> and <c>match</c> moves the whole palette together. Asking
    /// for ten colours would be a good way to have nobody set any of them.
    /// </remarks>
    public Colour Background { get; init; } = new(0x16, 0x16, 0x1C);

    public Colour Foreground { get; init; } = new(0xE8, 0xE8, 0xEE);

    /// <summary>Colour of the characters that matched what was typed.</summary>
    public Colour Match { get; init; } = new(0x7D, 0xD3, 0xFC);

    /// <summary>Dimmer text: the process name, the command summary.</summary>
    public Colour Secondary { get; init; } = new(0x87, 0x87, 0x96);

    public Colour SelectionBackground { get; init; } = new(0x24, 0x2C, 0x3E);

    public Colour Border { get; init; } = new(0x39, 0x39, 0x48);

    /// <summary>
    /// The colour of something that cannot be undone.
    /// </summary>
    /// <remarks>
    /// Its own setting because it is the one colour whose job is to be noticed rather
    /// than to be harmonious, and deriving it from the others would make it agree with
    /// them - which is exactly what it must not do.
    /// </remarks>
    public Colour Danger { get; init; } = new(0xF3, 0x8B, 0xA8);

    public string FontFamily { get; init; } = "Segoe UI";

    public int FontSize { get; init; } = 15;
}

/// <summary>Which monitor the palette opens on.</summary>
public enum PalettePlacement
{
    /// <summary>The monitor holding the window that had focus.</summary>
    FocusedMonitor,

    /// <summary>The monitor under the mouse pointer.</summary>
    CursorMonitor,

    /// <summary>Always the primary monitor.</summary>
    Primary,
}
