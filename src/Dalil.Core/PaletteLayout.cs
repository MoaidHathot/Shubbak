using Shubbak.Core.Geometry;

namespace Dalil.Core;

/// <summary>
/// Where everything sits, at one scale, for one window size.
/// </summary>
/// <remarks>
/// <para>
/// Computed once and used by both the renderer and the mouse. That is the entire
/// reason it exists as a type: the row rectangles were worked out inside the drawing
/// loop, and a hit test that recomputed them independently would agree today and
/// disagree the first time either side changed a margin. The failure would be a click
/// selecting the row above the one under the pointer - obvious to a user, invisible in
/// review, and impossible to write a test for while the two calculations are separate.
/// </para>
/// <para>
/// Every measurement is written at 96 DPI and multiplied on the way out, so there is
/// one set of numbers rather than two sets to keep in step.
/// </para>
/// </remarks>
public readonly struct PaletteLayout
{
    private const int PaddingAt96 = 10;
    private const int TextInsetAt96 = 14;
    private const int AccentWidthAt96 = 3;
    private const int PillPaddingAt96 = 7;
    private const int ChipPaddingAt96 = 9;
    private const int HintBarAt96 = 40;
    private const int CornerAt96 = 10;
    private const int SearchGapAt96 = 8;
    private const int IconSizeAt96 = 18;
    private const int IconGapAt96 = 10;
    private const int MarkWidthAt96 = 3;

    public PaletteLayout(DalilConfig config, double scale, Rect canvas, bool icons = false)
    {
        Scale = scale;
        Canvas = canvas;

        Padding = At(PaddingAt96, scale);
        TextInset = At(TextInsetAt96, scale);
        AccentWidth = Math.Max(2, At(AccentWidthAt96, scale));
        PillPadding = At(PillPaddingAt96, scale);
        ChipPadding = At(ChipPaddingAt96, scale);
        HintBar = At(HintBarAt96, scale);
        Corner = At(CornerAt96, scale);
        MarkWidth = Math.Max(2, At(MarkWidthAt96, scale));

        // Never taller than the row it sits in, whatever the user did to row-height.
        // A row of 16 pixels with an 18-pixel icon in it is a row with an icon
        // overlapping the rows above and below.
        IconSize = icons ? Math.Min(At(IconSizeAt96, scale), config.RowHeight - At(8, scale)) : 0;
        IconGap = icons ? At(IconGapAt96, scale) : 0;

        if (IconSize <= 0)
        {
            IconSize = 0;
            IconGap = 0;
        }

        RowHeight = config.RowHeight;
        VisibleRows = config.VisibleRows;

        SearchBox = new Rect(
            canvas.X + Padding,
            canvas.Y + Padding,
            canvas.Width - (Padding * 2),
            config.RowHeight);

        ListTop = SearchBox.Bottom + At(SearchGapAt96, scale);
        HintBarTop = canvas.Bottom - HintBar;
    }

    public double Scale { get; }

    public Rect Canvas { get; }

    public Rect SearchBox { get; }

    /// <summary>Top edge of the first result row.</summary>
    public int ListTop { get; }

    public int RowHeight { get; }

    public int VisibleRows { get; }

    public int HintBarTop { get; }

    public int Padding { get; }

    public int TextInset { get; }

    public int AccentWidth { get; }

    public int PillPadding { get; }

    public int ChipPadding { get; }

    public int HintBar { get; }

    public int Corner { get; }

    /// <summary>How big an application icon is drawn, or zero when they are off.</summary>
    public int IconSize { get; }

    /// <summary>The gap between an icon and the title beside it.</summary>
    public int IconGap { get; }

    /// <summary>How wide the stripe marking a row for a bulk action is.</summary>
    public int MarkWidth { get; }

    /// <summary>
    /// Where a row's text starts, once anything drawn before it has had its room.
    /// </summary>
    /// <remarks>
    /// One number rather than three additions repeated at every call site. The title,
    /// the highlighted runs and the dim half all have to agree about this, and the
    /// hit-testing does too - and an icon column that only some of them knew about
    /// would be the same class of bug the whole type exists to prevent.
    /// </remarks>
    public int RowTextInset => TextInset + IconSize + IconGap;

    /// <summary>Where the icon for a visible row goes.</summary>
    public Rect IconBounds(int slot)
    {
        Rect row = RowBounds(slot);

        return IconSize <= 0
            ? new Rect(row.X, row.Y, 0, 0)
            : new Rect(
                row.X + TextInset,
                row.Y + ((row.Height - IconSize) / 2),
                IconSize,
                IconSize);
    }

    /// <summary>Scales a measurement written at 96 DPI.</summary>
    public int this[int value] => At(value, Scale);

    /// <summary>The rectangle of one visible row.</summary>
    /// <param name="slot">
    /// Position on screen, counting from zero at <see cref="ListTop"/> - not the index
    /// in the result list. The two differ whenever the list is scrolled, and confusing
    /// them is the other way this could select the wrong row.
    /// </param>
    public Rect RowBounds(int slot) => new(
        Canvas.X + Padding,
        ListTop + (slot * RowHeight),
        Canvas.Width - (Padding * 2),
        RowHeight);

    /// <summary>
    /// Which visible row a point is over, or -1 for none.
    /// </summary>
    /// <remarks>
    /// Deliberately the exact inverse of <see cref="RowBounds"/>, and tested as one:
    /// every row's own rectangle must hit-test back to itself. Points over the search
    /// box, the hint bar or the margins belong to no row and are refused rather than
    /// clamped, so a click on the chrome does not act on whatever happens to be
    /// nearest.
    /// </remarks>
    public int SlotAt(int x, int y)
    {
        if (x < Canvas.X + Padding || x >= Canvas.Right - Padding) return -1;
        if (y < ListTop || y >= HintBarTop) return -1;

        int slot = (y - ListTop) / RowHeight;

        return slot >= 0 && slot < VisibleRows ? slot : -1;
    }

    private static int At(int value, double scale) => (int)Math.Round(value * scale);
}
