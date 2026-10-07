using Shubbak.Config;
using Shubbak.Core.Rendering;
using Shubbak.Ui.Layout;
using Taj.Core.Widgets;

namespace Taj.Core.Tests;

/// <summary>
/// The <c>layout</c> widget: the active layout as a picture of itself.
/// </summary>
public sealed class LayoutWidgetTests
{
    private static readonly Colour Ink = new(0x7F, 0x84, 0x9C);
    private static readonly Colour Green = new(0xA6, 0xE3, 0xA1);

    private static LayoutWidget Widget() => new("layout", 16, 16, VisualStyle.Default with { Foreground = Ink });

    private static VisualNode Build(LayoutWidget widget, string? layout, string? windows = null)
    {
        var values = new Dictionary<string, string?> { ["layout"] = layout };
        if (windows is not null) values["windows"] = windows;
        return widget.Build(values);
    }

    private static IReadOnlyList<RectShape> Panes(VisualNode node) =>
        [.. node.Shapes.Select(s => Assert.IsType<RectShape>(s))];

    // ---- the picture -----------------------------------------------------------

    [Fact]
    public void TheLayoutBecomesFourPanesInTheWidgetsColour()
    {
        VisualNode node = Build(Widget(), "fibonacci");

        Assert.Equal(VisualKind.Shape, node.Kind);
        Assert.True(node.Visible);

        IReadOnlyList<RectShape> panes = Panes(node);
        Assert.Equal(4, panes.Count);
        Assert.All(panes, p => Assert.Equal(Ink, p.Fill));
        Assert.Equal(new UnitRect(0, 0, 0.5, 1), panes[0].Rect);
    }

    [Fact]
    public void TheBoxIsThePictureAndItsPadding()
    {
        var widget = new LayoutWidget("l", 16, 16, VisualStyle.Default, new BoxStyle(Padding: Edges.All(4)));

        VisualNode node = widget.Build(new Dictionary<string, string?> { ["layout"] = "grid" });

        Assert.Equal(24, node.Box.Width);
        Assert.Equal(24, node.Box.Height);
    }

    [Fact]
    public void NothingSaidOrNothingKnownIsNothingShown()
    {
        // Before the window manager has answered, and for a layout this bar has never
        // heard of, there is no picture to draw and the widget takes no room.
        Assert.False(Build(Widget(), null).Visible);
        Assert.False(Build(Widget(), "").Visible);
        Assert.False(Build(Widget(), "tabbed").Visible);
        Assert.Empty(Build(Widget(), "tabbed").Shapes);
    }

    [Fact]
    public void TheMainPaneCanHaveAColourOfItsOwn()
    {
        LayoutWidget widget = Widget();
        widget.MainColour = Green;

        IReadOnlyList<RectShape> panes = Panes(Build(widget, "master-left"));

        Assert.Equal(Green, panes[0].Fill);
        Assert.All(panes.Skip(1), p => Assert.Equal(Ink, p.Fill));
    }

    [Fact]
    public void PanesCanFollowTheWorkspace()
    {
        LayoutWidget widget = Widget();
        widget.Panes = null;

        Assert.Equal(2, Panes(Build(widget, "splith", windows: "2")).Count);
        Assert.Equal(6, Panes(Build(widget, "grid", windows: "6")).Count);

        // At least one, at most nine: an empty workspace is one pane, a crowded one
        // is not a smear.
        Assert.Single(Panes(Build(widget, "splith", windows: "0")));
        Assert.Single(Panes(Build(widget, "splith", windows: null)));
        Assert.Equal(LayoutThumbnail.MaxPanes, Panes(Build(widget, "grid", windows: "40")).Count);
    }

    [Fact]
    public void FollowingTheWorkspaceIsADependencyAndAFixedCountIsNot()
    {
        LayoutWidget widget = Widget();
        Assert.Equal(["layout"], widget.Dependencies);

        widget.Panes = null;
        Assert.Equal(["layout", "windows"], widget.Dependencies);

        widget.Panes = 4;
        Assert.Equal(["layout"], widget.Dependencies);
    }

    [Fact]
    public void AFixedCountIsClamped()
    {
        LayoutWidget widget = Widget();

        widget.Panes = 0;
        Assert.Equal(1, widget.Panes);

        widget.Panes = 100;
        Assert.Equal(LayoutThumbnail.MaxPanes, widget.Panes);
    }

    [Fact]
    public void AConditionRecoloursThePanesAndNotThePill()
    {
        Colour pill = new(0xFF, 0xFF, 0xFF, 0x14);

        var widget = new LayoutWidget("l", 16, 16, VisualStyle.Default with { Foreground = Ink, Background = pill });
        widget.Conditions = [new WidgetCondition("splith", VisualStyle.Default with { Foreground = Green, Background = Colour.Black }, Negate: true)];

        VisualNode other = widget.Build(new Dictionary<string, string?> { ["layout"] = "grid" });
        Assert.All(Panes(other), p => Assert.Equal(Green, p.Fill));
        Assert.Equal(pill, other.Style.Background);

        VisualNode plain = widget.Build(new Dictionary<string, string?> { ["layout"] = "splith" });
        Assert.All(Panes(plain), p => Assert.Equal(Ink, p.Fill));
    }

    [Fact]
    public void AClickableOneLightsUpOnHoverAndCarriesItsGestures()
    {
        LayoutWidget widget = Widget();
        widget.Actions = new PointerActions(Click: "layout --cycle", RightClick: "layout --cycle-back");

        VisualNode node = Build(widget, "grid");

        Assert.Equal("layout --cycle", node.OnClick);
        Assert.Equal("layout --cycle-back", node.OnRightClick);
        Assert.NotNull(node.HoverStyle);
        Assert.True(node.IsInteractive);

        Assert.Null(Build(Widget(), "grid").HoverStyle);
    }

    [Fact]
    public void TheSamePictureIsNotDrawnTwice()
    {
        // The tree is rebuilt on every clock tick and the layout changes when the user
        // changes it; the shapes are kept until something about them differs.
        LayoutWidget widget = Widget();

        IReadOnlyList<Shape> first = Build(widget, "fibonacci").Shapes;
        IReadOnlyList<Shape> again = Build(widget, "fibonacci").Shapes;
        IReadOnlyList<Shape> changed = Build(widget, "grid").Shapes;

        Assert.Same(first, again);
        Assert.NotSame(first, changed);

        widget.MainColour = Green;
        Assert.NotSame(changed, Build(widget, "grid").Shapes);
    }

    [Fact]
    public void RebuildingAnUnchangedLayoutAllocatesOnlyTheNode()
    {
        LayoutWidget widget = Widget();
        var values = new Dictionary<string, string?> { ["layout"] = "fibonacci" };

        for (int i = 0; i < 200; i++) widget.Build(values);

        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 100; i++) widget.Build(values);

        long perBuild = (GC.GetAllocatedBytesForCurrentThread() - before) / 100;

        // A VisualNode and its empty children list; nothing for the picture.
        Assert.InRange(perBuild, 1, 512);
    }

    // ---- the loader ----------------------------------------------------------------

    private static (LayoutWidget Widget, IReadOnlyList<Diagnostic> Diagnostics) Load(string widget)
    {
        (TajConfig config, IReadOnlyList<Diagnostic> diagnostics) = TajConfigLoader.Load($$"""
            bar { profile "default" { zone "right" { {{widget}} } } }
            """);

        return (config.Default.Zones.SelectMany(z => z.Widgets).OfType<LayoutWidget>().Single(), diagnostics);
    }

    [Fact]
    public void TheLoaderReadsEverySetting()
    {
        (LayoutWidget widget, IReadOnlyList<Diagnostic> diagnostics) = Load("""
            layout id="lay" source="layout" width=20 height=12 gap=2 panes=6 colour="#7f849c" main-colour="#a6e3a1" background="#ffffff14" radius=3 on-click="layout --cycle" {
                when not="splith" colour="#a6e3a1"
            }
            """);

        Assert.Empty(diagnostics);

        Assert.Equal("lay", widget.Id);
        Assert.Equal("layout", widget.Source);
        Assert.Equal(20, widget.Width);
        Assert.Equal(12, widget.Height);
        Assert.Equal(2, widget.Gap);
        Assert.Equal(6, widget.Panes);
        Assert.Equal(Ink, widget.Style.Foreground);
        Assert.Equal(Green, widget.MainColour);
        Assert.Equal(new Colour(0xFF, 0xFF, 0xFF, 0x14), widget.Style.Background);
        Assert.Equal(3, widget.Style.CornerRadius);
        Assert.Equal("layout --cycle", widget.Actions.Click);
        Assert.Single(widget.Conditions);
    }

    [Fact]
    public void TheDefaultsAreASixteenPixelSquareOfFourPanes()
    {
        (LayoutWidget widget, IReadOnlyList<Diagnostic> diagnostics) = Load("layout");

        Assert.Empty(diagnostics);
        Assert.Equal(16, widget.Width);
        Assert.Equal(16, widget.Height);
        Assert.Equal(1, widget.Gap);
        Assert.Equal(4, widget.Panes);
        Assert.Null(widget.MainColour);
        Assert.Equal("layout", widget.Source);
        Assert.Equal(Edges.All(4), widget.Box.Padding);
    }

    [Fact]
    public void SizeIsTheShorthandForASquare()
    {
        (LayoutWidget widget, _) = Load("layout size=20");

        Assert.Equal(20, widget.Width);
        Assert.Equal(20, widget.Height);

        (LayoutWidget wide, _) = Load("layout size=20 width=30");
        Assert.Equal(30, wide.Width);
        Assert.Equal(20, wide.Height);
    }

    [Fact]
    public void PanesCanBeTheWordWindows()
    {
        (LayoutWidget widget, IReadOnlyList<Diagnostic> diagnostics) = Load("""layout panes="windows" """);

        Assert.Empty(diagnostics);
        Assert.Null(widget.Panes);
        Assert.Contains("windows", widget.Dependencies);
    }

    [Theory]
    [InlineData("panes=0")]
    [InlineData("panes=12")]
    [InlineData("panes=\"lots\"")]
    public void PanesThatAreNeitherACountNorTheWordAreReported(string setting)
    {
        (LayoutWidget widget, IReadOnlyList<Diagnostic> diagnostics) = Load($"layout {setting}");

        Diagnostic warning = Assert.Single(diagnostics, d => d.Code == "TAJ0040");
        Assert.Contains("windows", warning.Hint!, StringComparison.Ordinal);
        Assert.Equal(4, widget.Panes);
    }

    [Fact]
    public void ItsSettingsAreNotUnknownSettingsAndItIsNotAnUnknownWidget()
    {
        (_, IReadOnlyList<Diagnostic> diagnostics) = Load("""
            layout size=18 gap=1 panes=4 main-color="#fff" hover-background="#fff" on-scroll-up="layout --cycle" on-scroll-down="layout --cycle-back"
            """);

        Assert.DoesNotContain(diagnostics, d => d.Code is "TAJ0015" or "TAJ0016");
    }

    [Fact]
    public void ABadMainColourIsReportedLikeAnyOther()
    {
        (_, IReadOnlyList<Diagnostic> diagnostics) = Load("""layout main-colour="greenish" """);

        Diagnostic warning = Assert.Single(diagnostics, d => d.Code == "TAJ0031");
        Assert.Contains("main-colour", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWholeThingBuildsFromConfigToTree()
    {
        (TajConfig config, IReadOnlyList<Diagnostic> diagnostics) = TajConfigLoader.Load("""
            bar {
                profile "default" {
                    zone "right" {
                        layout panes="windows" on-click="layout --cycle" { when value="monocle" colour="#f38ba8" }
                    }
                }
            }
            """);

        Assert.Empty(diagnostics);

        using var model = new BarModel(config.Default);
        model.SetValue("layout", "monocle");
        model.SetValue("windows", "3");

        VisualNode node = model.Build().SelfAndDescendants().Single(n => n.Id == "layout");

        Assert.True(node.Visible);
        RectShape pane = Assert.IsType<RectShape>(Assert.Single(node.Shapes));
        Assert.Equal(new Colour(0xF3, 0x8B, 0xA8), pane.Fill);
        Assert.Equal("layout --cycle", node.OnClick);
    }
}
