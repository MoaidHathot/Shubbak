namespace Shubbak.Ui.Layout;

/// <summary>
/// Turns a tree built in device-independent pixels into one in the pixels of a
/// particular display.
/// </summary>
/// <remarks>
/// <para>
/// Every size in a configuration - a bar's height, a font, a padding, an icon - is
/// written once and meant to look the same on every display. The tree is built in
/// those units and scaled here, just before layout, by the ratio of the display's DPI
/// to 96; the layout then measures text in the scaled font, so what is measured is
/// what is drawn. Without this the bar was whatever size its numbers said in physical
/// pixels: on a 4K display beside a 1080p one at 100 percent, half the height, with
/// the config unable to say anything different for either.
/// </para>
/// <para>
/// In place, because the tree is rebuilt on every change and scaling a fresh tree is
/// cheaper than building a scaled copy. A factor of one is a no-op and is skipped.
/// </para>
/// <para>
/// A border that exists stays at least a pixel thick; a font that exists stays at
/// least a pixel tall. Everything else rounds to the nearest pixel, so a 6-pixel
/// padding at 150 percent is 9 and at 125 percent is 8 rather than 7.
/// </para>
/// </remarks>
public static class VisualScaling
{
    /// <summary>The DPI at which a device-independent pixel is a pixel.</summary>
    public const uint BaselineDpi = 96;

    /// <summary>The factor for a display, or exactly 1 at the baseline.</summary>
    public static double FactorFor(uint dpi) => dpi == 0 ? 1.0 : dpi / (double)BaselineDpi;

    /// <summary>Scales a length, rounding to the nearest pixel.</summary>
    public static int Scale(int length, double factor) => (int)Math.Round(length * factor, MidpointRounding.AwayFromZero);

    /// <summary>Scales a length that must not vanish: a border, a font.</summary>
    public static int ScaleAtLeastOne(int length, double factor) =>
        length <= 0 ? length : Math.Max(1, Scale(length, factor));

    /// <summary>Scales a node and everything under it, in place.</summary>
    public static void Scale(VisualNode root, double factor)
    {
        ArgumentNullException.ThrowIfNull(root);

        if (Math.Abs(factor - 1.0) < 0.0005) return;
        if (factor <= 0) throw new ArgumentOutOfRangeException(nameof(factor), factor, "A scale factor is positive.");

        foreach (VisualNode node in root.SelfAndDescendants())
        {
            node.Box = Scale(node.Box, factor);
            node.Gap = Scale(node.Gap, factor);
            node.Style = Scale(node.Style, factor);

            if (node.HoverStyle is { } hover) node.HoverStyle = Scale(hover, factor);
        }
    }

    /// <summary>Scales one box's sizes and spacing.</summary>
    public static BoxStyle Scale(BoxStyle box, double factor) => box with
    {
        Width = box.Width is { } width ? Scale(width, factor) : null,
        Height = box.Height is { } height ? Scale(height, factor) : null,
        MinWidth = Scale(box.MinWidth, factor),
        MaxWidth = box.MaxWidth is { } max ? Scale(max, factor) : null,
        Padding = Scale(box.Padding, factor),
        Margin = Scale(box.Margin, factor),
    };

    /// <summary>Scales one style's font, corners and border.</summary>
    public static VisualStyle Scale(VisualStyle style, double factor) => style with
    {
        Font = style.Font with { Size = Math.Max(1.0, style.Font.Size * factor) },
        CornerRadius = Scale(style.CornerRadius, factor),
        BorderWidth = ScaleAtLeastOne(style.BorderWidth, factor),
    };

    /// <summary>Scales each edge.</summary>
    public static Edges Scale(Edges edges, double factor) => new(
        Scale(edges.Left, factor),
        Scale(edges.Top, factor),
        Scale(edges.Right, factor),
        Scale(edges.Bottom, factor));
}
