using Shubbak.Core.Rendering;

namespace Shubbak.Ui.Layout;

/// <summary>
/// A point in the unit square of a node's content rectangle: <c>(0, 0)</c> is the top
/// left corner and <c>(1, 1)</c> the bottom right.
/// </summary>
/// <remarks>
/// Unit coordinates rather than pixels, so a shape is built once by a widget that
/// knows nothing about where the layout will put it or how large a pixel is on the
/// display it lands on. The painter maps the square onto the node's rectangle after
/// layout, which is the same moment it reads the rectangle for text.
/// </remarks>
public readonly record struct UnitPoint(double X, double Y);

/// <summary>
/// A rectangle in the unit square of a node's content rectangle; see <see cref="UnitPoint"/>.
/// </summary>
public readonly record struct UnitRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;
}

/// <summary>
/// Something a <see cref="VisualKind.Shape"/> node draws, described in the unit
/// square of its content rectangle.
/// </summary>
/// <remarks>
/// <para>
/// The bar's visual vocabulary was rectangles, text and pictures, and everything a
/// bar shows is one of those - until a sparkline, which is a line through points, and
/// a ring gauge, which is an arc. Two primitives cover both, and a node carries a list
/// of them so a gauge's dim track and its bright value are one node rather than two
/// the layout would have to stack.
/// </para>
/// <para>
/// Drawn through <see cref="Rendering.IShapeRenderer"/>, which a renderer offers or
/// does not; one that does not draws the node's background and border and leaves the
/// shape out, exactly as a renderer without pictures treats an image. The one
/// exception is <see cref="RectShape"/>, which every renderer can fill and so is
/// never left out.
/// </para>
/// </remarks>
public abstract record Shape
{
    /// <summary>The same shape with its pixel sizes multiplied, for a display whose pixels are smaller.</summary>
    public abstract Shape Scaled(double factor);
}

/// <summary>
/// A filled rectangle in the unit square, optionally rounded.
/// </summary>
/// <param name="Rect">Where, as fractions of the node's content rectangle.</param>
/// <param name="Fill">The colour.</param>
/// <param name="CornerRadius">Corner rounding in pixels, before scaling.</param>
/// <remarks>
/// <para>
/// The primitive a layout thumbnail is made of: a workspace drawn small, with one of
/// these per pane. A filled rectangle is the one thing every renderer already draws -
/// it is what a pill is - so unlike a line or an arc this needs no capability, and a
/// node made only of these is drawn whole by a renderer that has never heard of
/// shapes.
/// </para>
/// <para>
/// Edges are rounded to pixels independently, so two rectangles that share an edge
/// in the unit square share a pixel edge on screen - which is what lets panes tile a
/// box exactly, with the gaps between them the only gaps.
/// </para>
/// </remarks>
public sealed record RectShape(UnitRect Rect, Colour Fill, int CornerRadius = 0) : Shape
{
    public override Shape Scaled(double factor) =>
        CornerRadius == 0 ? this : this with { CornerRadius = VisualScaling.Scale(CornerRadius, factor) };
}

/// <summary>
/// A line through points, optionally with the area between it and the bottom edge of
/// the node filled.
/// </summary>
/// <param name="Points">The vertices, left to right for a graph, in unit coordinates.</param>
/// <param name="Stroke">The line's colour; transparent for a fill with no line over it.</param>
/// <param name="Thickness">The line's width in pixels, before scaling.</param>
/// <param name="Fill">
/// The colour of the area under the line, down to the bottom of the node; transparent
/// for none. What makes a sparkline read as a mass rather than a thread.
/// </param>
public sealed record PolylineShape(
    IReadOnlyList<UnitPoint> Points,
    Colour Stroke,
    int Thickness = 1,
    Colour Fill = default) : Shape
{
    public override Shape Scaled(double factor) =>
        this with { Thickness = VisualScaling.ScaleAtLeastOne(Thickness, factor) };
}

/// <summary>
/// A circular arc inscribed in the node's content rectangle, measured in degrees
/// clockwise from twelve o'clock.
/// </summary>
/// <param name="StartDegrees">Where the arc begins: 0 is the top, 90 the right.</param>
/// <param name="SweepDegrees">How far it runs clockwise; 360 is a full ring.</param>
/// <param name="Stroke">The arc's colour.</param>
/// <param name="Thickness">The stroke's width in pixels, before scaling.</param>
/// <remarks>
/// Clockwise from the top because that is how a gauge is read - a clock, a dial, a
/// progress ring all start at the top and run to the right - whereas mathematics'
/// counter-clockwise from three o'clock is how nobody describes one.
/// </remarks>
public sealed record ArcShape(
    double StartDegrees,
    double SweepDegrees,
    Colour Stroke,
    int Thickness = 2) : Shape
{
    public override Shape Scaled(double factor) =>
        this with { Thickness = VisualScaling.ScaleAtLeastOne(Thickness, factor) };
}
