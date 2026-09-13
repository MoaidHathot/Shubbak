using System.Text.Json;
using Shubbak.Config;
using Shubbak.Core.Rendering;
using Shubbak.Ipc;
using Shubbak.Ui.Layout;
using Taj.Core;
using Taj.Core.Widgets;

namespace Taj.Core.Tests;

/// <summary>
/// The <c>icon</c> widget: the focused window's icon, as the window manager sends it.
/// </summary>
/// <remarks>
/// The picture itself comes from the window manager over the pipe and cannot be
/// fetched here. What can be tested is the boundary: the wire shape decodes into a
/// bitmap, the widget hides without one and shows with one, decodes once rather than
/// per tick, and the loader reads the node.
/// </remarks>
public sealed class IconWidgetTests
{
    /// <summary>A 2x2 icon on the wire: red, green / blue, transparent.</summary>
    private static string Payload(long handle = 42)
    {
        byte[] bgra =
        [
            0, 0, 255, 255,     // red
            0, 255, 0, 255,     // green
            255, 0, 0, 255,     // blue
            0, 0, 0, 0,         // transparent
        ];

        return JsonSerializer.Serialize(
            new WindowIcon(handle, 2, 2, Convert.ToBase64String(bgra), "window"),
            IpcJsonContext.Default.WindowIcon);
    }

    private static IconWidget Widget() =>
        new("app", FocusedWindow.IconKey, 20, VisualStyle.Default, new BoxStyle(Padding: Edges.All(4)));

    // ---- the wire shape ----------------------------------------------------

    [Fact]
    public void TheWireShapeDecodesIntoABitmap()
    {
        ImageBitmap? image = IconWidget.Parse(Payload());

        Assert.NotNull(image);
        Assert.Equal(2, image.Width);
        Assert.Equal(2, image.Height);

        // Packed 0xAARRGGBB from bytes that travel blue, green, red, alpha.
        Assert.Equal(0xFFFF0000u, image.Pixels[0]);
        Assert.Equal(0xFF00FF00u, image.Pixels[1]);
        Assert.Equal(0xFF0000FFu, image.Pixels[2]);
        Assert.Equal(0x00000000u, image.Pixels[3]);
    }

    [Fact]
    public void ABitmapWhoseBytesDoNotMatchItsSizeIsRefused()
    {
        var wrong = new WindowIcon(1, 4, 4, Convert.ToBase64String(new byte[16]), "window");

        Assert.Null(wrong.Decode());
        Assert.Null(IconWidget.Parse(JsonSerializer.Serialize(wrong, IpcJsonContext.Default.WindowIcon)));
    }

    [Fact]
    public void GarbageIsNotAnIcon()
    {
        Assert.Null(IconWidget.Parse("not json"));
        Assert.Null(IconWidget.Parse("{\"handle\":1}"));
    }

    [Fact]
    public void TheMethodNameIsTheOneTheDaemonAnswers() =>
        Assert.Equal("window-icon", WindowIcon.Method);

    // ---- the widget --------------------------------------------------------

    [Fact]
    public void NothingFocusedLeavesNoTrace()
    {
        // Like a text widget with nothing to say: hidden, so the title beside it
        // does not get a gap where a picture would have been.
        VisualNode node = Widget().Build(new Dictionary<string, string?>());

        Assert.False(node.Visible);
        Assert.Null(node.Image);
    }

    [Fact]
    public void AnIconIsAnImageNodeOfTheConfiguredSize()
    {
        VisualNode node = Widget().Build(new Dictionary<string, string?> { [FocusedWindow.IconKey] = Payload() });

        Assert.True(node.Visible);
        Assert.Equal(VisualKind.Image, node.Kind);
        Assert.NotNull(node.Image);

        // The box is the picture plus its padding, whatever size the pixels came at:
        // a 2x2 icon on the wire is still drawn in a 20-pixel square.
        Assert.Equal(28, node.Box.Width);
        Assert.Equal(28, node.Box.Height);
    }

    [Fact]
    public void TheSameValueIsDecodedOnce()
    {
        // The tree is rebuilt on every clock tick with the same few kilobytes of
        // base64 in it; decoding them each time would be measurable for nothing.
        IconWidget widget = Widget();
        string payload = Payload();

        ImageBitmap? first = widget.Build(new Dictionary<string, string?> { [FocusedWindow.IconKey] = payload }).Image;
        ImageBitmap? second = widget.Build(new Dictionary<string, string?> { [FocusedWindow.IconKey] = payload }).Image;

        Assert.Same(first, second);

        // And an equal string that is a different object still hits.
        ImageBitmap? third = widget.Build(new Dictionary<string, string?> { [FocusedWindow.IconKey] = new string(payload) }).Image;
        Assert.Same(first, third);
    }

    [Fact]
    public void ANewValueIsANewPicture()
    {
        IconWidget widget = Widget();

        ImageBitmap? first = widget.Build(new Dictionary<string, string?> { [FocusedWindow.IconKey] = Payload(1) }).Image;
        ImageBitmap? second = widget.Build(new Dictionary<string, string?> { [FocusedWindow.IconKey] = Payload(2) }).Image;

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotSame(first, second);
    }

    [Fact]
    public void AClickableIconHoversLikeAGlyph()
    {
        IconWidget widget = Widget();
        widget.OnClick = "focus --next";

        VisualNode node = widget.Build(new Dictionary<string, string?> { [FocusedWindow.IconKey] = Payload() });

        Assert.Equal("focus --next", node.OnClick);
        Assert.NotNull(node.HoverStyle);
        Assert.Equal(new Colour(0xFF, 0xFF, 0xFF, 0x1A), node.HoverStyle.Value.Background);
    }

    [Fact]
    public void ItDependsOnItsSource()
    {
        Assert.Equal([FocusedWindow.IconKey], Widget().Dependencies);
        Assert.Equal(["custom"], new IconWidget("x", "custom", 16, VisualStyle.Default).Dependencies);
    }

    // ---- the loader --------------------------------------------------------

    [Fact]
    public void TheLoaderReadsAnIconNode()
    {
        (TajConfig config, IReadOnlyList<Diagnostic> diagnostics) = TajConfigLoader.Load("""
            bar {
                profile "default" {
                    zone "centre" justify="center" grow=1 {
                        icon id="app" size=24 background="#ffffff14" radius=6
                        text template="{{ window.title }}"
                    }
                }
            }
            """);

        Assert.DoesNotContain(diagnostics, d => d.Code.StartsWith("TAJ", StringComparison.Ordinal));

        var icon = Assert.IsType<IconWidget>(config.Profiles["default"].Zones[0].Widgets[0]);

        Assert.Equal("app", icon.Id);
        Assert.Equal(24, icon.Size);
        Assert.Equal(FocusedWindow.IconKey, icon.Source);
        Assert.Equal(new Colour(0xFF, 0xFF, 0xFF, 0x14), icon.Style.Background);
        Assert.Equal(6, icon.Style.CornerRadius);
    }

    [Fact]
    public void TheDefaultsAreTheFocusedWindowAtTwentyPixels()
    {
        (TajConfig config, _) = TajConfigLoader.Load("""
            bar {
                profile "default" { zone "centre" { icon } }
            }
            """);

        var icon = Assert.IsType<IconWidget>(config.Profiles["default"].Zones[0].Widgets[0]);

        Assert.Equal(20, icon.Size);
        Assert.Equal(FocusedWindow.IconKey, icon.Source);
        Assert.Equal("icon", icon.Id);
    }

    [Fact]
    public void AnotherSourceCanBeDrawn()
    {
        (TajConfig config, _) = TajConfigLoader.Load("""
            bar {
                profile "default" { zone "centre" { icon source="weather.icon" size=16 } }
            }
            """);

        var icon = Assert.IsType<IconWidget>(config.Profiles["default"].Zones[0].Widgets[0]);

        Assert.Equal("weather.icon", icon.Source);
        Assert.Equal(["weather.icon"], icon.Dependencies);
    }

    [Fact]
    public void AMistypedIconSettingIsReported()
    {
        (_, IReadOnlyList<Diagnostic> diagnostics) = TajConfigLoader.Load("""
            bar {
                profile "default" { zone "centre" { icon sizee=24 } }
            }
            """);

        Diagnostic warning = Assert.Single(diagnostics, d => d.Code == "TAJ0016");
        Assert.Contains("size", warning.Hint!, StringComparison.Ordinal);
    }

    [Fact]
    public void AColourOnAnIconIsPointedOut()
    {
        // The one setting every other widget takes that would silently do nothing here.
        (_, IReadOnlyList<Diagnostic> diagnostics) = TajConfigLoader.Load("""
            bar {
                profile "default" { zone "centre" { icon colour="#fff" } }
            }
            """);

        Assert.Contains(diagnostics, d => d.Code == "TAJ0016" && d.Message.Contains("no text to colour", StringComparison.Ordinal));
    }

    [Fact]
    public void ANonsensicalSizeIsReportedAndDefaulted()
    {
        (TajConfig config, IReadOnlyList<Diagnostic> diagnostics) = TajConfigLoader.Load("""
            bar {
                profile "default" { zone "centre" { icon size=0 } }
            }
            """);

        Assert.Single(diagnostics, d => d.Code == "TAJ0025");
        Assert.Equal(20, Assert.IsType<IconWidget>(config.Profiles["default"].Zones[0].Widgets[0]).Size);
    }

    [Fact]
    public void TheFocusedWindowPayloadCarriesTheHandle()
    {
        // The connection asks the window manager for the icon by handle, so the
        // handle has to survive the parse that used to drop it.
        string json = JsonSerializer.Serialize(
            new WindowInfo(1, 0xABCD, "Title", "Class", "app", "tiling", true, 0, 0, 10, 10),
            IpcJsonContext.Default.WindowInfo);

        FocusedWindowValues? values = FocusedWindow.Parse(json);

        Assert.NotNull(values);
        Assert.Equal(0xABCD, values.Value.Handle);
        Assert.Equal(0, FocusedWindowValues.None.Handle);
    }
}
