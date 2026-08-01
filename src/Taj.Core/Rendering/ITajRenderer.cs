using Shubbak.Core.Geometry;
using Taj.Core.Layout;

namespace Taj.Core.Rendering;

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
public interface ITajRenderer : ITextMeasurer, IDisposable
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
    public static void Paint(ITajRenderer renderer, VisualNode root, Rect bounds, Colour background)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(root);

        renderer.BeginFrame(bounds, background);

        try
        {
            PaintNode(renderer, root);
        }
        finally
        {
            // The frame is always presented, even after a failure. A renderer left
            // mid-frame would leave the bar showing the previous frame forever.
            renderer.EndFrame();
        }
    }

    private static void PaintNode(ITajRenderer renderer, VisualNode node)
    {
        if (!node.Visible || node.Rect.IsEmpty) return;

        VisualStyle style = node.Style;

        if (!style.Background.IsTransparent)
            renderer.FillRectangle(node.Rect, style.Background, style.CornerRadius);

        if (style.BorderWidth > 0 && !style.BorderColour.IsTransparent)
            renderer.DrawRectangle(node.Rect, style.BorderColour, style.BorderWidth, style.CornerRadius);

        if (node.Kind == VisualKind.Text && node.Text.Length > 0)
        {
            Rect textRect = Deflate(node.Rect, node.Box.Padding);
            renderer.DrawText(node.Text, textRect, style.Foreground, style.Font);
        }

        // Children after the parent's own background, so nesting draws correctly.
        foreach (VisualNode child in node.Children) PaintNode(renderer, child);
    }

    private static Rect Deflate(Rect rect, Edges padding) => Rect.FromEdges(
        rect.Left + padding.Left,
        rect.Top + padding.Top,
        Math.Max(rect.Left + padding.Left, rect.Right - padding.Right),
        Math.Max(rect.Top + padding.Top, rect.Bottom - padding.Bottom));
}
