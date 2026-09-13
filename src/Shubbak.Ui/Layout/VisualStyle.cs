using Shubbak.Core.Rendering;

namespace Shubbak.Ui.Layout;

/// <summary>How text is drawn.</summary>
/// <param name="Family">Font family name.</param>
/// <param name="Size">Size in device-independent pixels.</param>
/// <param name="Bold">Whether to use a bold weight.</param>
/// <param name="Italic">Whether to slant.</param>
public readonly record struct FontStyle(
    string Family = "Segoe UI",
    double Size = 12,
    bool Bold = false,
    bool Italic = false)
{
    public static FontStyle Default => new();
}

/// <summary>Which edges of a node its border is drawn along.</summary>
/// <remarks>
/// A border is usually an outline, but a bar docked at the top of the screen wants a
/// hairline along the edge that faces the windows and nothing along the three that
/// face the bezel - and an underline is the other common way to mark an active item.
/// Anything short of <see cref="All"/> is drawn as straight lines, ignoring the
/// corner radius.
/// </remarks>
[Flags]
public enum BorderSides
{
    None = 0,
    Top = 1,
    Right = 2,
    Bottom = 4,
    Left = 8,
    All = Top | Right | Bottom | Left,
}

/// <summary>Everything the renderer needs in order to draw a node.</summary>
/// <param name="Foreground">Text colour.</param>
/// <param name="Background">Fill colour.</param>
/// <param name="BorderColour">Border colour.</param>
/// <param name="BorderWidth">Border thickness in pixels.</param>
/// <param name="CornerRadius">Corner rounding in pixels.</param>
/// <param name="Font">Text style.</param>
/// <param name="Opacity">Overall opacity, 0 to 1.</param>
/// <param name="BorderSides">
/// The edges the border runs along; an outline unless said otherwise. Only read when
/// <paramref name="BorderWidth"/> is positive, so a zeroed struct still draws no border.
/// </param>
public readonly record struct VisualStyle(
    Colour Foreground = default,
    Colour Background = default,
    Colour BorderColour = default,
    int BorderWidth = 0,
    int CornerRadius = 0,
    FontStyle Font = default,
    double Opacity = 1.0,
    BorderSides BorderSides = BorderSides.All)
{
    public static VisualStyle Default => new()
    {
        Foreground = Colour.White,
        Background = Colour.Transparent,
        Font = FontStyle.Default,
        Opacity = 1.0,
        BorderSides = BorderSides.All,
    };

    /// <summary>Overlays only the properties set in <paramref name="other"/>.</summary>
    /// <remarks>
    /// Backs the config's <c>extends</c>, so a bar profile can inherit from another
    /// and change one thing.
    /// </remarks>
    public VisualStyle Merge(VisualStyle? other)
    {
        if (other is not { } o) return this;

        return this with
        {
            Foreground = o.Foreground.A == 0 && o.Foreground.R == 0 ? Foreground : o.Foreground,
            Background = o.Background,
            BorderColour = o.BorderWidth > 0 ? o.BorderColour : BorderColour,
            BorderWidth = o.BorderWidth > 0 ? o.BorderWidth : BorderWidth,
            BorderSides = o.BorderWidth > 0 ? o.BorderSides : BorderSides,
            CornerRadius = o.CornerRadius > 0 ? o.CornerRadius : CornerRadius,
            Font = string.IsNullOrEmpty(o.Font.Family) ? Font : o.Font,
            Opacity = o.Opacity,
        };
    }
}
