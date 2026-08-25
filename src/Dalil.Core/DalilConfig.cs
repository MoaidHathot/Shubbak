using Shubbak.Core.Rendering;

namespace Dalil.Core;

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

    /// <summary>Where the palette appears.</summary>
    public PalettePlacement Placement { get; init; } = PalettePlacement.FocusedMonitor;

    /// <summary>
    /// The palette's own colours.
    /// </summary>
    /// <remarks>
    /// Five, not nine. The chip behind the mode name, the pill behind a badge, the
    /// accent down the selected row and the hairlines between sections are all
    /// derived from these by blending towards the background, so changing
    /// <c>background</c> and <c>match</c> moves the whole palette together. Asking
    /// for nine colours would be a good way to have nobody set any of them.
    /// </remarks>
    public Colour Background { get; init; } = new(0x16, 0x16, 0x1C);

    public Colour Foreground { get; init; } = new(0xE8, 0xE8, 0xEE);

    /// <summary>Colour of the characters that matched what was typed.</summary>
    public Colour Match { get; init; } = new(0x7D, 0xD3, 0xFC);

    /// <summary>Dimmer text: the process name, the command summary.</summary>
    public Colour Secondary { get; init; } = new(0x87, 0x87, 0x96);

    public Colour SelectionBackground { get; init; } = new(0x24, 0x2C, 0x3E);

    public Colour Border { get; init; } = new(0x39, 0x39, 0x48);

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
