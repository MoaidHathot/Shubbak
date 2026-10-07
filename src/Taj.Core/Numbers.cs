using System.Globalization;

namespace Taj.Core;

/// <summary>
/// Reads the number out of a source's value, and writes one back.
/// </summary>
/// <remarks>
/// <para>
/// Sources carry strings, and a string that means a number rarely means only a number:
/// a battery says <c>87%</c>, a script prints <c>cpu 45</c>, a signal arrives as
/// <c>12.5 GB</c>. The first run of digits - with a sign and a decimal point when they
/// are there - is the number, and whatever surrounds it is the label. That is what lets
/// a <c>when above=80</c> be written against a source without first teaching the source
/// to print bare numbers.
/// </para>
/// <para>
/// Invariant culture throughout. The value is written by a program and read by a bar,
/// and neither is in a locale; a German machine's <c>1,5</c> would otherwise read as
/// one and a half here and as one there.
/// </para>
/// </remarks>
public static class Numbers
{
    /// <summary>Finds the first number in a value.</summary>
    /// <param name="text">The value, or null.</param>
    /// <param name="number">The number, when there is one.</param>
    /// <returns>Whether the value holds a number at all.</returns>
    public static bool TryParseFirst(string? text, out double number) =>
        TryParseFirst(text.AsSpan(), out number);

    /// <summary>Finds the first number in a span of text, allocating nothing.</summary>
    public static bool TryParseFirst(ReadOnlySpan<char> span, out double number)
    {
        number = 0;
        if (span.IsEmpty) return false;

        for (int start = 0; start < span.Length; start++)
        {
            if (!StartsNumber(span, start)) continue;

            int end = start + 1;
            bool point = span[start] == '.';

            while (end < span.Length)
            {
                char ch = span[end];

                if (char.IsAsciiDigit(ch))
                {
                    end++;
                    continue;
                }

                // One decimal point, and only when a digit follows: `87.` is eighty-seven
                // and a full stop, not a number that failed to parse.
                if (ch == '.' && !point && end + 1 < span.Length && char.IsAsciiDigit(span[end + 1]))
                {
                    point = true;
                    end++;
                    continue;
                }

                break;
            }

            return double.TryParse(
                span[start..end], NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out number);
        }

        return false;
    }

    /// <summary>The characters that separate one reading from the next in a list.</summary>
    private const string Separators = " \t,;";

    /// <summary>
    /// Every number in a value, in order: a history is a list, and a script that
    /// prints <c>12 15 50</c> or <c>12,15,50</c> has written one.
    /// </summary>
    /// <remarks>
    /// Two passes over spans - one to count, one to fill - rather than a growing list
    /// of substrings. This runs for every sparkline on every rebuild of the tree, and
    /// the tree is rebuilt whenever any value on the bar changes; sixty substrings a
    /// tick for a value that had not changed was most of what a sparkline cost.
    /// </remarks>
    public static IReadOnlyList<double> ParseAll(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        ReadOnlySpan<char> span = text.AsSpan();
        int count = 0;

        foreach (Range range in span.SplitAny(Separators))
        {
            if (TryParseFirst(span[range], out _)) count++;
        }

        if (count == 0) return [];

        var numbers = new double[count];
        int index = 0;

        foreach (Range range in span.SplitAny(Separators))
        {
            if (TryParseFirst(span[range], out double number)) numbers[index++] = number;
        }

        return numbers;
    }

    /// <summary>The format <see cref="Format(double)"/> uses: up to six decimals, none when whole.</summary>
    private const string Compact = "0.######";

    /// <summary>
    /// A number as a value: as many decimals as it has, up to six, and none when it is
    /// whole - so <c>87</c> stays <c>87</c> and a third is <c>0.333333</c> rather than
    /// sixteen digits of it.
    /// </summary>
    public static string Format(double number) =>
        double.IsFinite(number) ? number.ToString(Compact, CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>
    /// The same, written into a span rather than a new string, for a list being built
    /// a reading at a time.
    /// </summary>
    /// <returns>Whether it fit; a span of 32 always holds a finite double in this format.</returns>
    public static bool TryFormat(double number, Span<char> destination, out int written)
    {
        if (!double.IsFinite(number))
        {
            written = 0;
            return true;
        }

        return number.TryFormat(destination, out written, Compact, CultureInfo.InvariantCulture);
    }

    /// <summary>A number to a fixed count of decimals, half away from zero as a person rounds.</summary>
    public static string Format(double number, int decimals)
    {
        if (!double.IsFinite(number)) return string.Empty;

        decimals = Math.Clamp(decimals, 0, 15);

        return Math.Round(number, decimals, MidpointRounding.AwayFromZero)
            .ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    /// <summary>A digit, or a sign or point with a digit right after it.</summary>
    private static bool StartsNumber(ReadOnlySpan<char> span, int at)
    {
        char ch = span[at];

        if (char.IsAsciiDigit(ch)) return true;

        if (ch is '-' or '+' or '.')
        {
            if (at + 1 >= span.Length) return false;

            char next = span[at + 1];

            // `-.5` is a number; `-` on its own, or before a letter, is punctuation.
            return char.IsAsciiDigit(next) || (ch != '.' && next == '.' && at + 2 < span.Length && char.IsAsciiDigit(span[at + 2]));
        }

        return false;
    }
}
