using Shubbak.Core.Geometry;
using Shubbak.Core.Tree;

namespace Shubbak.Core.Layouts;

/// <summary>
/// Arranges children in a grid of roughly equal cells.
/// </summary>
/// <remarks>
/// Useful when several windows matter equally - comparing documents, watching
/// several logs - where a master area would misrepresent the intent. Column count
/// follows the square root of the child count, so the grid stays close to square as
/// windows come and go.
/// </remarks>
public sealed class GridLayout : ILayout
{
    public static GridLayout Instance { get; } = new();

    private GridLayout() { }

    public string Name => "grid";

    /// <summary>Null: a grid extends in both directions equally.</summary>
    public Axis? PrimaryAxis => null;

    public void Arrange(ContainerNode container, Rect area, in LayoutOptions options, Span<Rect> destination)
    {
        ArgumentNullException.ThrowIfNull(container);

        int count = container.Children.Count;

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

        int columns = (int)Math.Ceiling(Math.Sqrt(count));
        int rows = (int)Math.Ceiling((double)count / columns);

        int gap = options.InnerGap;

        Span<int> rowHeights = rows <= 32 ? stackalloc int[rows] : new int[rows];
        LayoutMath.DistributeEvenly(Math.Max(0, area.Height - (gap * (rows - 1))), rowHeights);

        int y = area.Y;
        int placed = 0;

        // Allocated once, outside the loop: a stackalloc per row would grow the
        // frame with the row count.
        Span<int> widths = columns <= 32 ? stackalloc int[columns] : new int[columns];

        for (int row = 0; row < rows; row++)
        {
            // The last row may be short, and stretching its cells to fill the width
            // looks far better than leaving a ragged gap.
            int cellsInRow = Math.Min(columns, count - placed);
            if (cellsInRow <= 0) break;

            Span<int> rowWidths = widths[..cellsInRow];
            LayoutMath.DistributeEvenly(Math.Max(0, area.Width - (gap * (cellsInRow - 1))), rowWidths);

            int x = area.X;

            for (int column = 0; column < cellsInRow; column++)
            {
                destination[placed++] = new Rect(x, y, rowWidths[column], rowHeights[row]);
                x += rowWidths[column] + gap;
            }

            y += rowHeights[row] + gap;
        }
    }

    public int ResolveInsertIndex(ContainerNode container, Node? reference)
    {
        ArgumentNullException.ThrowIfNull(container);

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

        return GeometricNavigator.Navigate(container, from, direction);
    }

    public override string ToString() => Name;
}

/// <summary>
/// Shows only the focused window, at full size.
/// </summary>
/// <remarks>
/// <para>
/// Every child gets the whole area; z-order decides what is seen. Deliberately not
/// implemented by hiding the others: they stay laid out, so leaving monocle mode is
/// instantaneous and nothing has to be re-shown.
/// </para>
/// <para>
/// This differs from fullscreen in that it is a property of the <i>container</i>,
/// so it composes - a monocle container can sit inside a split alongside other
/// windows.
/// </para>
/// </remarks>
public sealed class MonocleLayout : ILayout
{
    public static MonocleLayout Instance { get; } = new();

    private MonocleLayout() { }

    public string Name => "monocle";

    public Axis? PrimaryAxis => null;

    public void Arrange(ContainerNode container, Rect area, in LayoutOptions options, Span<Rect> destination)
    {
        ArgumentNullException.ThrowIfNull(container);

        if (container.Children.Count != destination.Length)
            throw new ArgumentException(
                $"Destination length {destination.Length} does not match child count {container.Children.Count}.",
                nameof(destination));

        destination.Fill(area);
    }

    public int ResolveInsertIndex(ContainerNode container, Node? reference)
    {
        ArgumentNullException.ThrowIfNull(container);

        if (reference is not null)
        {
            int index = container.IndexOf(reference);
            if (index >= 0) return index + 1;
        }

        return container.Children.Count;
    }

    /// <summary>
    /// Directional movement cycles through the stack.
    /// </summary>
    /// <remarks>
    /// Since every window occupies the same rectangle, "right" cannot mean anything
    /// spatial. Mapping forward directions to "next" keeps the usual keys working
    /// rather than leaving them inert.
    /// </remarks>
    public Node? Navigate(ContainerNode container, Node from, Direction direction)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(from);

        int index = container.IndexOf(from);
        if (index < 0) return null;

        int target = direction.IsForward() ? index + 1 : index - 1;
        return target >= 0 && target < container.Children.Count ? container.Children[target] : null;
    }

    public override string ToString() => Name;
}
