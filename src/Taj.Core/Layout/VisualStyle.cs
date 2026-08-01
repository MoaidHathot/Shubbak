namespace Taj.Core.Layout;

/// <summary>An RGBA colour.</summary>
/// <remarks>
/// Stored as bytes rather than floats: the values come from config as hex, and the
/// renderer wants them per channel anyway.
/// </remarks>
public readonly record struct Colour(byte R, byte G, byte B, byte A = 255)
{
    public static Colour Transparent => new(0, 0, 0, 0);

    public static Colour White => new(255, 255, 255);

    public static Colour Black => new(0, 0, 0);

    public bool IsTransparent => A == 0;

    /// <summary>
    /// Parses <c>#RGB</c>, <c>#RRGGBB</c> or <c>#RRGGBBAA</c>.
    /// </summary>
    /// <remarks>
    /// Alpha last, matching CSS rather than Win32's <c>#AARRGGBB</c>. Config is
    /// written by people who know CSS, and silently reinterpreting their colours
    /// would be a baffling class of bug.
    /// </remarks>
    public static bool TryParse(string? text, out Colour colour)
    {
        colour = default;

        if (string.IsNullOrWhiteSpace(text)) return false;

        ReadOnlySpan<char> span = text.AsSpan().Trim();
        if (span.Length > 0 && span[0] == '#') span = span[1..];

        switch (span.Length)
        {
            case 3:
            {
                if (!Nibble(span[0], out int r) || !Nibble(span[1], out int g) || !Nibble(span[2], out int b))
                    return false;

                // #abc means #aabbcc, as in CSS.
                colour = new Colour((byte)(r * 17), (byte)(g * 17), (byte)(b * 17));
                return true;
            }

            case 6 or 8:
            {
                if (!Byte(span[0], span[1], out byte r) ||
                    !Byte(span[2], span[3], out byte g) ||
                    !Byte(span[4], span[5], out byte b))
                {
                    return false;
                }

                byte a = 255;
                if (span.Length == 8 && !Byte(span[6], span[7], out a)) return false;

                colour = new Colour(r, g, b, a);
                return true;
            }

            default:
                return false;
        }
    }

    /// <summary>Blends towards another colour; <c>t</c> of 0 is this colour.</summary>
    public Colour Lerp(Colour other, double t)
    {
        t = Math.Clamp(t, 0, 1);

        return new Colour(
            (byte)Math.Round(R + ((other.R - R) * t)),
            (byte)Math.Round(G + ((other.G - G) * t)),
            (byte)Math.Round(B + ((other.B - B) * t)),
            (byte)Math.Round(A + ((other.A - A) * t)));
    }

    /// <summary>The colour with a different alpha.</summary>
    public Colour WithAlpha(byte alpha) => this with { A = alpha };

    private static bool Nibble(char c, out int value)
    {
        value = c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1,
        };

        return value >= 0;
    }

    private static bool Byte(char high, char low, out byte value)
    {
        value = 0;
        if (!Nibble(high, out int h) || !Nibble(low, out int l)) return false;

        value = (byte)((h << 4) | l);
        return true;
    }

    public override string ToString() =>
        A == 255 ? $"#{R:X2}{G:X2}{B:X2}" : $"#{R:X2}{G:X2}{B:X2}{A:X2}";
}

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
