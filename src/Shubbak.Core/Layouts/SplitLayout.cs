using Shubbak.Core.Geometry;
using Shubbak.Core.Tree;

namespace Shubbak.Core.Layouts;

/// <summary>
/// Divides a container's area among its children along one axis, in proportion to
/// each child's <see cref="Node.SizeRatio"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is "manual split" - the default behaviour of i3, sway and GlazeWM, and the
/// only layout shipped in P1. It never restructures the tree: a new window goes
/// beside the focused one and everything else shrinks proportionally. Nesting is
/// created explicitly by the user, not implicitly by the layout, which is what
/// makes the result predictable.
/// </para>
/// <para>
/// Horizontal and vertical are separate instances rather than one layout with a
/// flag, matching i3's <c>splith</c>/<c>splitv</c>. That makes
/// <c>toggle-tiling-direction</c> a plain layout swap on the container and keeps
/// <see cref="ILayout"/> implementations free of modes.
/// </para>
/// </remarks>
public sealed class SplitLayout : ILayout
{
    /// <summary>Children left-to-right.</summary>
    public static SplitLayout Horizontal { get; } = new(Axis.Horizontal);

    /// <summary>Children top-to-bottom.</summary>
    public static SplitLayout Vertical { get; } = new(Axis.Vertical);

    private SplitLayout(Axis axis) => Axis = axis;

    public Axis Axis { get; }

    public string Name => Axis == Axis.Horizontal ? "splith" : "splitv";

    public Axis? PrimaryAxis => Axis;

    /// <summary>The layout for the perpendicular axis.</summary>
    public SplitLayout Transposed => Axis == Axis.Horizontal ? Vertical : Horizontal;

    public void Arrange(ContainerNode container, Rect area, in LayoutOptions options, Span<Rect> destination)
    {
        ArgumentNullException.ThrowIfNull(container);

        IReadOnlyList<Node> children = container.Children;
        int count = children.Count;

        if (count != destination.Length)
            throw new ArgumentException(
                $"Destination length {destination.Length} does not match child count {count}.",
                nameof(destination));

        if (count == 0) return;

        if (count == 1)
        {
            destination[0] = area;
            return;
        }

        Axis main = Axis;
        int totalGap = options.InnerGap * (count - 1);
        int available = Math.Max(0, area.Extent(main) - totalGap);

        Span<int> sizes = count <= 32 ? stackalloc int[count] : new int[count];
        DistributeExact(children, available, sizes);

        // If ratios alone would starve a tile, re-run with a guaranteed floor.
        // Only when it is actually needed: applying the floor unconditionally would
        // distort deliberately lopsided splits that were already perfectly legal.
        int minimum = options.MinimumTileExtent;
        if (minimum > 0 && available >= (long)minimum * count && AnyBelow(sizes, minimum))
            DistributeExactWithFloor(children, available, minimum, sizes);

        int origin = area.Origin(main);
        for (int i = 0; i < count; i++)
        {
            destination[i] = area.WithAxis(main, origin, sizes[i]);
            origin += sizes[i] + options.InnerGap;
        }
    }

    public int ResolveInsertIndex(ContainerNode container, Node? reference)
    {
        ArgumentNullException.ThrowIfNull(container);

        // Beside the focused window, on its trailing side - the i3/GlazeWM
        // convention. Appending instead would make a new window appear far from
        // where the user is looking.
        if (reference is not null)
        {
            int index = container.IndexOf(reference);
            if (index >= 0) return index + 1;
        }

        return container.Children.Count;
    }

    public Node? Navigate(ContainerNode container, Node from, Direction direction)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(from);

        // Movement across this container's axis is not ours to resolve; the caller
        // retries on the parent, which is how focus escapes a nested split.
        if (direction.Axis() != Axis) return null;

        int index = container.IndexOf(from);
        if (index < 0) return null;

        int step = direction.IsForward() ? 1 : -1;

        // Step over siblings that hold nothing tiled. A floating window still
        // occupies a slot in the child list but none on screen, so stopping at it
        // would strand focus on an invisible position.
        for (int i = index + step; i >= 0 && i < container.Children.Count; i += step)
        {
            Node candidate = container.Children[i];
            if (candidate.ParticipatesInTiling) return candidate;
        }

        return null;
    }

    // ---- size distribution -------------------------------------------------

    /// <summary>
    /// Splits <paramref name="available"/> among children by ratio such that the
    /// sizes sum to <paramref name="available"/> exactly.
    /// </summary>
    /// <remarks>
    /// Rounds cumulative edges rather than individual sizes. Rounding each size
    /// independently would lose or gain up to n/2 pixels overall, which shows up as
    /// a visible seam or overlap at the right edge of the screen - and the error
    /// would compound every time the user resized.
    /// </remarks>
    private static void DistributeExact(IReadOnlyList<Node> children, int available, Span<int> sizes)
    {
        double cumulativeRatio = 0;
        int previousEdge = 0;

        for (int i = 0; i < children.Count; i++)
        {
            cumulativeRatio += children[i].SizeRatio;

            // The final edge is pinned rather than computed, so accumulated
            // floating point error can never leak into the total.
            int edge = i == children.Count - 1
                ? available
                : Math.Clamp((int)Math.Round(cumulativeRatio * available), 0, available);

            if (edge < previousEdge) edge = previousEdge;

            sizes[i] = edge - previousEdge;
            previousEdge = edge;
        }
    }

    /// <summary>
    /// As <see cref="DistributeExact"/>, but every child is guaranteed at least
    /// <paramref name="floor"/> pixels, with only the surplus distributed by ratio.
    /// </summary>
    private static void DistributeExactWithFloor(
        IReadOnlyList<Node> children, int available, int floor, Span<int> sizes)
    {
        int count = children.Count;
        int surplus = available - (floor * count);

        double cumulativeRatio = 0;
        int previousEdge = 0;

        for (int i = 0; i < count; i++)
        {
            cumulativeRatio += children[i].SizeRatio;

            int edge = i == count - 1
                ? surplus
                : Math.Clamp((int)Math.Round(cumulativeRatio * surplus), 0, surplus);

            if (edge < previousEdge) edge = previousEdge;

            sizes[i] = floor + (edge - previousEdge);
            previousEdge = edge;
        }
    }

    private static bool AnyBelow(ReadOnlySpan<int> sizes, int minimum)
    {
        foreach (int size in sizes)
            if (size < minimum) return true;
        return false;
    }

    public override string ToString() => Name;
}
