using Shubbak.Core.Geometry;
using Shubbak.Core.Rendering;
using Shubbak.Ui.Layout;

namespace Shubbak.Ui.Rendering;

/// <summary>
/// Draws a visual tree.
/// </summary>
/// <remarks>
/// <para>
/// The seam that keeps rendering swappable. Everything above it - sources, widgets,
/// templates, the flex layout - is renderer-agnostic and covered by tests that run
/// without a window on screen. Replacing the drawing technology means implementing
/// this interface and nothing else: no config changes, no widget changes, no
/// changes to the layout engine.
/// </para>
/// <para>
/// It is deliberately small. A bar draws filled rectangles, borders and text; an
/// interface exposing paths, clipping, transforms and layers would tie the model to
/// one API's capabilities and defeat the purpose.
/// </para>
/// </remarks>
public interface IRenderer : ITextMeasurer, IDisposable
{
    /// <summary>Begins a frame, clearing to <paramref name="background"/>.</summary>
    void BeginFrame(Rect bounds, Colour background);

    /// <summary>Fills a rectangle, optionally rounded.</summary>
    void FillRectangle(Rect rect, Colour colour, int cornerRadius = 0);

    /// <summary>Strokes a rectangle outline.</summary>
    void DrawRectangle(Rect rect, Colour colour, int thickness, int cornerRadius = 0);

    /// <summary>
    /// Draws text within <paramref name="rect"/>.
    /// </summary>
    /// <remarks>
    /// Clipped to the rectangle. The layout engine has already decided the space
    /// available, so text that does not fit is the layout's business - and silently
    /// spilling over a neighbour would be worse than clipping.
    /// </remarks>
    void DrawText(string text, Rect rect, Colour colour, FontStyle font);

    /// <summary>Presents the frame.</summary>
    void EndFrame();
}

/// <summary>
/// Walks a laid-out tree and issues draw calls.
/// </summary>
/// <remarks>
/// Shared by every renderer: the traversal, the ordering and the decisions about
/// what to draw belong to the model, not to any drawing API. A new renderer supplies
/// primitives; it does not reimplement this.
/// </remarks>
public static class VisualPainter
{
    /// <summary>Draws a tree that has already been laid out.</summary>
    /// <remarks>
    /// The hovered node is passed in rather than stored on the tree, so hovering does
    /// not require rebuilding it - the tree is rebuilt only when the data behind it
    /// changes, and pointer movement is not data.
    /// </remarks>
    public static void Paint(
        IRenderer renderer,
        VisualNode root,
        Rect bounds,
        Colour background,
        VisualNode? hovered = null)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(root);

        renderer.BeginFrame(bounds, background);

        try
        {
            PaintNode(renderer, root, hovered);
        }
        finally
        {
            // The frame is always presented, even after a failure. A renderer left
            // mid-frame would leave the bar showing the previous frame forever.
            renderer.EndFrame();
        }
    }

    private static void PaintNode(IRenderer renderer, VisualNode node, VisualNode? hovered)
    {
        if (!node.Visible || node.Rect.IsEmpty) return;

        VisualStyle style = ReferenceEquals(node, hovered) && node.HoverStyle is { } hover
            ? hover
            : node.Style;

        if (!style.Background.IsTransparent)
            renderer.FillRectangle(node.Rect, style.Background, style.CornerRadius);

        if (style.BorderWidth > 0 && !style.BorderColour.IsTransparent)
            PaintBorder(renderer, node.Rect, style);

        if (node.Kind == VisualKind.Text && node.Text.Length > 0)
        {
            Rect textRect = Deflate(node.Rect, node.Box.Padding);
            renderer.DrawText(node.Text, textRect, style.Foreground, style.Font);
        }

        // Through the capability when the renderer has it; a renderer that cannot
        // composite draws the node's background and border and leaves the picture out,
        // which is a gap rather than a failure.
        if (node.Kind == VisualKind.Image && node.Image is { } image && renderer is IImageRenderer images)
        {
            Rect imageRect = Deflate(node.Rect, node.Box.Padding);
            if (!imageRect.IsEmpty) images.DrawImage(image, imageRect);
        }

        // Children after the parent's own background, so nesting draws correctly.
        foreach (VisualNode child in node.Children) PaintNode(renderer, child, hovered);
    }

    /// <summary>
    /// An outline, or a straight strip along each edge the style names.
    /// </summary>
    /// <remarks>
    /// The strips are filled rectangles rather than a stroke, so a renderer needs no
    /// notion of partial outlines: a hairline under a docked bar or under an active
    /// item is the same primitive as any other fill. Drawn inside the node's rectangle,
    /// where an outline is, so switching between the two does not move anything.
    /// </remarks>
    private static void PaintBorder(IRenderer renderer, Rect rect, VisualStyle style)
    {
        BorderSides sides = style.BorderSides;

        if (sides == BorderSides.All)
        {
            renderer.DrawRectangle(rect, style.BorderColour, style.BorderWidth, style.CornerRadius);
            return;
        }

        int width = Math.Min(style.BorderWidth, Math.Min(rect.Width, rect.Height));
        if (width <= 0) return;

        if (sides.HasFlag(BorderSides.Top))
            renderer.FillRectangle(new Rect(rect.Left, rect.Top, rect.Width, width), style.BorderColour);

        if (sides.HasFlag(BorderSides.Bottom))
            renderer.FillRectangle(new Rect(rect.Left, rect.Bottom - width, rect.Width, width), style.BorderColour);

        if (sides.HasFlag(BorderSides.Left))
            renderer.FillRectangle(new Rect(rect.Left, rect.Top, width, rect.Height), style.BorderColour);

        if (sides.HasFlag(BorderSides.Right))
            renderer.FillRectangle(new Rect(rect.Right - width, rect.Top, width, rect.Height), style.BorderColour);
    }

    private static Rect Deflate(Rect rect, Edges padding) => Rect.FromEdges(
        rect.Left + padding.Left,
        rect.Top + padding.Top,
        Math.Max(rect.Left + padding.Left, rect.Right - padding.Right),
        Math.Max(rect.Top + padding.Top, rect.Bottom - padding.Bottom));
}
