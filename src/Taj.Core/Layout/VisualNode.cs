using Shubbak.Core.Geometry;

namespace Taj.Core.Layout;

/// <summary>How a container arranges its children.</summary>
public enum FlexDirection
{
    Row,
    Column,
}

/// <summary>How spare space along the main axis is distributed.</summary>
public enum JustifyContent
{
    Start,
    Center,
    End,

    /// <summary>Equal space between items, none at the ends.</summary>
    SpaceBetween,

    /// <summary>Equal space around each item, so ends get half.</summary>
    SpaceAround,
}

/// <summary>How children are positioned on the cross axis.</summary>
public enum AlignItems
{
    Start,
    Center,
    End,

    /// <summary>Fill the cross axis.</summary>
    Stretch,
}

/// <summary>Per-edge spacing.</summary>
public readonly record struct Edges(int Left, int Top, int Right, int Bottom)
{
    public static Edges Zero => default;

    public static Edges All(int amount) => new(amount, amount, amount, amount);

    public static Edges Symmetric(int horizontal, int vertical) =>
        new(horizontal, vertical, horizontal, vertical);

    public int Horizontal => Left + Right;

    public int Vertical => Top + Bottom;
}

/// <summary>Sizing and spacing for one node.</summary>
/// <param name="Width">Fixed width, or null to size to content.</param>
/// <param name="Height">Fixed height, or null to size to content.</param>
/// <param name="MinWidth">Lower bound on width.</param>
/// <param name="MaxWidth">Upper bound on width; content is clipped beyond it.</param>
/// <param name="Grow">
/// Share of leftover main-axis space. Zero means the node keeps its content size.
/// </param>
/// <param name="NoShrink">
/// Whether the node refuses to be shrunk below its content size when space runs out.
/// </param>
/// <param name="Padding">Space inside the node, around its content.</param>
/// <param name="Margin">Space outside the node.</param>
/// <remarks>
/// <see cref="NoShrink"/> is phrased negatively on purpose. This is a struct, and
/// <c>default(BoxStyle)</c> zeroes every field regardless of the parameter defaults
/// written here - so a positively-phrased <c>Shrink</c> would silently be
/// <see langword="false"/> for every node that did not name it, which is the
/// opposite of flexbox's default and produces layouts that overflow rather than
/// compress.
/// </remarks>
public readonly record struct BoxStyle(
    int? Width = null,
    int? Height = null,
    int MinWidth = 0,
    int? MaxWidth = null,
    double Grow = 0,
    bool NoShrink = false,
    Edges Padding = default,
    Edges Margin = default)
{
    public static BoxStyle Default => new();

    /// <summary>Whether the node may be compressed when space runs out.</summary>
    public bool CanShrink => !NoShrink;
}

/// <summary>
/// A node in the bar's visual tree.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately renderer-agnostic. This type and the layout that operates on it know
/// nothing about Direct2D, Win32 or any drawing API - they compute rectangles and
/// nothing more. That is what makes the renderer swappable and, just as usefully,
/// what makes the whole layout engine testable without a window on screen.
/// </para>
/// <para>
/// The model is a small, well-understood subset of flexbox. A bar is a constrained
/// UI - nested rows and columns of text, icons and small graphs - so implementing
/// grid, floats or absolute positioning would be work that never pays for itself.
/// </para>
/// </remarks>
public sealed class VisualNode
{
    /// <summary>Identifier used by config, styling and hit testing.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>What kind of content this node draws.</summary>
    public VisualKind Kind { get; init; } = VisualKind.Container;

    /// <summary>Text to draw, for <see cref="VisualKind.Text"/>.</summary>
    public string Text { get; set; } = string.Empty;

    public BoxStyle Box { get; init; } = BoxStyle.Default;

    public FlexDirection Direction { get; init; } = FlexDirection.Row;

    public JustifyContent Justify { get; init; } = JustifyContent.Start;

    public AlignItems Align { get; init; } = AlignItems.Center;

    /// <summary>Space between children.</summary>
    public int Gap { get; init; }

    /// <summary>Visual styling, interpreted by the renderer.</summary>
    public VisualStyle Style { get; set; } = VisualStyle.Default;

    /// <summary>Whether this node and its children are laid out and drawn at all.</summary>
    public bool Visible { get; set; } = true;

    public List<VisualNode> Children { get; init; } = [];

    /// <summary>
    /// The command to send to the window manager when clicked, if any.
    /// </summary>
    /// <remarks>
    /// A command string rather than a callback, so a click goes through exactly the
    /// same path as a keybinding. Clicking a workspace and pressing its key cannot
    /// then behave differently.
    /// </remarks>
    public string? OnClick { get; set; }

    /// <summary>
    /// Style to use while the pointer is over this node.
    /// </summary>
    /// <remarks>
    /// Null means the node does not react, which is right for anything that is not
    /// clickable: highlighting a label the user cannot press only suggests it does
    /// something.
    /// </remarks>
    public VisualStyle? HoverStyle { get; set; }

    /// <summary>The command for a scroll-up, if any.</summary>
    public string? OnScrollUp { get; set; }

    /// <summary>The command for a scroll-down, if any.</summary>
    public string? OnScrollDown { get; set; }

    /// <summary>Computed position, filled in by <see cref="FlexLayout"/>.</summary>
    public Rect Rect { get; internal set; }

    /// <summary>Measured content size, filled in during layout.</summary>
    public Size ContentSize { get; internal set; }

    public VisualNode Add(VisualNode child)
    {
        ArgumentNullException.ThrowIfNull(child);
        Children.Add(child);
        return this;
    }

    /// <summary>This node and every descendant, parents first.</summary>
    public IEnumerable<VisualNode> SelfAndDescendants()
    {
        yield return this;

        foreach (VisualNode child in Children)
            foreach (VisualNode descendant in child.SelfAndDescendants())
                yield return descendant;
    }

    /// <summary>
    /// The deepest visible node containing the point, or null.
    /// </summary>
    /// <remarks>
    /// Depth-first and last-child-first, so the node drawn on top wins - which is
    /// what a user expects when clicking overlapping content.
    /// </remarks>
    public VisualNode? HitTest(int x, int y)
    {
        if (!Visible || !Rect.Contains(x, y)) return null;

        for (int i = Children.Count - 1; i >= 0; i--)
            if (Children[i].HitTest(x, y) is { } hit) return hit;

        return this;
    }

    public override string ToString() => $"{Kind}#{Id} {Rect} \"{Text}\"";
}

/// <summary>What a node draws.</summary>
public enum VisualKind
{
    /// <summary>Lays out children; draws only its background and border.</summary>
    Container,

    /// <summary>Draws <see cref="VisualNode.Text"/>.</summary>
    Text,

    /// <summary>Draws nothing; used for fixed or flexible gaps.</summary>
    Spacer,
}

/// <summary>A width and height.</summary>
public readonly record struct Size(int Width, int Height)
{
    public static Size Empty => default;
}
