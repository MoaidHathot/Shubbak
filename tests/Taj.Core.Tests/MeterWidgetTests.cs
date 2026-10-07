using Shubbak.Config;
using Shubbak.Core.Rendering;
using Shubbak.Ui.Layout;
using Taj.Core.Widgets;

namespace Taj.Core.Tests;

/// <summary>
/// The <c>meter</c> widget: a value as how much of a range it is, as a bar or a ring.
/// </summary>
public sealed class MeterWidgetTests
{
    private static readonly Colour Fill = new(0x8D, 0xBC, 0xFF);
    private static readonly Colour Track = new(0xFF, 0xFF, 0xFF, 0x1A);

    private static MeterWidget Widget() =>
        new("battery", "battery", VisualStyle.Default with { Foreground = Fill, Background = Track, CornerRadius = 3 });

    private static VisualNode Build(MeterWidget widget, string? value) =>
        widget.Build(new Dictionary<string, string?> { ["battery"] = value });

    // ---- the bar ---------------------------------------------------------------

    [Fact]
    public void ABarIsATrackWithAFillSizedToTheFraction()
    {
        VisualNode node = Build(Widget(), "25%");

        Assert.Equal(VisualKind.Container, node.Kind);
        Assert.Equal(Track, node.Style.Background);
        Assert.Equal(60, node.Box.Width);
        Assert.Equal(6, node.Box.Height);

        VisualNode fill = Assert.Single(node.Children);
        Assert.Equal(15, fill.Box.Width);
        Assert.Equal(Fill, fill.Style.Background);
        Assert.Equal(3, fill.Style.CornerRadius);
    }

    [Fact]
    public void TheFractionIsOfTheRangeAndClamped()
    {
        MeterWidget widget = Widget();

        Assert.Equal(0.25, widget.Fraction("25"));
        Assert.Equal(1, widget.Fraction("150"));
        Assert.Equal(0, widget.Fraction("-5"));
        Assert.Null(widget.Fraction("unknown"));
        Assert.Null(widget.Fraction(null));

        widget.Min = 20;
        widget.Max = 30;
        Assert.Equal(0.5, widget.Fraction("25"));
    }

    [Fact]
    public void EmptyAndFullAreTheEnds()
    {
        Assert.Empty(Build(Widget(), "0").Children);
        Assert.Equal(60, Assert.Single(Build(Widget(), "100").Children).Box.Width);
    }

    [Fact]
    public void AValueWithNoNumberHidesTheMeter()
    {
        // A battery that has not reported is not at nought.
        Assert.False(Build(Widget(), null).Visible);
        Assert.False(Build(Widget(), "").Visible);
        Assert.False(Build(Widget(), "charging").Visible);
        Assert.True(Build(Widget(), "50").Visible);
    }

    [Fact]
    public void AVerticalBarFillsUpward()
    {
        MeterWidget widget = Widget();
        widget.Vertical = true;
        widget.Width = 6;
        widget.Height = 20;

        VisualNode node = Build(widget, "50");

        Assert.Equal(FlexDirection.Column, node.Direction);
        Assert.Equal(JustifyContent.End, node.Justify);
        Assert.Equal(10, Assert.Single(node.Children).Box.Height);
        Assert.Null(Assert.Single(node.Children).Box.Width);
    }

    [Fact]
    public void TheFillSitsInsideTheTrackOnceLaidOut()
    {
        VisualNode node = Build(Widget(), "50");
        new FlexLayout(new FixedTextMeasurer()).Arrange(node, new Shubbak.Core.Geometry.Rect(0, 0, 60, 6));

        VisualNode fill = Assert.Single(node.Children);
        Assert.Equal(new Shubbak.Core.Geometry.Rect(0, 0, 30, 6), fill.Rect);
    }

    // ---- the ring --------------------------------------------------------------

    [Fact]
    public void ARingIsTwoArcsTheTrackUnderTheValue()
    {
        MeterWidget widget = Widget();
        widget.Shape = MeterShape.Ring;
        widget.Size = 18;
        widget.Thickness = 3;

        VisualNode node = Build(widget, "25");

        Assert.Equal(VisualKind.Shape, node.Kind);
        Assert.Equal(18, node.Box.Width);
        Assert.Equal(18, node.Box.Height);
        Assert.Equal(Colour.Transparent, node.Style.Background);

        Assert.Equal(2, node.Shapes.Count);

        var track = Assert.IsType<ArcShape>(node.Shapes[0]);
        var value = Assert.IsType<ArcShape>(node.Shapes[1]);

        Assert.Equal(360, track.SweepDegrees);
        Assert.Equal(Track, track.Stroke);
        Assert.Equal(90, value.SweepDegrees, precision: 9);
        Assert.Equal(Fill, value.Stroke);
        Assert.Equal(3, value.Thickness);
    }

    [Fact]
    public void ADialStartsAndSweepsWhereItIsTold()
    {
        MeterWidget widget = Widget();
        widget.Shape = MeterShape.Ring;
        widget.Start = 225;
        widget.Sweep = 270;

        VisualNode node = Build(widget, "50");

        var track = Assert.IsType<ArcShape>(node.Shapes[0]);
        var value = Assert.IsType<ArcShape>(node.Shapes[1]);

        Assert.Equal(225, track.StartDegrees);
        Assert.Equal(270, track.SweepDegrees);
        Assert.Equal(225, value.StartDegrees);
        Assert.Equal(135, value.SweepDegrees, precision: 9);
    }

    [Fact]
    public void AnEmptyRingIsOnlyItsTrackAndATransparentTrackIsNoArc()
    {
        MeterWidget widget = Widget();
        widget.Shape = MeterShape.Ring;

        Assert.Single(Build(widget, "0").Shapes);

        widget.Style = widget.Style with { Background = Colour.Transparent };
        Assert.Single(Build(widget, "50").Shapes);
        Assert.Empty(Build(widget, "0").Shapes);
    }

    // ---- conditions and the pointer -----------------------------------------------

    [Fact]
    public void AConditionRecoloursTheFillAndNothingElse()
    {
        Colour red = new(0xF3, 0x8B, 0xA8);

        MeterWidget widget = Widget();
        widget.Conditions = [new WidgetCondition("", VisualStyle.Default with { Foreground = red, Background = Colour.Black }) { Below = 10 }];

        VisualNode low = Build(widget, "5");
        Assert.Equal(red, Assert.Single(low.Children).Style.Background);
        Assert.Equal(Track, low.Style.Background);

        VisualNode fine = Build(widget, "50");
        Assert.Equal(Fill, Assert.Single(fine.Children).Style.Background);
    }

    [Fact]
    public void AClickableMeterLightensItsTrackOnHover()
    {
        MeterWidget widget = Widget();
        widget.Actions = new PointerActions(Click: "exec settings");

        VisualNode node = Build(widget, "50");

        Assert.Equal("exec settings", node.OnClick);
        Assert.NotNull(node.HoverStyle);
        Assert.NotEqual(Track, node.HoverStyle!.Value.Background);

        Assert.Null(Build(Widget(), "50").HoverStyle);
    }

    // ---- the loader ----------------------------------------------------------------

    private static (MeterWidget Widget, IReadOnlyList<Diagnostic> Diagnostics) Load(string widget)
    {
        (TajConfig config, IReadOnlyList<Diagnostic> diagnostics) = TajConfigLoader.Load($$"""
            bar { profile "default" { zone "right" { {{widget}} } } }
            """);

        return (config.Default.Zones.SelectMany(z => z.Widgets).OfType<MeterWidget>().Single(), diagnostics);
    }

    [Fact]
    public void TheLoaderReadsABar()
    {
        (MeterWidget widget, IReadOnlyList<Diagnostic> diagnostics) = Load("""
            meter id="bat" source="battery" width=50 height=4 colour="#8dbcff" background="#ffffff14" radius=2 min=0 max=100 on-click="exec ms-settings:batterysaver" {
                when below=10 colour="#f38ba8"
            }
            """);

        Assert.Empty(diagnostics);

        Assert.Equal("bat", widget.Id);
        Assert.Equal(MeterShape.Bar, widget.Shape);
        Assert.Equal(50, widget.Width);
        Assert.Equal(4, widget.Height);
        Assert.Equal(Fill, widget.Style.Foreground);
        Assert.Equal(new Colour(0xFF, 0xFF, 0xFF, 0x14), widget.Style.Background);
        Assert.Equal(2, widget.Style.CornerRadius);
        Assert.Equal("exec ms-settings:batterysaver", widget.Actions.Click);
        Assert.Single(widget.Conditions);
    }

    [Fact]
    public void TheLoaderReadsARing()
    {
        (MeterWidget widget, IReadOnlyList<Diagnostic> diagnostics) = Load("""
            meter source="cpu" shape="ring" size=18 thickness=3 start=225 sweep=270
            """);

        Assert.Empty(diagnostics);
        Assert.Equal(MeterShape.Ring, widget.Shape);
        Assert.Equal(18, widget.Size);
        Assert.Equal(3, widget.Thickness);
        Assert.Equal(225, widget.Start);
        Assert.Equal(270, widget.Sweep);
    }

    [Fact]
    public void TheDefaultsAreAFaintTrackOfAHundred()
    {
        (MeterWidget widget, IReadOnlyList<Diagnostic> diagnostics) = Load("""meter source="x" """);

        Assert.Empty(diagnostics);
        Assert.Equal(60, widget.Width);
        Assert.Equal(6, widget.Height);
        Assert.Equal(16, widget.Size);
        Assert.Equal(0, widget.Min);
        Assert.Equal(100, widget.Max);
        Assert.Equal(360, widget.Sweep);
        Assert.Equal(Track, widget.Style.Background);
        Assert.Equal(3, widget.Style.CornerRadius);
        Assert.False(widget.Vertical);
    }

    [Fact]
    public void DirectionVerticalIsRead()
    {
        (MeterWidget widget, _) = Load("""meter source="x" direction="vertical" width=4 height=16 """);

        Assert.True(widget.Vertical);
    }

    [Fact]
    public void AnUnknownShapeIsReportedAndTheMeterIsABar()
    {
        (MeterWidget widget, IReadOnlyList<Diagnostic> diagnostics) = Load("""meter source="x" shape="triangle" """);

        Diagnostic warning = Assert.Single(diagnostics, d => d.Code == "TAJ0030");
        Assert.Contains("triangle", warning.Message, StringComparison.Ordinal);
        Assert.Equal(MeterShape.Bar, widget.Shape);
    }

    [Fact]
    public void AMeterWithoutASourceIsAnError()
    {
        (TajConfig config, IReadOnlyList<Diagnostic> diagnostics) = TajConfigLoader.Load("""
            bar { profile "default" { zone "right" { meter width=60 } } }
            """);

        Assert.Single(diagnostics, d => d.Code == "TAJ0037" && d.Severity == DiagnosticSeverity.Error);
        Assert.Empty(config.Default.Zones[0].Widgets);
    }

    [Fact]
    public void ItsSettingsAreNotUnknownSettings()
    {
        (_, IReadOnlyList<Diagnostic> diagnostics) = Load("""
            meter source="x" shape="ring" size=18 thickness=3 start=0 sweep=360 min=0 max=1 direction="vertical" on-scroll-up="a" hover-background="#fff"
            """);

        Assert.DoesNotContain(diagnostics, d => d.Code is "TAJ0015" or "TAJ0016");
    }

    [Fact]
    public void TheWholeThingBuildsFromConfigToTree()
    {
        (TajConfig config, IReadOnlyList<Diagnostic> diagnostics) = TajConfigLoader.Load("""
            bar {
                source "battery" kind="signal"
                profile "default" {
                    zone "right" {
                        meter source="battery" width=40 height=4 { when below=20 colour="#f38ba8" }
                        text template="{{ battery }}%"
                    }
                }
            }
            """);

        Assert.Empty(diagnostics);

        using var model = new BarModel(config.Default);
        model.SetValue("battery", "15");

        VisualNode root = model.Build();
        VisualNode meter = root.SelfAndDescendants().Single(n => n.Id == "meter");

        Assert.True(meter.Visible);
        Assert.Equal(6, Assert.Single(meter.Children).Box.Width);
        Assert.Equal(new Colour(0xF3, 0x8B, 0xA8), Assert.Single(meter.Children).Style.Background);
    }
}
