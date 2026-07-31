using Shubbak.Core.Geometry;

namespace Shubbak.Core.Tree;

/// <summary>
/// Resolves directional focus and movement across arbitrarily nested containers.
/// </summary>
/// <remarks>
/// <para>
/// The algorithm is i3's, in two phases:
/// </para>
/// <list type="number">
///   <item><b>Ascend.</b> Walk up from the focused node. At each container ask its
///   layout whether the movement can be satisfied among that container's own
///   children. A vertical split cannot answer "move right", so the question passes
///   to its parent - which is exactly how focus escapes a nested split.</item>
///   <item><b>Descend.</b> Having found a sibling to move into, drill down to a
///   leaf window.</item>
/// </list>
/// <para>
/// The descent picks the child whose span best overlaps the origin along the
/// <i>cross</i> axis, rather than i3's "most recently focused". Geometric descent
/// is predictable from the screen alone: moving right into a column of three lands
/// on the one physically beside you. Recency-based descent requires the user to
/// remember invisible state, which is a common source of "focus went somewhere
/// odd" complaints.
/// </para>
/// </remarks>
public static class FocusNavigator
{
    /// <summary>
    /// The window that focus should move to from <paramref name="from"/>, or
    /// <see langword="null"/> when nothing lies that way within the workspace.
    /// </summary>
    /// <remarks>
    /// Confined to the workspace deliberately. Crossing to another monitor is a
    /// separate decision involving monitor geometry and workspace activation, and
    /// belongs to the command layer rather than here.
    /// </remarks>
    public static WindowNode? Navigate(WindowNode from, Direction direction)
    {
        ArgumentNullException.ThrowIfNull(from);

        Node? target = NavigateToNode(from, direction);
        return target is null ? null : DescendToWindow(target, from.Rect, direction);
    }

    /// <summary>
    /// The sibling subtree that lies in <paramref name="direction"/>, before
    /// descending into it.
    /// </summary>
    /// <remarks>
    /// Exposed separately because <c>move --direction</c> needs the subtree rather
    /// than the leaf: moving a window right must place it beside the neighbouring
    /// container, not inside whichever leaf happens to be nearest.
    /// </remarks>
    public static Node? NavigateToNode(Node from, Direction direction)
    {
        ArgumentNullException.ThrowIfNull(from);

        Node current = from;

        while (current.ParentContainer is { } parent)
        {
            Node? sibling = parent.Layout.Navigate(parent, current, direction);
            if (sibling is not null) return sibling;

            // This container cannot satisfy the movement. Stop at the workspace
            // boundary rather than escaping into the monitor.
            if (parent is WorkspaceNode) return null;

            current = parent;
        }

        return null;
    }

    /// <summary>
    /// Descends into <paramref name="node"/> to the leaf window that best lines up
    /// with <paramref name="origin"/>.
    /// </summary>
    /// <param name="node">The subtree being entered.</param>
    /// <param name="origin">The rectangle focus is arriving from.</param>
    /// <param name="direction">The direction of travel.</param>
    public static WindowNode? DescendToWindow(Node node, Rect origin, Direction direction)
    {
        ArgumentNullException.ThrowIfNull(node);

        Node current = node;

        while (true)
        {
            switch (current)
            {
                case WindowNode window:
                    return window.IsTiled ? window : null;

                case ContainerNode container:
                {
                    Node? next = ChooseEntryChild(container, origin, direction);
                    if (next is null) return FirstTiledWindow(container);
                    current = next;
                    break;
                }

                default:
                    return null;
            }
        }
    }

    /// <summary>
    /// Picks which child of <paramref name="container"/> to enter.
    /// </summary>
    private static Node? ChooseEntryChild(ContainerNode container, Rect origin, Direction direction)
    {
        List<Node> candidates = [.. container.Children.Where(HasTiledWindow)];
        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0];

        Axis containerAxis = container.Layout.PrimaryAxis ?? direction.Axis();

        // Entering along the container's own axis: take the near edge, so moving
        // right enters a row at its leftmost child.
        if (containerAxis == direction.Axis())
            return direction.IsForward() ? candidates[0] : candidates[^1];

        // Entering across the container's axis: pick by overlap with the origin, so
        // moving right into a column lands on the child physically beside you.
        Node best = candidates[0];
        long bestOverlap = long.MinValue;
        long bestDistance = long.MaxValue;

        Axis crossAxis = containerAxis;

        foreach (Node candidate in candidates)
        {
            long overlap = Overlap(origin, candidate.Rect, crossAxis);
            long distance = Math.Abs(Centre(candidate.Rect, crossAxis) - Centre(origin, crossAxis));

            if (overlap > bestOverlap || (overlap == bestOverlap && distance < bestDistance))
            {
                bestOverlap = overlap;
                bestDistance = distance;
                best = candidate;
            }
        }

        return best;
    }

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

    private static bool HasTiledWindow(Node node) => node.ParticipatesInTiling;

    private static WindowNode? FirstTiledWindow(Node node) =>
        node.DescendantWindows().FirstOrDefault(w => w.IsTiled);

    /// <summary>
    /// The next or previous tiled window in flat tree order, wrapping at the ends.
    /// </summary>
    /// <remarks>
    /// Backs <c>focus --next</c>/<c>--prev</c>: a linear cycle through every window
    /// on the workspace regardless of nesting, for users who prefer that to
    /// directional movement.
    /// </remarks>
    public static WindowNode? Cycle(WorkspaceNode workspace, WindowNode? current, bool forward)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        List<WindowNode> windows = [.. workspace.DescendantWindows().Where(w => w.IsTiled)];
        if (windows.Count == 0) return null;

        if (current is null) return forward ? windows[0] : windows[^1];

        int index = windows.IndexOf(current);
        if (index < 0) return forward ? windows[0] : windows[^1];

        int next = forward ? index + 1 : index - 1;
        if (next < 0) next = windows.Count - 1;
        else if (next >= windows.Count) next = 0;

        return windows[next];
    }
}
