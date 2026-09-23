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

    /// <summary>
    /// The accent colour Windows ships with, used for <c>accent</c> when no host has
    /// said what the machine's actually is.
    /// </summary>
    public static Colour DefaultAccent => new(0x00, 0x78, 0xD4);

    /// <summary>
    /// Reads the machine's accent colour, supplied by a host that can ask the
    /// compositor. Null, or a null answer, falls back to <see cref="DefaultAccent"/>.
    /// </summary>
    /// <remarks>
    /// A hook rather than a call, because this type is shared by every process and
    /// the core is deliberately free of Win32. Asked on every parse rather than once,
    /// so a config re-read after the user changes their accent sees the new one.
    /// </remarks>
    public static Func<Colour?>? AccentSource { get; set; }

    /// <summary>The machine's accent colour, opaque.</summary>
    public static Colour Accent => (AccentSource?.Invoke() ?? DefaultAccent) with { A = 255 };

    /// <summary>The words <see cref="TryParse"/> accepts in place of a hex colour.</summary>
    public static IReadOnlyList<string> Names { get; } = ["accent"];

    public bool IsTransparent => A == 0;

    /// <summary>
    /// Whether the colour reads as dark: relative luminance under a half, so text laid
    /// on it should be light. The one question every pill has to answer before it
    /// picks a text colour, and the accent - which may be any colour Windows is set
    /// to - is the one fill that cannot be assumed either way.
    /// </summary>
    public bool IsDark => ((0.2126 * R) + (0.7152 * G) + (0.0722 * B)) / 255.0 < 0.5;

    /// <summary>
    /// Parses <c>#RGB</c>, <c>#RRGGBB</c>, <c>#RRGGBBAA</c> or a named colour, each
    /// optionally followed by an opacity: <c>#8dbcff 40%</c>, <c>accent 25%</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Alpha last, matching CSS rather than Win32's <c>#AARRGGBB</c>. Config is
    /// written by people who know CSS, and silently reinterpreting their colours
    /// would be a baffling class of bug.
    /// </para>
    /// <para>
    /// The percentage scales whatever opacity the colour already has, which for the
    /// ordinary opaque case simply sets it. It exists for the named colours - there
    /// is no hex to append an alpha to - and is allowed after a hex colour so that one
    /// rule covers both.
    /// </para>
    /// </remarks>
    public static bool TryParse(string? text, out Colour colour)
    {
        colour = default;

        if (string.IsNullOrWhiteSpace(text)) return false;

        ReadOnlySpan<char> span = text.AsSpan().Trim();

        // An opacity, if the last word is one: "40%".
        double opacity = 1.0;
        int lastSpace = span.LastIndexOfAny(' ', '\t');

        if (lastSpace > 0 && span[^1] == '%')
        {
            ReadOnlySpan<char> percent = span[(lastSpace + 1)..^1];

            if (!int.TryParse(percent, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out int value) || value > 100)
            {
                return false;
            }

            opacity = value / 100.0;
            span = span[..lastSpace].TrimEnd();
        }

        if (!TryParseHex(span, out colour) && !TryParseName(span, out colour)) return false;

        if (opacity < 1.0) colour = colour with { A = (byte)Math.Round(colour.A * opacity) };

        return true;
    }

    private static bool TryParseHex(ReadOnlySpan<char> span, out Colour colour)
    {
        colour = default;

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

    /// <summary>
    /// The words a colour may be written as. One so far: <c>accent</c>, the colour
    /// Windows is set to, so a bar or a border can follow the machine rather than
    /// hard-coding a blue that stops matching the moment the user picks a green.
    /// </summary>
    private static bool TryParseName(ReadOnlySpan<char> span, out Colour colour)
    {
        if (span.Equals("accent", StringComparison.OrdinalIgnoreCase))
        {
            colour = Accent;
            return true;
        }

        colour = default;
        return false;
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
