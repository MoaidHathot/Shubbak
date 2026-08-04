using Shubbak.Core.Rendering;

namespace Taj.Core.Layout;

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

/// <summary>Everything the renderer needs in order to draw a node.</summary>
/// <param name="Foreground">Text colour.</param>
/// <param name="Background">Fill colour.</param>
/// <param name="BorderColour">Border colour.</param>
/// <param name="BorderWidth">Border thickness in pixels.</param>
/// <param name="CornerRadius">Corner rounding in pixels.</param>
/// <param name="Font">Text style.</param>
/// <param name="Opacity">Overall opacity, 0 to 1.</param>
public readonly record struct VisualStyle(
    Colour Foreground = default,
    Colour Background = default,
    Colour BorderColour = default,
    int BorderWidth = 0,
    int CornerRadius = 0,
    FontStyle Font = default,
    double Opacity = 1.0)
{
    public static VisualStyle Default => new()
    {
        Foreground = Colour.White,
        Background = Colour.Transparent,
        Font = FontStyle.Default,
        Opacity = 1.0,
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
            CornerRadius = o.CornerRadius > 0 ? o.CornerRadius : CornerRadius,
            Font = string.IsNullOrEmpty(o.Font.Family) ? Font : o.Font,
            Opacity = o.Opacity,
        };
    }
}
