namespace Shubbak.Core.Rendering;

/// <summary>An RGBA colour.</summary>
/// <remarks>
/// <para>
/// Stored as bytes rather than floats: the values come from config as hex, and the
/// renderer wants them per channel anyway.
/// </para>
/// <para>
/// Lives in the core so that both binaries share one parser. It began in the bar,
/// and the window manager grew a second, shorter copy for its focus borders - which
/// accepted eight hex digits and then silently discarded the alpha, so a border
/// written with one was quietly not the colour that was asked for.
/// </para>
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
