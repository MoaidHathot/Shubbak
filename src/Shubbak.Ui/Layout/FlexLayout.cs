using Shubbak.Core.Geometry;

namespace Shubbak.Ui.Layout;

/// <summary>
/// Measures text so layout can size nodes to their content.
/// </summary>
/// <remarks>
/// An interface because measurement genuinely needs the renderer - only DirectWrite
/// knows how wide a string is in a given font. Keeping it behind an interface is what
/// lets the layout engine be tested with a predictable stub, and it is the only place
/// the renderer leaks into the layout.
/// </remarks>
public interface ITextMeasurer
{
    /// <summary>The size <paramref name="text"/> would occupy.</summary>
    Size Measure(string text, FontStyle font);
}

/// <summary>
/// A flexbox-subset layout engine.
/// </summary>
/// <remarks>
/// <para>
/// Two passes, as flexbox requires: measure content sizes bottom-up, then assign
/// positions top-down once the available space is known. A single pass cannot work,
/// because a node's position depends on its siblings' sizes and those depend on
/// their own children.
/// </para>
/// <para>
/// Supported: row and column direction, grow and shrink, justify, align, gap,
/// padding, margin, fixed and minimum and maximum sizes. Not supported: wrapping,
/// grid, absolute positioning, aspect ratios. A bar is nested rows and columns of
/// text and small graphics; the rest would be work that never pays for itself.
/// </para>
/// </remarks>
public sealed class FlexLayout
{
    private readonly ITextMeasurer _measurer;

    public FlexLayout(ITextMeasurer measurer) =>
        _measurer = measurer ?? throw new ArgumentNullException(nameof(measurer));

    /// <summary>Lays out a tree within the given bounds.</summary>
    public void Arrange(VisualNode root, Rect bounds)
    {
        ArgumentNullException.ThrowIfNull(root);

        Measure(root);
        Place(root, bounds);
    }

    // ---- pass one: measure -------------------------------------------------

    /// <summary>
    /// Computes each node's natural content size, depth first.
    /// </summary>
    private Size Measure(VisualNode node)
    {
        if (!node.Visible)
        {
            node.ContentSize = Size.Empty;
            return Size.Empty;
        }

        Size content = node.Kind switch
        {
            VisualKind.Text => _measurer.Measure(node.Text, node.Style.Font),
            VisualKind.Spacer => Size.Empty,
            VisualKind.Image => node.Image is { } image ? new Size(image.Width, image.Height) : Size.Empty,
            _ => MeasureChildren(node),
        };

        // Explicit sizes win over measured content, but padding still applies:
        // a fixed width describes the border box, as in CSS's border-box model.
        int width = node.Box.Width ?? (content.Width + node.Box.Padding.Horizontal);
        int height = node.Box.Height ?? (content.Height + node.Box.Padding.Vertical);

        width = Math.Max(width, node.Box.MinWidth);
        if (node.Box.MaxWidth is { } max) width = Math.Min(width, max);

        node.ContentSize = new Size(width, height);
        return node.ContentSize;
    }

    private Size MeasureChildren(VisualNode node)
    {
        int main = 0;
        int cross = 0;
        int visible = 0;

        foreach (VisualNode child in node.Children)
        {
            if (!child.Visible) continue;

            Size size = Measure(child);
            visible++;

            int childMain = node.Direction == FlexDirection.Row
                ? size.Width + child.Box.Margin.Horizontal
                : size.Height + child.Box.Margin.Vertical;

            int childCross = node.Direction == FlexDirection.Row
                ? size.Height + child.Box.Margin.Vertical
                : size.Width + child.Box.Margin.Horizontal;

            main += childMain;
            cross = Math.Max(cross, childCross);
        }

        if (visible > 1) main += node.Gap * (visible - 1);

        return node.Direction == FlexDirection.Row
            ? new Size(main, cross)
            : new Size(cross, main);
    }

    // ---- pass two: place ---------------------------------------------------

    private void Place(VisualNode node, Rect bounds)
    {
        node.Rect = bounds;

        if (!node.Visible || node.Children.Count == 0) return;

        Rect inner = Rect.FromEdges(
            bounds.Left + node.Box.Padding.Left,
            bounds.Top + node.Box.Padding.Top,
            bounds.Right - node.Box.Padding.Right,
            bounds.Bottom - node.Box.Padding.Bottom);

        List<VisualNode> children = [];
        foreach (VisualNode child in node.Children)
            if (child.Visible) children.Add(child);

        if (children.Count == 0) return;

        bool row = node.Direction == FlexDirection.Row;
        int available = row ? inner.Width : inner.Height;
        int gapTotal = node.Gap * (children.Count - 1);

        // Natural sizes along the main axis, including margins.
        Span<int> sizes = children.Count <= 64 ? stackalloc int[children.Count] : new int[children.Count];
        int naturalTotal = 0;

        for (int i = 0; i < children.Count; i++)
        {
            VisualNode child = children[i];

            sizes[i] = row
                ? child.ContentSize.Width + child.Box.Margin.Horizontal
                : child.ContentSize.Height + child.Box.Margin.Vertical;

            naturalTotal += sizes[i];
        }

        int spare = available - gapTotal - naturalTotal;

        if (spare > 0) Grow(children, sizes, spare);
        else if (spare < 0) Shrink(children, sizes, -spare);

        // Justification only has anything to distribute when nothing grew.
        int used = gapTotal;
        for (int i = 0; i < children.Count; i++) used += sizes[i];

        int leftover = Math.Max(0, available - used);

        (int offset, int extraGap) = Justify(node.Justify, leftover, children.Count);

        int position = (row ? inner.Left : inner.Top) + offset;

        for (int i = 0; i < children.Count; i++)
        {
            VisualNode child = children[i];
            Edges margin = child.Box.Margin;

            int mainStart = position + (row ? margin.Left : margin.Top);
            int mainSize = Math.Max(0, sizes[i] - (row ? margin.Horizontal : margin.Vertical));

            int crossAvailable = row
                ? inner.Height - margin.Vertical
                : inner.Width - margin.Horizontal;

            int crossNatural = row ? child.ContentSize.Height : child.ContentSize.Width;

            (int crossOffset, int crossSize) = AlignChild(
                node.Align, crossAvailable, crossNatural, child.Box.Height is not null && row);

            int crossStart = (row ? inner.Top + margin.Top : inner.Left + margin.Left) + crossOffset;

            Rect childBounds = row
                ? new Rect(mainStart, crossStart, mainSize, crossSize)
                : new Rect(crossStart, mainStart, crossSize, mainSize);

            Place(child, childBounds);

            position += sizes[i] + node.Gap + extraGap;
        }
    }

    /// <summary>Distributes spare space among children that want to grow.</summary>
    private static void Grow(List<VisualNode> children, Span<int> sizes, int spare)
    {
        double totalGrow = 0;
        foreach (VisualNode child in children) totalGrow += child.Box.Grow;

        if (totalGrow <= 0) return;

        // Rounds cumulative edges rather than individual sizes, so the children add
        // up to exactly the space available and the last one lands flush with the
        // right edge instead of a pixel short.
        double cumulative = 0;
        int previous = 0;

        for (int i = 0; i < children.Count; i++)
        {
            cumulative += children[i].Box.Grow;

            int edge = i == children.Count - 1
                ? spare
                : (int)Math.Round(cumulative / totalGrow * spare);

            sizes[i] += edge - previous;
            previous = edge;
        }
    }

    /// <summary>
    /// Removes overflow from children that allow it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What stretches when there is room is what gives when there is not: overflow
    /// comes first out of the children that grow, and only when they have nothing
    /// left to give does it fall on the rest. A bar's centre zone grows to hold the
    /// window title; without this rule a long title squeezed the clock beside it,
    /// proportionally, and the guard against that was a character cap on the title
    /// that knew nothing about how much room there actually was.
    /// </para>
    /// <para>
    /// Within each group, proportional to each child's size, as flexbox does, and
    /// pinned so the result fits exactly. Reducing children one at a time against a
    /// shrinking remainder leaves part of the overflow unresolved, and the content
    /// then spills past the bar's right edge.
    /// </para>
    /// <para>
    /// A child that hits its minimum stops giving; the shortfall is redistributed
    /// over whoever still has room. Without that second pass, a single node with a
    /// large minimum would silently reintroduce the overflow.
    /// </para>
    /// </remarks>
    private static void Shrink(List<VisualNode> children, Span<int> sizes, int overflow)
    {
        overflow = ShrinkAmong(children, sizes, overflow, growingOnly: true);

        if (overflow > 0) ShrinkAmong(children, sizes, overflow, growingOnly: false);
    }

    /// <summary>
    /// Takes as much of the overflow as it can from one group of children, and
    /// returns what is left.
    /// </summary>
    private static int ShrinkAmong(List<VisualNode> children, Span<int> sizes, int overflow, bool growingOnly)
    {
        for (int pass = 0; pass < 3 && overflow > 0; pass++)
        {
            int shrinkable = 0;

            for (int i = 0; i < children.Count; i++)
                if (Gives(children[i], sizes[i], growingOnly)) shrinkable += sizes[i];

            if (shrinkable <= 0) return overflow;

            int removed = 0;

            for (int i = 0; i < children.Count; i++)
            {
                VisualNode child = children[i];
                if (!Gives(child, sizes[i], growingOnly)) continue;

                int share = (int)Math.Round((double)sizes[i] / shrinkable * overflow);
                int reduced = Math.Max(child.Box.MinWidth, sizes[i] - share);

                removed += sizes[i] - reduced;
                sizes[i] = reduced;
            }

            if (removed == 0) return overflow;

            overflow -= removed;
        }

        return overflow;
    }

    /// <summary>Whether a child can still give up width in this round.</summary>
    private static bool Gives(VisualNode child, int size, bool growingOnly) =>
        child.Box.CanShrink &&
        size > child.Box.MinWidth &&
        (!growingOnly || child.Box.Grow > 0);

    private static (int Offset, int ExtraGap) Justify(JustifyContent justify, int leftover, int count) =>
        justify switch
        {
            JustifyContent.Center => (leftover / 2, 0),
            JustifyContent.End => (leftover, 0),
            JustifyContent.SpaceBetween when count > 1 => (0, leftover / (count - 1)),
            JustifyContent.SpaceAround when count > 0 => (leftover / (count * 2), leftover / count),
            _ => (0, 0),
        };

    /// <summary>
    /// Where a child sits across the row, and how tall it is there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The offset may be negative. A child taller than the row is placed where its
    /// alignment says and the row's edges clip it: centred means the overflow is
    /// split, end means the far edge stays on the far edge. The offset used to be
    /// clamped at zero, which turned every overflowing child into a top-aligned one -
    /// and the bar's icon is a picture in four pixels of padding, so <c>icon size=20</c>
    /// in a bar of <c>height 23</c> overflowed by five, all of it at the bottom: the
    /// picture sat six pixels down and a pixel over the edge, off-centre and cropped
    /// at once. Split, the overflow is the icon's own transparent padding and the
    /// picture is whole.
    /// </para>
    /// <para>
    /// Hit testing follows the rectangle wherever it is, so the visible part of an
    /// overflowing child is still the part that is clicked.
    /// </para>
    /// </remarks>
    private static (int Offset, int Size) AlignChild(
        AlignItems align, int available, int natural, bool hasExplicitCrossSize)
    {
        // An explicit cross size is honoured whatever the alignment says; otherwise
        // "stretch" would silently override it.
        if (hasExplicitCrossSize)
            return ((available - natural) / 2, natural);

        return align switch
        {
            AlignItems.Stretch => (0, available),
            AlignItems.Center => ((available - natural) / 2, natural),
            AlignItems.End => (available - natural, natural),
            _ => (0, natural),
        };
    }
}
