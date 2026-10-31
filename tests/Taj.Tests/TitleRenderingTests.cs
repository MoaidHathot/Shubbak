using System.Reflection;
using Shubbak.Core.Geometry;
using Shubbak.Core.Rendering;
using Shubbak.Ui.Gdi;
using Shubbak.Ui.Layout;
using Shubbak.Ui.Rendering;

namespace Taj.Tests;

public sealed class TitleRenderingTests
{
    private const string PlainTitle = "OutKast • HBO Max — Mozilla Firefox";
    private const string FirefoxTitle = "\u2068OutKast\u2069 • HBO Max — Mozilla Firefox";

    [Theory]
    [InlineData(false, "Segoe UI")]
    [InlineData(false, "Segoe UI Variable Text")]
    [InlineData(true, "Segoe UI")]
    [InlineData(true, "Segoe UI Variable Text")]
    public void FirefoxDirectionMarkersDoNotWidenTheTitle(bool composited, string family)
    {
        using IRenderer renderer = composited ? new CompositedGdiRenderer(0) : new GdiRenderer(0);
        var font = new FontStyle(family, 16);

        Size plain = renderer.Measure(PlainTitle, font);
        Assert.True(plain.Width > 0);
        Assert.Equal(plain, renderer.Measure(FirefoxTitle, font));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(260)]
    [InlineData(130)]
    public void FirefoxDirectionMarkersDrawNoBoxesOrExtraEllipsis(int width)
    {
        uint[] plain = Draw(PlainTitle, width);

        Assert.Contains(plain, pixel => pixel != 0);
        Assert.Equal(plain, Draw(FirefoxTitle, width));
    }

    [Theory]
    [InlineData("\u2066Title\u2069", "Title")]
    [InlineData("\u2067عنوان\u2069", "عنوان")]
    [InlineData("\u2068שלום\u2069", "שלום")]
    [InlineData("\u2068Outer \u2066inner\u2069 title\u2069", "Outer inner title")]
    [InlineData("Prefix \u2068truncated…", "Prefix truncated…")]
    [InlineData("Title\u2069", "Title")]
    [InlineData("\u2066\u2067\u2068\u2069", "")]
    [InlineData("", "")]
    public void IsolatesAreInvisibleEvenWhenNestedOrUnpaired(string title, string visible)
    {
        var font = new FontStyle("Segoe UI", 16);
        using var composited = new CompositedGdiRenderer(0);
        using var opaque = new GdiRenderer(0);

        Assert.Equal(composited.Measure(visible, font), composited.Measure(title, font));
        Assert.Equal(opaque.Measure(visible, font), opaque.Measure(title, font));
        Assert.Equal(Draw(visible, 400), Draw(title, 400));
    }

    // Allocate only off-screen surfaces. BeginFrame would acquire a window or
    // desktop DC; this regression needs the actual glyphs, but no on-screen window.
    private static unsafe uint[] Draw(string text, int width)
    {
        const int Height = 32;
        const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        using var renderer = new CompositedGdiRenderer(0);
        typeof(CompositedGdiRenderer).GetMethod("EnsureSurfaces", PrivateInstance)!
            .Invoke(renderer, [width, Height]);

        object surface = typeof(CompositedGdiRenderer).GetField("_frame", PrivateInstance)!.GetValue(renderer)!;
        var bits = (uint*)Pointer.Unbox(surface.GetType().GetField("Bits")!.GetValue(surface)!);
        var pixels = new Span<uint>(bits, width * Height);
        pixels.Clear();

        renderer.DrawText(text, new Rect(0, 0, width, Height), Colour.White, new FontStyle("Segoe UI", 16));

        return pixels.ToArray();
    }
}
