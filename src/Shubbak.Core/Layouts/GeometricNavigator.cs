using Shubbak.Core.Geometry;
using Shubbak.Core.Tree;

namespace Shubbak.Core.Layouts;

/// <summary>
/// Directional navigation by rectangle geometry rather than by list order.
/// </summary>
/// <remarks>
/// Split layouts can answer "what is to my right?" from their child list, because
/// the list <i>is</i> the screen order. Layouts that arrange children in two
/// dimensions - fibonacci, grid, bsp - cannot: the child at index 3 may be anywhere.
/// For those, direction has to be resolved from the rectangles themselves.
/// </remarks>
internal static class GeometricNavigator
{
    /// <summary>
    /// The sibling nearest <paramref name="from"/> in <paramref name="direction"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A candidate qualifies only if its near edge lies beyond the origin's far
    /// edge, so a window that merely overlaps is never considered "to the right of"
    /// another. Among those, the one closest along the travel axis wins, with
    /// cross-axis overlap breaking ties.
    /// </para>
    /// <para>
    /// Preferring overlap over raw distance is what makes this feel right: moving
    /// right from a tall window should land on whatever is beside it, not on
    /// something nearer in a straight line but vertically elsewhere.
    /// </para>
    /// </remarks>
    public static Node? Navigate(ContainerNode container, Node from, Direction direction)
    {
        Rect origin = from.Rect;
        if (origin.IsEmpty) return null;

        Axis travel = direction.Axis();
        Axis cross = travel.Cross();

        Node? best = null;
        long bestDistance = long.MaxValue;
        long bestOverlap = -1;

        foreach (Node candidate in container.Children)
        {
            if (ReferenceEquals(candidate, from)) continue;
            if (!candidate.ParticipatesInTiling) continue;

            Rect target = candidate.Rect;
            if (target.IsEmpty) continue;

            if (!LiesBeyond(origin, target, direction)) continue;

            long distance = Math.Abs(Centre(target, travel) - Centre(origin, travel));
            long overlap = Overlap(origin, target, cross);

            // Any overlapping candidate beats any non-overlapping one, regardless of
            // distance; among equals, nearest wins.
            bool better =
                (overlap > 0 && bestOverlap <= 0) ||
                (overlap > 0 == bestOverlap > 0 && distance < bestDistance);

            if (better)
            {
                best = candidate;
                bestDistance = distance;
                bestOverlap = overlap;
            }
        }

        return best;
    }

    private static bool LiesBeyond(Rect origin, Rect target, Direction direction) => direction switch
    {
        Direction.Left => target.CenterX < origin.Left,
        Direction.Right => target.CenterX >= origin.Right,
        Direction.Up => target.CenterY < origin.Top,
        _ => target.CenterY >= origin.Bottom,
    };

    private static long Overlap(Rect a, Rect b, Axis axis)
    {
        (int aStart, int aEnd) = Span(a, axis);
        (int bStart, int bEnd) = Span(b, axis);
        return Math.Max(0, Math.Min(aEnd, bEnd) - Math.Max(aStart, bStart));
    }

    private static (int Start, int End) Span(Rect rect, Axis axis) =>
        axis == Axis.Horizontal ? (rect.Left, rect.Right) : (rect.Top, rect.Bottom);

    private static long Centre(Rect rect, Axis axis) =>
        axis == Axis.Horizontal ? rect.CenterX : rect.CenterY;
}
