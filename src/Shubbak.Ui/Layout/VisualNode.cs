using Shubbak.Core.Geometry;

namespace Shubbak.Ui.Layout;

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

    /// <summary>Sizing and spacing, in the units the tree was built in; see <see cref="VisualScaling"/>.</summary>
    public BoxStyle Box { get; set; } = BoxStyle.Default;

    public FlexDirection Direction { get; init; } = FlexDirection.Row;

    public JustifyContent Justify { get; init; } = JustifyContent.Start;

    public AlignItems Align { get; init; } = AlignItems.Center;

    /// <summary>Space between children.</summary>
    public int Gap { get; set; }

    /// <summary>Visual styling, interpreted by the renderer.</summary>
    public VisualStyle Style { get; set; } = VisualStyle.Default;

    /// <summary>The picture to draw, for <see cref="VisualKind.Image"/>.</summary>
    public ImageBitmap? Image { get; set; }

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

    /// <summary>The command for a right click, if any.</summary>
    public string? OnRightClick { get; set; }

    /// <summary>The command for a middle click, if any.</summary>
    public string? OnMiddleClick { get; set; }

    /// <summary>Whether the pointer can do anything at all here.</summary>
    public bool IsInteractive =>
        OnClick is { Length: > 0 } || OnRightClick is { Length: > 0 } || OnMiddleClick is { Length: > 0 } ||
        OnScrollUp is { Length: > 0 } || OnScrollDown is { Length: > 0 };

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

    /// <summary>Draws <see cref="VisualNode.Image"/>, scaled to its box.</summary>
    Image,
}

/// <summary>
/// A picture in memory: top-down rows of premultiplied blue-green-red-alpha pixels,
/// each packed into one <see cref="uint"/> as <c>0xAARRGGBB</c>.
/// </summary>
/// <remarks>
/// <para>
/// Renderer-agnostic in the sense that matters: it is bytes, not a handle. A window's
/// icon arrives over the pipe as exactly this, and a renderer that can composite can
/// draw it without asking the operating system for anything. Premultiplied because
/// that is what compositing wants and what the pipe sends; a renderer that needs
/// straight alpha can divide.
/// </para>
/// <para>
/// Immutable once built and shared by reference between rebuilds of the tree, so a
/// widget that decodes one keeps it rather than decoding on every clock tick.
/// </para>
/// </remarks>
public sealed class ImageBitmap
{
    private readonly uint[] _pixels;

    /// <param name="width">Pixels across.</param>
    /// <param name="height">Pixels down.</param>
    /// <param name="pixels"><c>width * height</c> premultiplied BGRA pixels, top-down.</param>
    public ImageBitmap(int width, int height, uint[] pixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(pixels);

        if (pixels.Length != width * height)
            throw new ArgumentException($"expected {width * height} pixels for {width}x{height}, got {pixels.Length}", nameof(pixels));

        Width = width;
        Height = height;
        _pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>The pixels, row after row.</summary>
    public ReadOnlySpan<uint> Pixels => _pixels;

    /// <summary>
    /// Builds one from bytes in the order they travel: blue, green, red, alpha per
    /// pixel. Null when the byte count does not match the size.
    /// </summary>
    public static ImageBitmap? FromBgra(int width, int height, ReadOnlySpan<byte> bgra)
    {
        if (width <= 0 || height <= 0 || bgra.Length != width * height * 4) return null;

        var pixels = new uint[width * height];

        for (int i = 0; i < pixels.Length; i++)
        {
            int offset = i * 4;

            pixels[i] = ((uint)bgra[offset + 3] << 24)
                | ((uint)bgra[offset + 2] << 16)
                | ((uint)bgra[offset + 1] << 8)
                | bgra[offset];
        }

        return new ImageBitmap(width, height, pixels);
    }
}

/// <summary>A width and height.</summary>
public readonly record struct Size(int Width, int Height)
{
    public static Size Empty => default;
}
