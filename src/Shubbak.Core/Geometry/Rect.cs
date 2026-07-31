using System.Globalization;

namespace Shubbak.Core.Geometry;

/// <summary>
/// An integer, screen-space rectangle in virtual-desktop coordinates.
/// </summary>
/// <remarks>
/// <para>
/// Integer rather than floating point deliberately: Win32 window positions are
/// integers, so keeping the model integral means the value a test asserts on is
/// the exact value that will eventually reach <c>DeferWindowPos</c>. Layout
/// arithmetic that needs sub-pixel precision does it internally and rounds once,
/// distributing the remainder (see <c>SplitLayout</c>) so that children always
/// tile their parent exactly with no seams or overlaps.
/// </para>
/// <para>
/// <see cref="X"/>/<see cref="Y"/> may be negative: monitors left of, or above,
/// the primary monitor occupy negative virtual-desktop coordinates.
/// </para>
/// </remarks>
public readonly record struct Rect
{
    public Rect(int x, int y, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);

        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }

    public int Left => X;
    public int Top => Y;
    public int Right => X + Width;
    public int Bottom => Y + Height;

    public int CenterX => X + (Width / 2);
    public int CenterY => Y + (Height / 2);

    public long Area => (long)Width * Height;

    public bool IsEmpty => Width == 0 || Height == 0;

    public static Rect Empty => default;

    /// <summary>Creates a rectangle from edges rather than size.</summary>
    public static Rect FromEdges(int left, int top, int right, int bottom) =>
        new(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));

    /// <summary>The extent along <paramref name="axis"/>.</summary>
    public int Extent(Axis axis) => axis == Axis.Horizontal ? Width : Height;

    /// <summary>The origin along <paramref name="axis"/>.</summary>
    public int Origin(Axis axis) => axis == Axis.Horizontal ? X : Y;

    /// <summary>
    /// Returns a copy with the origin and extent along <paramref name="axis"/>
    /// replaced. Used by layouts to place a child along the main axis while
    /// leaving the cross axis untouched.
    /// </summary>
    public Rect WithAxis(Axis axis, int origin, int extent) => axis == Axis.Horizontal
        ? this with { X = origin, Width = Math.Max(0, extent) }
        : this with { Y = origin, Height = Math.Max(0, extent) };

    /// <summary>
    /// Shrinks the rectangle by <paramref name="gaps"/> on each side, clamping at
    /// zero so an over-large gap yields an empty rectangle rather than throwing.
    /// </summary>
    public Rect Deflate(Gaps gaps) => FromEdges(
        X + gaps.Left,
        Y + gaps.Top,
        Math.Max(X + gaps.Left, Right - gaps.Right),
        Math.Max(Y + gaps.Top, Bottom - gaps.Bottom));

    /// <summary>Shrinks the rectangle by <paramref name="amount"/> on all sides.</summary>
    public Rect Deflate(int amount) => Deflate(Gaps.All(amount));

    public Rect Inflate(int amount) => FromEdges(
        X - amount, Y - amount, Right + amount, Bottom + amount);

    public Rect Translate(int dx, int dy) => this with { X = X + dx, Y = Y + dy };

    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;

    public bool IntersectsWith(Rect other) =>
        other.Left < Right && Left < other.Right &&
        other.Top < Bottom && Top < other.Bottom;

    public Rect Intersect(Rect other)
    {
        int left = Math.Max(Left, other.Left);
        int top = Math.Max(Top, other.Top);
        int right = Math.Min(Right, other.Right);
        int bottom = Math.Min(Bottom, other.Bottom);
        return right <= left || bottom <= top ? Empty : FromEdges(left, top, right, bottom);
    }

    /// <summary>The smallest rectangle containing both.</summary>
    public Rect Union(Rect other)
    {
        if (IsEmpty) return other;
        if (other.IsEmpty) return this;
        return FromEdges(
            Math.Min(Left, other.Left), Math.Min(Top, other.Top),
            Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom));
    }

    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture, $"({X},{Y} {Width}x{Height})");
}
