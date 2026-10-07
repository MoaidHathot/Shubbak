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

        // The same bargain for lines and arcs. A filled rectangle needs no capability -
        // every renderer fills them - so a node made of those is drawn by any renderer.
        if (node.Kind == VisualKind.Shape && node.Shapes.Count > 0)
        {
            Rect content = Deflate(node.Rect, node.Box.Padding);

            if (!content.IsEmpty)
            {
                IShapeRenderer? shapes = renderer as IShapeRenderer;

                // Indexed: enumerating the list through its interface allocates the
                // enumerator, once per shape node per paint.
                IReadOnlyList<Shape> list = node.Shapes;
                for (int i = 0; i < list.Count; i++) PaintShape(renderer, shapes, list[i], content);
            }
        }

        // Children after the parent's own background, so nesting draws correctly.
        foreach (VisualNode child in node.Children) PaintNode(renderer, child, hovered);
    }

    /// <summary>
    /// Resolves a shape's unit square against the node's content rectangle and draws
    /// it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A stroke is kept inside the rectangle: the square it is mapped onto is inset by
    /// half the stroke's width, so a line along the top edge of a graph is a whole
    /// line and not the lower half of one. A fill under a line runs to the rectangle's
    /// real bottom edge, not the inset one, since the area under a graph ends at the
    /// bottom of the box.
    /// </para>
    /// <para>
    /// An arc is inscribed in the rectangle's largest centred square, its radius
    /// brought in by half the stroke so the outer edge of the ring is the edge of the
    /// box - which is where a gauge sized <c>size=18</c> expects to end.
    /// </para>
    /// <para>
    /// A rectangle's four edges are each rounded to the nearest pixel on their own, so
    /// two that meet in the unit square meet on screen; see <see cref="RectShape"/>.
    /// </para>
    /// <para>
    /// The resolved points live on the stack: this is called once per shape per
    /// paint, and a sparkline's sixty points and the sixty-two of the area under them
    /// are two kilobytes that were otherwise two arrays of garbage per frame. Its own
    /// method rather than a block in the caller's loop, so each call's stack is given
    /// back before the next shape's is taken.
    /// </para>
    /// </remarks>
    /// <param name="renderer">The renderer, for what every renderer draws.</param>
    /// <param name="shapes">The same renderer as a shape renderer, or null when it is not one.</param>
    /// <param name="shape">What to draw.</param>
    /// <param name="content">The node's content rectangle, in pixels.</param>
    private static void PaintShape(IRenderer renderer, IShapeRenderer? shapes, Shape shape, Rect content)
    {
        // Past this many points the stack is left alone; a graph that wide is not a
        // sparkline anyway.
        const int StackPoints = 256;

        switch (shape)
        {
            case RectShape box when !box.Fill.IsTransparent:
            {
                int left = Pixel(content.Left + (Math.Clamp(box.Rect.X, 0, 1) * content.Width));
                int top = Pixel(content.Top + (Math.Clamp(box.Rect.Y, 0, 1) * content.Height));
                int right = Pixel(content.Left + (Math.Clamp(box.Rect.Right, 0, 1) * content.Width));
                int bottom = Pixel(content.Top + (Math.Clamp(box.Rect.Bottom, 0, 1) * content.Height));

                Rect rect = Rect.FromEdges(left, top, right, bottom);
                if (!rect.IsEmpty) renderer.FillRectangle(rect, box.Fill, box.CornerRadius);

                break;
            }

            case PolylineShape line when shapes is not null && line.Points.Count > 0:
            {
                double inset = Math.Min(line.Thickness / 2.0, Math.Min(content.Width, content.Height) / 2.0);

                double left = content.Left + inset;
                double top = content.Top + inset;
                double width = Math.Max(0, content.Width - (2 * inset));
                double height = Math.Max(0, content.Height - (2 * inset));

                int count = line.Points.Count;
                bool filled = !line.Fill.IsTransparent && count >= 2;

                // One buffer for both: the line's points, then the two that close the
                // area under it, so the fill is a slice of the same span.
                int needed = filled ? count + 2 : count;
                Span<PointD> points = needed <= StackPoints ? stackalloc PointD[needed] : new PointD[needed];

                for (int i = 0; i < count; i++)
                {
                    UnitPoint unit = line.Points[i];
                    points[i] = new PointD(left + (Math.Clamp(unit.X, 0, 1) * width), top + (Math.Clamp(unit.Y, 0, 1) * height));
                }

                if (filled)
                {
                    // The line, then straight down to the bottom edge at each end and
                    // back along it: the area under the graph.
                    points[count] = new PointD(points[count - 1].X, content.Bottom);
                    points[count + 1] = new PointD(points[0].X, content.Bottom);

                    shapes.FillPolygon(points, line.Fill);
                }

                if (!line.Stroke.IsTransparent && line.Thickness > 0 && count >= 2)
                    shapes.DrawPolyline(points[..count], line.Stroke, line.Thickness);

                break;
            }

            case ArcShape arc when shapes is not null && !arc.Stroke.IsTransparent && arc.Thickness > 0 && arc.SweepDegrees != 0:
            {
                double side = Math.Min(content.Width, content.Height);
                double radius = (side / 2.0) - (arc.Thickness / 2.0);
                if (radius <= 0) break;

                var centre = new PointD(content.Left + (content.Width / 2.0), content.Top + (content.Height / 2.0));

                shapes.DrawArc(centre, radius, arc.StartDegrees, arc.SweepDegrees, arc.Stroke, arc.Thickness);
                break;
            }

            default:
                break;
        }
    }

    /// <summary>The nearest pixel edge, half away from zero.</summary>
    private static int Pixel(double coordinate) => (int)Math.Round(coordinate, MidpointRounding.AwayFromZero);

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
