using Shubbak.Core.Geometry;
using Shubbak.Core.Tree;

namespace Shubbak.Core.Layouts;

/// <summary>
/// Shared helpers for layouts that divide a rectangle among children.
/// </summary>
internal static class LayoutMath
{
    /// <summary>
    /// Splits <paramref name="available"/> among the children by ratio, such that
    /// the sizes sum to <paramref name="available"/> exactly.
    /// </summary>
    /// <remarks>
    /// Rounds cumulative edges rather than individual sizes. Rounding each size
    /// independently loses or gains up to n/2 pixels overall, which shows up as a
    /// seam at the screen edge and compounds on every resize.
    /// </remarks>
    public static void DistributeExact(
        IReadOnlyList<Node> children, int available, Span<int> sizes)
    {
        double cumulative = 0;
        int previousEdge = 0;
        int count = children.Count;

        for (int i = 0; i < count; i++)
        {
            cumulative += children[i].SizeRatio;

            // The final edge is pinned rather than computed, so accumulated
            // floating point error can never leak into the total.
            int edge = i == count - 1
                ? available
                : Math.Clamp((int)Math.Round(cumulative * available), 0, available);

            if (edge < previousEdge) edge = previousEdge;

            sizes[i] = edge - previousEdge;
            previousEdge = edge;
        }
    }

    /// <summary>Splits evenly, distributing the remainder across the first slots.</summary>
    public static void DistributeEvenly(int available, Span<int> sizes)
    {
        int count = sizes.Length;
        if (count == 0) return;

        int each = available / count;
        int remainder = available - (each * count);

        for (int i = 0; i < count; i++)
            sizes[i] = each + (i < remainder ? 1 : 0);
    }

    /// <summary>Lays children out along one axis, given their sizes.</summary>
    public static void PlaceAlongAxis(
        Rect area, Axis axis, ReadOnlySpan<int> sizes, int gap, Span<Rect> destination)
    {
        int origin = area.Origin(axis);

        for (int i = 0; i < sizes.Length; i++)
        {
            destination[i] = area.WithAxis(axis, origin, sizes[i]);
            origin += sizes[i] + gap;
        }
    }

    /// <summary>Splits a rectangle in two along an axis, at a ratio.</summary>
    public static (Rect First, Rect Second) Split(Rect area, Axis axis, double ratio, int gap)
    {
        int extent = Math.Max(0, area.Extent(axis) - gap);
        int first = Math.Clamp((int)Math.Round(extent * ratio), 0, extent);

        int origin = area.Origin(axis);

        return (
            area.WithAxis(axis, origin, first),
            area.WithAxis(axis, origin + first + gap, extent - first));
    }

    /// <summary>The tiled children of a container, in order.</summary>
    public static List<Node> TiledChildren(ContainerNode container)
    {
        List<Node> tiled = [];

        foreach (Node child in container.Children)
            if (child.ParticipatesInTiling) tiled.Add(child);

        return tiled;
    }
}
