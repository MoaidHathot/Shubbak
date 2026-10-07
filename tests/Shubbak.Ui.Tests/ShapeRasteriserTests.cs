using Shubbak.Core.Geometry;
using Shubbak.Ui.Rendering;

namespace Shubbak.Ui.Tests;

/// <summary>
/// The arithmetic behind anti-aliased lines, fills and arcs: which pixels a shape
/// covers, and by how much.
/// </summary>
/// <remarks>
/// A software renderer is only as good as these numbers, and they are the kind that
/// are wrong by half a pixel without anyone being able to say why the sparkline looks
/// soft. Each test draws into a small grid and reads cells back.
/// </remarks>
public sealed class ShapeRasteriserTests
{
    private static readonly Rect Grid = new(0, 0, 10, 10);

    private static double[] Fresh() => new double[Grid.Width * Grid.Height];

    private static double At(double[] coverage, int x, int y) => coverage[(y * Grid.Width) + x];

    // ---- lines -------------------------------------------------------------

    [Fact]
    public void AOnePixelLineAlongPixelCentresCoversItsRowAndNothingElse()
    {
        double[] coverage = Fresh();

        ShapeRasteriser.Polyline([new PointD(1, 4.5), new PointD(9, 4.5)], 1, Grid, coverage);

        for (int x = 1; x < 9; x++) Assert.Equal(1.0, At(coverage, x, 4), precision: 6);

        Assert.Equal(0.0, At(coverage, 5, 3), precision: 6);
        Assert.Equal(0.0, At(coverage, 5, 5), precision: 6);
    }

    [Fact]
    public void AOnePixelLineOnAPixelBoundaryIsSharedByTwoRows()
    {
        // Half in each: the line at y=4 lies on the edge between rows 3 and 4.
        double[] coverage = Fresh();

        ShapeRasteriser.Polyline([new PointD(1, 4), new PointD(9, 4)], 1, Grid, coverage);

        Assert.Equal(0.5, At(coverage, 5, 3), precision: 6);
        Assert.Equal(0.5, At(coverage, 5, 4), precision: 6);
    }

    [Fact]
    public void ATwoPixelLineOnAPixelBoundaryCoversBothRowsWhole()
    {
        double[] coverage = Fresh();

        ShapeRasteriser.Polyline([new PointD(1, 4), new PointD(9, 4)], 2, Grid, coverage);

        Assert.Equal(1.0, At(coverage, 5, 3), precision: 6);
        Assert.Equal(1.0, At(coverage, 5, 4), precision: 6);
        Assert.Equal(0.0, At(coverage, 5, 2), precision: 6);
        Assert.Equal(0.0, At(coverage, 5, 5), precision: 6);
    }

    [Fact]
    public void TheEndsAreRoundAndStopAtTheEndpoint()
    {
        double[] coverage = Fresh();

        ShapeRasteriser.Polyline([new PointD(2.5, 4.5), new PointD(7.5, 4.5)], 1, Grid, coverage);

        // The pixel holding the endpoint is covered; the one past it is not.
        Assert.Equal(1.0, At(coverage, 7, 4), precision: 6);
        Assert.Equal(0.0, At(coverage, 8, 4), precision: 6);
        Assert.Equal(0.0, At(coverage, 1, 4), precision: 6);
    }

    [Fact]
    public void AJointIsNoDarkerThanTheLine()
    {
        // Two segments meet at (5.5, 4.5); the larger coverage wins rather than the
        // two adding up to more than a pixel.
        double[] coverage = Fresh();

        ShapeRasteriser.Polyline([new PointD(1.5, 4.5), new PointD(5.5, 4.5), new PointD(5.5, 8.5)], 1, Grid, coverage);

        Assert.Equal(1.0, At(coverage, 5, 4), precision: 6);
        Assert.Equal(1.0, At(coverage, 5, 7), precision: 6);
        Assert.Equal(1.0, At(coverage, 2, 4), precision: 6);
    }

    [Fact]
    public void ADiagonalIsSoftAtItsEdges()
    {
        double[] coverage = Fresh();

        ShapeRasteriser.Polyline([new PointD(0.5, 0.5), new PointD(9.5, 9.5)], 1, Grid, coverage);

        // On the line: whole. A pixel beside it: partly, since its centre is one
        // over root two away.
        Assert.Equal(1.0, At(coverage, 4, 4), precision: 6);
        Assert.InRange(At(coverage, 5, 4), 0.2, 0.4);
        Assert.Equal(0.0, At(coverage, 7, 2), precision: 6);
    }

    [Fact]
    public void OnePointOrNoThicknessDrawsNothing()
    {
        double[] coverage = Fresh();

        ShapeRasteriser.Polyline([new PointD(5, 5)], 1, Grid, coverage);
        ShapeRasteriser.Polyline([new PointD(1, 5), new PointD(9, 5)], 0, Grid, coverage);

        Assert.All(coverage, c => Assert.Equal(0.0, c));
    }

    [Fact]
    public void ALineIsClippedToTheGrid()
    {
        // Points beyond the surface neither throw nor write outside the span.
        double[] coverage = Fresh();

        ShapeRasteriser.Polyline([new PointD(-20, 4.5), new PointD(30, 4.5)], 1, Grid, coverage);

        Assert.Equal(1.0, At(coverage, 0, 4), precision: 6);
        Assert.Equal(1.0, At(coverage, 9, 4), precision: 6);
    }

    // ---- fills -------------------------------------------------------------

    [Fact]
    public void ARectangleOnPixelEdgesFillsItsPixelsWholeAndNoOthers()
    {
        double[] coverage = Fresh();

        ShapeRasteriser.Polygon(
            [new PointD(2, 2), new PointD(6, 2), new PointD(6, 5), new PointD(2, 5)], Grid, coverage);

        for (int y = 2; y < 5; y++)
            for (int x = 2; x < 6; x++)
                Assert.Equal(1.0, At(coverage, x, y), precision: 6);

        Assert.Equal(0.0, At(coverage, 1, 3), precision: 6);
        Assert.Equal(0.0, At(coverage, 6, 3), precision: 6);
        Assert.Equal(0.0, At(coverage, 3, 1), precision: 6);
        Assert.Equal(0.0, At(coverage, 3, 5), precision: 6);
    }

    [Fact]
    public void AnEdgeThroughTheMiddleOfAPixelCoversHalfOfIt()
    {
        double[] coverage = Fresh();

        // Left edge at x=2.5, top edge at y=2.5.
        ShapeRasteriser.Polygon(
            [new PointD(2.5, 2.5), new PointD(6, 2.5), new PointD(6, 6), new PointD(2.5, 6)], Grid, coverage);

        Assert.Equal(0.5, At(coverage, 2, 4), precision: 6);     // left column: half its width
        Assert.Equal(0.5, At(coverage, 4, 2), precision: 6);     // top row: two of four sub-rows
        Assert.Equal(0.25, At(coverage, 2, 2), precision: 6);    // the corner: half of half
        Assert.Equal(1.0, At(coverage, 4, 4), precision: 6);
    }

    [Fact]
    public void AnAreaUnderASlopeIsFilledBelowTheLineAndNotAbove()
    {
        // The sparkline's polygon: a line from the top left down to the bottom right,
        // closed along the bottom edge.
        double[] coverage = Fresh();

        ShapeRasteriser.Polygon(
            [new PointD(0, 0), new PointD(10, 10), new PointD(0, 10)], Grid, coverage);

        Assert.Equal(1.0, At(coverage, 1, 8), precision: 6);
        Assert.Equal(0.0, At(coverage, 8, 1), precision: 6);
        Assert.InRange(At(coverage, 4, 4), 0.3, 0.7);
    }

    [Fact]
    public void FewerThanThreePointsFillNothing()
    {
        double[] coverage = Fresh();

        ShapeRasteriser.Polygon([new PointD(0, 0), new PointD(10, 10)], Grid, coverage);

        Assert.All(coverage, c => Assert.Equal(0.0, c));
    }

    // ---- arcs --------------------------------------------------------------

    [Fact]
    public void ARingCoversTheCircleAndNotTheMiddle()
    {
        double[] coverage = Fresh();
        var centre = new PointD(5.5, 5.5);

        ShapeRasteriser.Arc(centre, 3, 0, 360, 1, Grid, coverage);

        // Top, right, bottom and left of a circle of radius 3 about (5.5, 5.5) land on pixel centres.
        Assert.Equal(1.0, At(coverage, 5, 2), precision: 6);
        Assert.Equal(1.0, At(coverage, 8, 5), precision: 6);
        Assert.Equal(1.0, At(coverage, 5, 8), precision: 6);
        Assert.Equal(1.0, At(coverage, 2, 5), precision: 6);

        Assert.Equal(0.0, At(coverage, 5, 5), precision: 6);
        Assert.Equal(0.0, At(coverage, 4, 5), precision: 6);
    }

    [Fact]
    public void AQuarterArcFromTheTopRunsClockwiseToTheRight()
    {
        double[] coverage = Fresh();
        var centre = new PointD(5.5, 5.5);

        ShapeRasteriser.Arc(centre, 3, 0, 90, 1, Grid, coverage);

        Assert.Equal(1.0, At(coverage, 5, 2), precision: 6);    // twelve o'clock
        Assert.Equal(1.0, At(coverage, 8, 5), precision: 6);    // three o'clock
        Assert.Equal(0.0, At(coverage, 5, 8), precision: 6);    // six: not swept
        Assert.Equal(0.0, At(coverage, 2, 5), precision: 6);    // nine: not swept
    }

    [Fact]
    public void ANegativeSweepIsTheSameArcFromTheOtherEnd()
    {
        double[] forward = Fresh();
        double[] backward = Fresh();
        var centre = new PointD(5.5, 5.5);

        ShapeRasteriser.Arc(centre, 3, 0, 90, 1, Grid, forward);
        ShapeRasteriser.Arc(centre, 3, 90, -90, 1, Grid, backward);

        Assert.Equal(forward, backward);
    }

    [Fact]
    public void TheEndsOfAnArcAreRoundedAndDoNotLeak()
    {
        double[] coverage = Fresh();
        var centre = new PointD(5.5, 5.5);

        // From the top a quarter turn: the stroke ends at three o'clock, and a pixel
        // on the circle just past it - at four o'clock - is beyond the cap.
        ShapeRasteriser.Arc(centre, 3, 0, 90, 1, Grid, coverage);

        PointD past = ShapeRasteriser.OnCircle(centre, 3, 135);
        Assert.Equal(0.0, At(coverage, (int)past.X, (int)past.Y), precision: 6);
    }

    [Fact]
    public void NoSweepOrNoThicknessDrawsNothing()
    {
        double[] coverage = Fresh();

        ShapeRasteriser.Arc(new PointD(5, 5), 3, 0, 0, 1, Grid, coverage);
        ShapeRasteriser.Arc(new PointD(5, 5), 3, 0, 360, 0, Grid, coverage);

        Assert.All(coverage, c => Assert.Equal(0.0, c));
    }

    [Fact]
    public void PointsOnTheCircleAreMeasuredClockwiseFromTheTop()
    {
        var centre = new PointD(5, 5);

        PointD top = ShapeRasteriser.OnCircle(centre, 2, 0);
        PointD right = ShapeRasteriser.OnCircle(centre, 2, 90);
        PointD bottom = ShapeRasteriser.OnCircle(centre, 2, 180);

        Assert.Equal(5, top.X, precision: 9);
        Assert.Equal(3, top.Y, precision: 9);
        Assert.Equal(7, right.X, precision: 9);
        Assert.Equal(5, right.Y, precision: 9);
        Assert.Equal(7, bottom.Y, precision: 9);
    }

    // ---- bounds ------------------------------------------------------------

    [Fact]
    public void BoundsGrowByTheMarginAndStopAtTheSurface()
    {
        Rect bounds = ShapeRasteriser.Bounds([new PointD(2.5, 3.5), new PointD(7.5, 3.5)], 1.5, Grid);
        Assert.Equal(Rect.FromEdges(1, 2, 9, 5), bounds);

        Rect clipped = ShapeRasteriser.Bounds([new PointD(-5, -5), new PointD(50, 50)], 1, Grid);
        Assert.Equal(Grid, clipped);

        Assert.Equal(Rect.Empty, ShapeRasteriser.Bounds([], 1, Grid));
    }

    [Fact]
    public void ACoverageSpanSmallerThanTheClipIsRefused()
    {
        Assert.Throws<ArgumentException>(() =>
            ShapeRasteriser.Polyline([new PointD(0, 0), new PointD(1, 1)], 1, Grid, new double[4]));
    }

    // ---- cost ----------------------------------------------------------------

    [Fact]
    public void RasterisingAllocatesNothing()
    {
        // These run once per shape per paint, and a sparkline repaints every second
        // for as long as its source ticks. The buffers are the caller's; nothing here
        // may take any of its own. Warmed first, as the latency stats test is, so the
        // runtime's own tiering is not mistaken for the rasteriser's.
        var surface = new Rect(0, 0, 200, 40);
        var coverage = new double[80 * 24];

        var line = new PointD[60];
        for (int i = 0; i < 60; i++) line[i] = new PointD(10.5 + i, 10 + (i % 7));

        var area = new PointD[62];
        line.CopyTo(area, 0);
        area[60] = new PointD(line[59].X, 30);
        area[61] = new PointD(line[0].X, 30);

        Rect lineClip = ShapeRasteriser.Bounds(line, 1.5, surface);
        Rect areaClip = ShapeRasteriser.Bounds(area, 1, surface);
        var centre = new PointD(100, 20);
        Rect arcClip = ShapeRasteriser.Bounds(centre, 10, surface);

        void Draw()
        {
            ShapeRasteriser.Polyline(line, 1, lineClip, coverage);
            ShapeRasteriser.Polygon(area, areaClip, coverage);
            ShapeRasteriser.Arc(centre, 7.5, 0, 270, 3, arcClip, coverage);
            ShapeRasteriser.Arc(centre, 7.5, 0, 360, 3, arcClip, coverage);
        }

        for (int i = 0; i < 200; i++) Draw();

        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 100; i++) Draw();

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
