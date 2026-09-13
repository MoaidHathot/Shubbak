using Shubbak.Core.Geometry;
using Shubbak.Core.Rendering;
using Shubbak.Ui.Layout;
using Shubbak.Ui.Rendering;

namespace Shubbak.Ui.Tests;

/// <summary>
/// A renderer that writes down what it was asked to draw.
/// </summary>
internal sealed class RecordingRenderer : IRenderer
{
    public List<string> Calls { get; } = [];

    public Size Measure(string text, FontStyle font) =>
        new((text ?? string.Empty).Length * FixedTextMeasurer.CharacterWidth, FixedTextMeasurer.LineHeight);

    public void BeginFrame(Rect bounds, Colour background) => Calls.Add($"begin {bounds} {background}");

    public void FillRectangle(Rect rect, Colour colour, int cornerRadius = 0) =>
        Calls.Add($"fill {rect} {colour} r{cornerRadius}");

    public void DrawRectangle(Rect rect, Colour colour, int thickness, int cornerRadius = 0) =>
        Calls.Add($"outline {rect} {colour} t{thickness} r{cornerRadius}");

    public void DrawText(string text, Rect rect, Colour colour, FontStyle font) =>
        Calls.Add($"text \"{text}\" {rect} {colour}");

    public void EndFrame() => Calls.Add("end");

    public void Dispose() { }
}

/// <summary>The same, able to draw pictures.</summary>
internal sealed class RecordingImageRenderer : IRenderer, IImageRenderer
{
    private readonly RecordingRenderer _inner = new();

    public List<string> Calls => _inner.Calls;

    public Size Measure(string text, FontStyle font) => _inner.Measure(text, font);

    public void BeginFrame(Rect bounds, Colour background) => _inner.BeginFrame(bounds, background);

    public void FillRectangle(Rect rect, Colour colour, int cornerRadius = 0) => _inner.FillRectangle(rect, colour, cornerRadius);

    public void DrawRectangle(Rect rect, Colour colour, int thickness, int cornerRadius = 0) => _inner.DrawRectangle(rect, colour, thickness, cornerRadius);

    public void DrawText(string text, Rect rect, Colour colour, FontStyle font) => _inner.DrawText(text, rect, colour, font);

    public void DrawImage(ImageBitmap image, Rect rect) => Calls.Add($"image {image.Width}x{image.Height} {rect}");

    public void EndFrame() => _inner.EndFrame();

    public void Dispose() { }
}

/// <summary>Tests for the shared painter: what it asks a renderer for, and in what order.</summary>
public sealed class VisualPainterTests
{
    private static readonly Colour Line = new(0xFF, 0xFF, 0xFF, 0x1A);

    private static VisualNode Surface(VisualStyle style)
    {
        var node = new VisualNode { Id = "bar", Style = style };
        new FlexLayout(new FixedTextMeasurer()).Arrange(node, new Rect(0, 0, 100, 30));
        return node;
    }

    private static List<string> Paint(VisualNode root)
    {
        var renderer = new RecordingRenderer();
        VisualPainter.Paint(renderer, root, new Rect(0, 0, 100, 30), Colour.Transparent);
        return renderer.Calls;
    }

    [Fact]
    public void AnOutlineIsOneStroke()
    {
        List<string> calls = Paint(Surface(VisualStyle.Default with
        {
            BorderColour = Line,
            BorderWidth = 1,
            CornerRadius = 8,
        }));

        Assert.Contains($"outline (0,0 100x30) {Line} t1 r8", calls);
        Assert.DoesNotContain(calls, c => c.StartsWith("fill", StringComparison.Ordinal));
    }

    [Fact]
    public void ABottomBorderIsAStripAlongTheBottomEdge()
    {
        // The docked bar's hairline: inside the node, where an outline's bottom edge
        // would be, and nowhere else.
        List<string> calls = Paint(Surface(VisualStyle.Default with
        {
            BorderColour = Line,
            BorderWidth = 1,
            BorderSides = BorderSides.Bottom,
        }));

        Assert.Contains($"fill (0,29 100x1) {Line} r0", calls);
        Assert.DoesNotContain(calls, c => c.StartsWith("outline", StringComparison.Ordinal));
        Assert.Single(calls, c => c.StartsWith("fill", StringComparison.Ordinal));
    }

    [Fact]
    public void ATopBorderIsAStripAlongTheTopEdge()
    {
        List<string> calls = Paint(Surface(VisualStyle.Default with
        {
            BorderColour = Line,
            BorderWidth = 2,
            BorderSides = BorderSides.Top,
        }));

        Assert.Contains($"fill (0,0 100x2) {Line} r0", calls);
    }

    [Fact]
    public void SidesCombine()
    {
        List<string> calls = Paint(Surface(VisualStyle.Default with
        {
            BorderColour = Line,
            BorderWidth = 1,
            BorderSides = BorderSides.Left | BorderSides.Right,
        }));

        Assert.Contains($"fill (0,0 1x30) {Line} r0", calls);
        Assert.Contains($"fill (99,0 1x30) {Line} r0", calls);
        Assert.Equal(2, calls.Count(c => c.StartsWith("fill", StringComparison.Ordinal)));
    }

    [Fact]
    public void NoWidthMeansNoBorderWhateverTheSides()
    {
        // A zeroed style names no sides and has no width; the width is what decides.
        List<string> calls = Paint(Surface(VisualStyle.Default with
        {
            BorderColour = Line,
            BorderWidth = 0,
            BorderSides = BorderSides.Bottom,
        }));

        Assert.DoesNotContain(calls, c => c.StartsWith("fill", StringComparison.Ordinal) || c.StartsWith("outline", StringComparison.Ordinal));
    }

    [Fact]
    public void TheBackgroundIsPaintedBeforeTheBorder()
    {
        List<string> calls = Paint(Surface(VisualStyle.Default with
        {
            Background = new Colour(0x18, 0x18, 0x25, 0xB3),
            BorderColour = Line,
            BorderWidth = 1,
            BorderSides = BorderSides.Bottom,
        }));

        int background = calls.FindIndex(c => c.StartsWith("fill (0,0 100x30)", StringComparison.Ordinal));
        int border = calls.FindIndex(c => c.StartsWith("fill (0,29 100x1)", StringComparison.Ordinal));

        Assert.True(background >= 0);
        Assert.True(border > background, "the hairline has to land on top of the surface, not under it");
    }

    [Fact]
    public void MergingKeepsTheSidesWithTheBorderTheyBelongTo()
    {
        VisualStyle underlined = VisualStyle.Default with
        {
            BorderColour = Line,
            BorderWidth = 1,
            BorderSides = BorderSides.Bottom,
        };

        // An overlay with no border of its own leaves the base's border, sides and all.
        Assert.Equal(BorderSides.Bottom, underlined.Merge(VisualStyle.Default with { Background = Colour.Black }).BorderSides);

        // An overlay with a border brings its own sides.
        Assert.Equal(BorderSides.All, underlined.Merge(VisualStyle.Default with { BorderWidth = 1, BorderColour = Line }).BorderSides);
    }

    // ---- images ------------------------------------------------------------

    private static VisualNode ImageNode(ImageBitmap image, BoxStyle box = default) => new()
    {
        Id = "icon",
        Kind = VisualKind.Image,
        Image = image,
        Box = box,
        Style = VisualStyle.Default with { Background = new Colour(0xFF, 0xFF, 0xFF, 0x14), CornerRadius = 6 },
    };

    private static ImageBitmap Picture(int size) => new(size, size, new uint[size * size]);

    [Fact]
    public void AnImageIsMeasuredAtItsOwnSizeUnlessBoxed()
    {
        VisualNode natural = ImageNode(Picture(32));
        new FlexLayout(new FixedTextMeasurer()).Arrange(natural, new Rect(0, 0, 100, 40));
        Assert.Equal(new Size(32, 32), natural.ContentSize);

        VisualNode boxed = ImageNode(Picture(32), new BoxStyle(Width: 28, Height: 28, Padding: Edges.All(4)));
        new FlexLayout(new FixedTextMeasurer()).Arrange(boxed, new Rect(0, 0, 100, 40));
        Assert.Equal(new Size(28, 28), boxed.ContentSize);
    }

    [Fact]
    public void ARendererThatCanDrawPicturesIsAskedTo()
    {
        // Drawn inside the padding, after the background, so a pill can sit behind it.
        VisualNode node = ImageNode(Picture(32), new BoxStyle(Width: 28, Height: 28, Padding: Edges.All(4)));
        new FlexLayout(new FixedTextMeasurer()).Arrange(node, new Rect(0, 0, 28, 28));

        var renderer = new RecordingImageRenderer();
        VisualPainter.Paint(renderer, node, new Rect(0, 0, 28, 28), Colour.Transparent);

        int background = renderer.Calls.FindIndex(c => c.StartsWith("fill (0,0 28x28)", StringComparison.Ordinal));
        int picture = renderer.Calls.FindIndex(c => c == "image 32x32 (4,4 20x20)");

        Assert.True(background >= 0, string.Join("\n", renderer.Calls));
        Assert.True(picture > background, string.Join("\n", renderer.Calls));
    }

    [Fact]
    public void ARendererThatCannotDrawPicturesGetsTheRestAndNoError()
    {
        // The seam degrades: background and border, and a gap where the picture would
        // be, rather than a renderer having to grow a method to say no.
        VisualNode node = ImageNode(Picture(16));
        new FlexLayout(new FixedTextMeasurer()).Arrange(node, new Rect(0, 0, 16, 16));

        var renderer = new RecordingRenderer();
        VisualPainter.Paint(renderer, node, new Rect(0, 0, 16, 16), Colour.Transparent);

        Assert.Contains(renderer.Calls, c => c.StartsWith("fill (0,0 16x16)", StringComparison.Ordinal));
        Assert.DoesNotContain(renderer.Calls, c => c.StartsWith("image", StringComparison.Ordinal));
    }

    [Fact]
    public void ABitmapIsBuiltFromTheBytesThatTravel()
    {
        // Blue, green, red, alpha per pixel on the wire; 0xAARRGGBB in memory.
        ImageBitmap? image = ImageBitmap.FromBgra(2, 1, [1, 2, 3, 4, 255, 255, 255, 255]);

        Assert.NotNull(image);
        Assert.Equal(0x04030201u, image.Pixels[0]);
        Assert.Equal(0xFFFFFFFFu, image.Pixels[1]);

        Assert.Null(ImageBitmap.FromBgra(2, 1, [1, 2, 3]));
        Assert.Null(ImageBitmap.FromBgra(0, 1, []));
    }
}
