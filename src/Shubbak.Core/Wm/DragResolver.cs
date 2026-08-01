using Shubbak.Core.Geometry;
using Shubbak.Core.Tree;

namespace Shubbak.Core.Wm;

/// <summary>What dropping a dragged window should do.</summary>
public enum DropKind
{
    /// <summary>Exchange places with the window underneath.</summary>
    Swap,

    /// <summary>Insert on the leading side of the target, along <see cref="DropTarget.Axis"/>.</summary>
    Before,

    /// <summary>Insert on the trailing side of the target.</summary>
    After,
}

/// <summary>Where a dragged window should land.</summary>
/// <param name="Target">The window underneath the cursor.</param>
/// <param name="Kind">Swap with it, or insert beside it.</param>
/// <param name="Axis">Which axis to insert along; unused for a swap.</param>
public readonly record struct DropTarget(WindowNode Target, DropKind Kind, Axis Axis);

/// <summary>
/// Interprets where a dragged window was released.
/// </summary>
/// <remarks>
/// <para>
/// Dropping onto the middle of another window swaps the two; dropping near an edge
/// inserts beside it. That distinction is what makes dragging genuinely useful
/// rather than merely possible - swap alone cannot express "put this to the left of
/// that", which is most of what anyone actually wants to do with a mouse.
/// </para>
/// <para>
/// Pure geometry over the rectangles the layout engine already computed, so it is
/// testable without a mouse, a window or a running window manager.
/// </para>
/// </remarks>
public static class DragResolver
{
    /// <summary>
    /// How much of each side counts as an edge, as a fraction of the target.
    /// </summary>
    /// <remarks>
    /// A quarter from each side leaves the middle half as the swap zone. Smaller
    /// edges make insertion fiddly to hit; larger ones make a deliberate swap hard,
    /// and swapping is the more common intent.
    /// </remarks>
    public const double EdgeFraction = 0.25;

    /// <summary>
    /// Decides what dropping <paramref name="dragged"/> at a point should do.
    /// </summary>
    /// <param name="workspace">The workspace under the cursor.</param>
    /// <param name="dragged">The window being dragged.</param>
    /// <param name="x">Cursor x, in virtual-desktop coordinates.</param>
    /// <param name="y">Cursor y.</param>
    /// <returns>
    /// The drop, or <see langword="null"/> when the cursor is not over anything the
    /// window could sensibly land on - in which case the caller should put the window
    /// back where it came from.
    /// </returns>
    public static DropTarget? Resolve(WorkspaceNode workspace, WindowNode dragged, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(dragged);

        WindowNode? target = FindTarget(workspace, dragged, x, y);
        if (target is null) return null;

        Rect rect = target.Rect;
        if (rect.IsEmpty) return null;

        // Position within the target, 0 to 1 on each axis.
        double relativeX = (double)(x - rect.Left) / rect.Width;
        double relativeY = (double)(y - rect.Top) / rect.Height;

        // Distance to each edge, as a fraction. The nearest edge wins, so a drop in
        // a corner resolves to whichever side it is closest to rather than being
        // ambiguous.
        double toLeft = relativeX;
        double toRight = 1 - relativeX;
        double toTop = relativeY;
        double toBottom = 1 - relativeY;

        double nearest = Math.Min(Math.Min(toLeft, toRight), Math.Min(toTop, toBottom));

        if (nearest > EdgeFraction) return new DropTarget(target, DropKind.Swap, Axis.Horizontal);

        if (nearest == toLeft) return new DropTarget(target, DropKind.Before, Axis.Horizontal);
        if (nearest == toRight) return new DropTarget(target, DropKind.After, Axis.Horizontal);
        if (nearest == toTop) return new DropTarget(target, DropKind.Before, Axis.Vertical);

        return new DropTarget(target, DropKind.After, Axis.Vertical);
    }

    /// <summary>
    /// The tiled window under the cursor.
    /// </summary>
    /// <remarks>
    /// Falls back to the nearest window by centre distance when the point is over
    /// nothing. Inner gaps mean there is real dead space between tiles, and a drop
    /// that lands in a six-pixel gutter should still do what the user obviously
    /// meant rather than silently snapping back.
    /// </remarks>
    private static WindowNode? FindTarget(WorkspaceNode workspace, WindowNode dragged, int x, int y)
    {
        WindowNode? nearest = null;
        long nearestDistance = long.MaxValue;

        foreach (WindowNode candidate in workspace.DescendantWindows())
        {
            if (ReferenceEquals(candidate, dragged)) continue;
            if (!candidate.IsTiled) continue;
            if (candidate.Rect.IsEmpty) continue;

            if (candidate.Rect.Contains(x, y)) return candidate;

            long dx = candidate.Rect.CenterX - x;
            long dy = candidate.Rect.CenterY - y;
            long distance = (dx * dx) + (dy * dy);

            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = candidate;
            }
        }

        if (nearest is null) return null;

        // Only rescue a near miss. A drop far from every tile - on the desktop, or
        // outside the workspace - is not a drop at all.
        Rect bounds = nearest.Rect.Inflate(Math.Max(nearest.Rect.Width, nearest.Rect.Height) / 4);

        return bounds.Contains(x, y) ? nearest : null;
    }

    /// <summary>
    /// Whether a drag changed a window's size rather than only its position.
    /// </summary>
    /// <param name="before">Geometry when the drag started.</param>
    /// <param name="after">Geometry when it finished.</param>
    /// <param name="tolerance">
    /// Pixels of change to ignore. Windows nudges a window's size by a pixel or two
    /// during some moves, and treating that as a resize would make every drag
    /// silently adjust the layout ratios.
    /// </param>
    public static bool IsResize(Rect before, Rect after, int tolerance = 4) =>
        Math.Abs(before.Width - after.Width) > tolerance ||
        Math.Abs(before.Height - after.Height) > tolerance;

    /// <summary>
    /// Whether a drag moved a window far enough to count.
    /// </summary>
    /// <remarks>
    /// A click on a title bar produces a move of zero or one pixels. Acting on that
    /// would rearrange the layout every time the user merely focused a window by
    /// clicking it, which would be maddening.
    /// </remarks>
    public static bool IsMove(Rect before, Rect after, int tolerance = 8) =>
        Math.Abs(before.X - after.X) > tolerance ||
        Math.Abs(before.Y - after.Y) > tolerance;
}
