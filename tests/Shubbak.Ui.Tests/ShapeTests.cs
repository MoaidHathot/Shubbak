using System.Globalization;
using Shubbak.Core.Geometry;
using Shubbak.Core.Rendering;
using Shubbak.Ui.Layout;
using Shubbak.Ui.Rendering;

namespace Shubbak.Ui.Tests;

/// <summary>A renderer that writes down the lines and arcs it was asked for.</summary>
internal sealed class RecordingShapeRenderer : IRenderer, IShapeRenderer
{
    private readonly RecordingRenderer _inner = new();

    public List<string> Calls => _inner.Calls;

    public Size Measure(string text, FontStyle font) => _inner.Measure(text, font);

    public void BeginFrame(Rect bounds, Colour background) => _inner.BeginFrame(bounds, background);

    public void FillRectangle(Rect rect, Colour colour, int cornerRadius = 0) => _inner.FillRectangle(rect, colour, cornerRadius);

    public void DrawRectangle(Rect rect, Colour colour, int thickness, int cornerRadius = 0) => _inner.DrawRectangle(rect, colour, thickness, cornerRadius);

    public void DrawText(string text, Rect rect, Colour colour, FontStyle font) => _inner.DrawText(text, rect, colour, font);

    public void EndFrame() => _inner.EndFrame();

    public void DrawPolyline(ReadOnlySpan<PointD> points, Colour colour, double thickness) =>
        Calls.Add($"line {Join(points)} {colour} t{thickness.ToString(CultureInfo.InvariantCulture)}");

    public void FillPolygon(ReadOnlySpan<PointD> points, Colour colour) =>
        Calls.Add($"polygon {Join(points)} {colour}");

    public void DrawArc(PointD centre, double radius, double startDegrees, double sweepDegrees, Colour colour, double thickness) =>
        Calls.Add(string.Create(CultureInfo.InvariantCulture,
            $"arc c({centre.X},{centre.Y}) r{radius} from{startDegrees} sweep{sweepDegrees} {colour} t{thickness}"));

    public void Dispose() { }

    private static string Join(ReadOnlySpan<PointD> points)
    {
        List<string> parts = [];
        foreach (PointD p in points) parts.Add(string.Create(CultureInfo.InvariantCulture, $"({p.X},{p.Y})"));
        return string.Join(" ", parts);
    }
}

/// <summary>
/// Lines and arcs in the visual tree: how a shape node is measured, scaled and
/// resolved against its rectangle, and what a renderer is asked for.
/// </summary>
/// <remarks>
/// The shapes are described in the unit square so a widget can build one without
/// knowing where it will land; these pin the mapping from that square onto pixels,
/// which is the one place a sparkline can be subtly wrong - half a stroke clipped
/// along the top, a ring a pixel wider than its box.
/// </remarks>
public sealed class ShapeTests
{
    private static readonly Colour Ink = new(0x8D, 0xBC, 0xFF);
    private static readonly Colour Wash = new(0x8D, 0xBC, 0xFF, 0x40);

    private static VisualNode ShapeNode(Shape shape, int width, int height, Edges padding = default) => new()
    {
        Id = "graph",
        Kind = VisualKind.Shape,
        Shapes = [shape],
        Box = new BoxStyle(Width: width, Height: height, Padding: padding),
    };

    private static List<string> Paint(VisualNode node, int width, int height)
    {
        new FlexLayout(new FixedTextMeasurer()).Arrange(node, new Rect(0, 0, width, height));

        var renderer = new RecordingShapeRenderer();
        VisualPainter.Paint(renderer, node, new Rect(0, 0, width, height), Colour.Transparent);
        return renderer.Calls;
    }

    [Fact]
    public void AShapeHasNoSizeOfItsOwn()
    {
        // Like a spacer: drawn into whatever box it is given, and nothing without one.
        VisualNode boxed = ShapeNode(new PolylineShape([new(0, 0), new(1, 1)], Ink), 60, 14);
        new FlexLayout(new FixedTextMeasurer()).Arrange(boxed, new Rect(0, 0, 100, 30));
        Assert.Equal(new Size(60, 14), boxed.ContentSize);

        var bare = new VisualNode { Kind = VisualKind.Shape, Shapes = [new PolylineShape([new(0, 0), new(1, 1)], Ink)] };
        new FlexLayout(new FixedTextMeasurer()).Arrange(bare, new Rect(0, 0, 100, 30));
        Assert.Equal(Size.Empty, bare.ContentSize);
    }

    [Fact]
    public void TheUnitSquareIsMappedOntoTheBoxInsetByHalfTheStroke()
    {
        // A two-pixel line along the top edge is a whole line at y=1, not the lower
        // half of one at y=0.
        List<string> calls = Paint(
            ShapeNode(new PolylineShape([new(0, 0), new(0.5, 1), new(1, 0)], Ink, Thickness: 2), 42, 12),
            42, 12);

        Assert.Contains($"line (1,1) (21,11) (41,1) {Ink} t2", calls);
    }

    [Fact]
    public void PaddingComesOffBeforeTheShapeIsPlaced()
    {
        List<string> calls = Paint(
            ShapeNode(new PolylineShape([new(0, 0), new(1, 1)], Ink, Thickness: 1), 20, 10, Edges.All(2)),
            20, 10);

        // Content is (2,2 16x6); inset by half a pixel for the stroke.
        Assert.Contains($"line (2.5,2.5) (17.5,7.5) {Ink} t1", calls);
    }

    [Fact]
    public void TheFillRunsUnderTheLineToTheBottomEdgeAndIsDrawnFirst()
    {
        List<string> calls = Paint(
            ShapeNode(new PolylineShape([new(0, 0.5), new(1, 0)], Ink, Thickness: 2, Fill: Wash), 22, 12),
            22, 12);

        int fill = calls.FindIndex(c => c.StartsWith("polygon", StringComparison.Ordinal));
        int line = calls.FindIndex(c => c.StartsWith("line", StringComparison.Ordinal));

        Assert.True(fill >= 0, string.Join("\n", calls));
        Assert.True(line > fill, "the line goes over the wash, not under it");

        // The two line points, then down to the box's real bottom at each end.
        Assert.Equal($"polygon (1,6) (21,1) (21,12) (1,12) {Wash}", calls[fill]);
    }

    [Fact]
    public void ATransparentStrokeIsOnlyAFill()
    {
        List<string> calls = Paint(
            ShapeNode(new PolylineShape([new(0, 0.5), new(1, 0)], Colour.Transparent, Thickness: 1, Fill: Wash), 22, 12),
            22, 12);

        Assert.Contains(calls, c => c.StartsWith("polygon", StringComparison.Ordinal));
        Assert.DoesNotContain(calls, c => c.StartsWith("line", StringComparison.Ordinal));
    }

    [Fact]
    public void OnePointIsNotALine()
    {
        List<string> calls = Paint(ShapeNode(new PolylineShape([new(0.5, 0.5)], Ink, Thickness: 1, Fill: Wash), 20, 10), 20, 10);

        Assert.DoesNotContain(calls, c => c.StartsWith("line", StringComparison.Ordinal) || c.StartsWith("polygon", StringComparison.Ordinal));
    }

    [Fact]
    public void AnArcIsInscribedWithItsOuterEdgeOnTheBox()
    {
        // An 18-pixel box and a 3-pixel stroke: the ring's centreline sits at radius
        // 7.5, so its outside is at 9 - the edge of the box, and no further.
        List<string> calls = Paint(ShapeNode(new ArcShape(0, 270, Ink, Thickness: 3), 18, 18), 18, 18);

        Assert.Contains($"arc c(9,9) r7.5 from0 sweep270 {Ink} t3", calls);
    }

    [Fact]
    public void AnArcInAWideBoxIsCentredInIt()
    {
        List<string> calls = Paint(ShapeNode(new ArcShape(0, 360, Ink, Thickness: 2), 30, 18), 30, 18);

        Assert.Contains($"arc c(15,9) r8 from0 sweep360 {Ink} t2", calls);
    }

    [Fact]
    public void AnArcWithNothingToSweepDrawsNothing()
    {
        List<string> calls = Paint(ShapeNode(new ArcShape(0, 0, Ink, Thickness: 2), 18, 18), 18, 18);

        Assert.DoesNotContain(calls, c => c.StartsWith("arc", StringComparison.Ordinal));
    }

    [Fact]
    public void SeveralShapesOnOneNodeAreDrawnInOrder()
    {
        // A gauge: the dim track first, the bright value over it.
        Colour track = new(0xFF, 0xFF, 0xFF, 0x1A);

        var node = new VisualNode
        {
            Kind = VisualKind.Shape,
            Shapes = [new ArcShape(0, 360, track, 2), new ArcShape(0, 90, Ink, 2)],
            Box = new BoxStyle(Width: 18, Height: 18),
        };

        List<string> calls = Paint(node, 18, 18);
        List<string> arcs = calls.Where(c => c.StartsWith("arc", StringComparison.Ordinal)).ToList();

        Assert.Equal(2, arcs.Count);
        Assert.Contains("sweep360", arcs[0], StringComparison.Ordinal);
        Assert.Contains("sweep90", arcs[1], StringComparison.Ordinal);
    }

    [Fact]
    public void TheBackgroundIsPaintedUnderTheShape()
    {
        var node = new VisualNode
        {
            Kind = VisualKind.Shape,
            Shapes = [new ArcShape(0, 90, Ink, 2)],
            Box = new BoxStyle(Width: 18, Height: 18),
            Style = VisualStyle.Default with { Background = Wash, CornerRadius = 4 },
        };

        List<string> calls = Paint(node, 18, 18);

        int background = calls.FindIndex(c => c.StartsWith("fill (0,0 18x18)", StringComparison.Ordinal));
        int arc = calls.FindIndex(c => c.StartsWith("arc", StringComparison.Ordinal));

        Assert.True(background >= 0);
        Assert.True(arc > background);
    }

    [Fact]
    public void ARendererThatCannotDrawShapesGetsTheRestAndNoError()
    {
        var node = new VisualNode
        {
            Kind = VisualKind.Shape,
            Shapes = [new PolylineShape([new(0, 0), new(1, 1)], Ink)],
            Box = new BoxStyle(Width: 20, Height: 10),
            Style = VisualStyle.Default with { Background = Wash },
        };

        new FlexLayout(new FixedTextMeasurer()).Arrange(node, new Rect(0, 0, 20, 10));

        var renderer = new RecordingRenderer();
        VisualPainter.Paint(renderer, node, new Rect(0, 0, 20, 10), Colour.Transparent);

        Assert.Contains(renderer.Calls, c => c.StartsWith("fill (0,0 20x10)", StringComparison.Ordinal));
        Assert.DoesNotContain(renderer.Calls, c => c.StartsWith("line", StringComparison.Ordinal));
    }

    [Fact]
    public void ScalingThickensTheStrokeAndLeavesThePointsAlone()
    {
        // The points are fractions of the box, and the box is scaled; a stroke is pixels.
        var node = new VisualNode
        {
            Kind = VisualKind.Shape,
            Shapes = [new PolylineShape([new(0, 0), new(1, 1)], Ink, Thickness: 2), new ArcShape(0, 90, Ink, Thickness: 1)],
            Box = new BoxStyle(Width: 20, Height: 10),
        };

        VisualScaling.Scale(node, 1.5);

        var line = Assert.IsType<PolylineShape>(node.Shapes[0]);
        var arc = Assert.IsType<ArcShape>(node.Shapes[1]);

        Assert.Equal(3, line.Thickness);
        Assert.Equal(new UnitPoint(1, 1), line.Points[1]);
        Assert.Equal(2, arc.Thickness);   // 1.5 rounds away from zero, and never to nothing
        Assert.Equal(30, node.Box.Width);
    }

    // ---- cost ----------------------------------------------------------------

    /// <summary>A shape renderer that only counts, so what the painter allocates is what is measured.</summary>
    private sealed class CountingShapeRenderer : IRenderer, IShapeRenderer
    {
        public int Lines, Polygons, Arcs;

        public Size Measure(string text, FontStyle font) => new(text.Length * 10, 16);
        public void BeginFrame(Rect bounds, Colour background) { }
        public void FillRectangle(Rect rect, Colour colour, int cornerRadius = 0) { }
        public void DrawRectangle(Rect rect, Colour colour, int thickness, int cornerRadius = 0) { }
        public void DrawText(string text, Rect rect, Colour colour, FontStyle font) { }
        public void EndFrame() { }
        public void DrawPolyline(ReadOnlySpan<PointD> points, Colour colour, double thickness) => Lines++;
        public void FillPolygon(ReadOnlySpan<PointD> points, Colour colour) => Polygons++;
        public void DrawArc(PointD centre, double radius, double startDegrees, double sweepDegrees, Colour colour, double thickness) => Arcs++;
        public void Dispose() { }
    }

    [Fact]
    public void PaintingShapesAllocatesNothing()
    {
        // A paint is not a rebuild: the tree stands, and the painter resolves each
        // shape's unit square against its rectangle on the way to the renderer. A
        // sparkline's sixty points and the sixty-two of the area under them live on
        // the stack for that - two arrays of garbage per frame otherwise, and a frame
        // every second for as long as the source ticks.
        var points = new UnitPoint[60];
        for (int i = 0; i < 60; i++) points[i] = new UnitPoint(i / 59.0, (i % 7) / 7.0);

        var root = new VisualNode { Id = "bar", Direction = FlexDirection.Row };
        root.Add(new VisualNode
        {
            Kind = VisualKind.Shape,
            Shapes = [new PolylineShape(points, Ink, Thickness: 1, Fill: Wash)],
            Box = new BoxStyle(Width: 60, Height: 14),
        });
        root.Add(new VisualNode
        {
            Kind = VisualKind.Shape,
            Shapes = [new ArcShape(0, 360, Wash, 3), new ArcShape(0, 270, Ink, 3)],
            Box = new BoxStyle(Width: 18, Height: 18),
        });

        var bounds = new Rect(0, 0, 100, 18);
        new FlexLayout(new FixedTextMeasurer()).Arrange(root, bounds);

        var renderer = new CountingShapeRenderer();

        for (int i = 0; i < 200; i++) VisualPainter.Paint(renderer, root, bounds, Colour.Transparent);

        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 100; i++) VisualPainter.Paint(renderer, root, bounds, Colour.Transparent);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(300, renderer.Lines);
        Assert.Equal(300, renderer.Polygons);
        Assert.Equal(600, renderer.Arcs);
    }
}
