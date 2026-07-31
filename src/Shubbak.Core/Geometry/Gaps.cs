using System.Globalization;

namespace Shubbak.Core.Geometry;

/// <summary>
/// Per-edge spacing, in physical pixels.
/// </summary>
/// <remarks>
/// Shubbak distinguishes two uses, matching GlazeWM's vocabulary:
/// <list type="bullet">
///   <item><b>Outer gap</b> - between the workspace and the monitor's work area.
///   Applied once, by the layout engine, before any layout runs.</item>
///   <item><b>Inner gap</b> - between adjacent sibling windows. Applied by each
///   layout as it divides its area. Represented as a single scalar, since a gap
///   between two tiles has no meaningful per-edge asymmetry.</item>
/// </list>
/// </remarks>
public readonly record struct Gaps
{
    public Gaps(int left, int top, int right, int bottom)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(left);
        ArgumentOutOfRangeException.ThrowIfNegative(top);
        ArgumentOutOfRangeException.ThrowIfNegative(right);
        ArgumentOutOfRangeException.ThrowIfNegative(bottom);

        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }

    public int Left { get; init; }
    public int Top { get; init; }
    public int Right { get; init; }
    public int Bottom { get; init; }

    public static Gaps None => default;

    public static Gaps All(int amount) => new(amount, amount, amount, amount);

    public static Gaps Symmetric(int horizontal, int vertical) =>
        new(horizontal, vertical, horizontal, vertical);

    public int Horizontal => Left + Right;
    public int Vertical => Top + Bottom;

    public bool IsZero => Left == 0 && Top == 0 && Right == 0 && Bottom == 0;

    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture, $"[l={Left} t={Top} r={Right} b={Bottom}]");
}
