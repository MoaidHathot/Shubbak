using Shubbak.Core.Rendering;

namespace Shubbak.Ui.Rendering;

/// <summary>A position in pixels, with the fraction kept.</summary>
/// <remarks>
/// The rest of the model is integral because window positions are, but a line through
/// the middle of a six-pixel-tall graph has to be able to pass through the middle of a
/// pixel, and an anti-aliased stroke is drawn from exactly where it lies.
/// </remarks>
public readonly record struct PointD(double X, double Y);

/// <summary>
/// Draws lines and arcs.
/// </summary>
/// <remarks>
/// <para>
/// A capability beside <see cref="IImageRenderer"/>, for the same reason: everything
/// added to <see cref="IRenderer"/> is something every renderer has to be able to do,
/// and a renderer that cannot stroke a path should be able to say so by not offering
/// this rather than by throwing. The painter draws a <see cref="Layout.VisualKind.Shape"/>
/// node through this when the renderer has it, and draws the node's background and
/// border alone when it does not.
/// </para>
/// <para>
/// Three primitives, in pixels, anti-aliased: a line through points, a filled polygon,
/// and a circular arc. A sparkline is the first over the second; a gauge is two of the
/// third. Nothing here knows about the unit square a shape is described in - the
/// painter has resolved that against the node's rectangle before it calls.
/// </para>
/// </remarks>
public interface IShapeRenderer
{
    /// <summary>
    /// Strokes a line through the points in order, with round joins and ends, so a
    /// graph with sharp turns does not grow spikes at its corners.
    /// </summary>
    /// <param name="points">At least two; fewer draw nothing.</param>
    /// <param name="colour">The stroke's colour, alpha and all.</param>
    /// <param name="thickness">The stroke's width in pixels.</param>
    void DrawPolyline(ReadOnlySpan<PointD> points, Colour colour, double thickness);

    /// <summary>Fills a polygon, closed from its last point back to its first.</summary>
    /// <param name="points">At least three; fewer draw nothing.</param>
    /// <param name="colour">The fill's colour, alpha and all.</param>
    void FillPolygon(ReadOnlySpan<PointD> points, Colour colour);

    /// <summary>
    /// Strokes part of a circle, clockwise from twelve o'clock.
    /// </summary>
    /// <param name="centre">The circle's centre.</param>
    /// <param name="radius">The circle's radius, to the middle of the stroke.</param>
    /// <param name="startDegrees">Where the arc begins; 0 is the top, 90 the right.</param>
    /// <param name="sweepDegrees">How far it runs clockwise; 360 or more is the whole circle.</param>
    /// <param name="colour">The stroke's colour, alpha and all.</param>
    /// <param name="thickness">The stroke's width in pixels.</param>
    void DrawArc(PointD centre, double radius, double startDegrees, double sweepDegrees, Colour colour, double thickness);
}
