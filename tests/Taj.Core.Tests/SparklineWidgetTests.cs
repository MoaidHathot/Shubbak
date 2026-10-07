using Shubbak.Config;
using Shubbak.Core.Rendering;
using Shubbak.Ui.Layout;
using Taj.Core.Widgets;

namespace Taj.Core.Tests;

/// <summary>
/// The <c>sparkline</c> widget: a list of numbers as a line.
/// </summary>
/// <remarks>
/// The widget's whole job is to turn a list into points in the unit square the
/// painter maps onto its box; these pin that mapping - which end is which, how the
/// scale is chosen, where a short list sits - and what the loader reads.
/// </remarks>
public sealed class SparklineWidgetTests
{
    private static readonly Colour Ink = new(0x8D, 0xBC, 0xFF);

    private static SparklineWidget Widget(int width = 60, int height = 14) =>
        new("cpu", "cpu.history", width, height, VisualStyle.Default with { Foreground = Ink });

    private static VisualNode Build(SparklineWidget widget, string? value) =>
        widget.Build(new Dictionary<string, string?> { ["cpu.history"] = value });

    private static PolylineShape Line(VisualNode node) =>
        Assert.IsType<PolylineShape>(Assert.Single(node.Shapes));

    // ---- the mapping -------------------------------------------------------------

    [Fact]
    public void TheListBecomesALineOldestAtTheLeft()
    {
        VisualNode node = Build(Widget(), "0 50 100");

        Assert.Equal(VisualKind.Shape, node.Kind);
        PolylineShape line = Line(node);

        Assert.Equal(Ink, line.Stroke);
        Assert.Equal([new UnitPoint(0, 1), new UnitPoint(0.5, 0.5), new UnitPoint(1, 0)], line.Points);
    }

    [Fact]
    public void WithoutAScaleTheDataIsItsOwn()
    {
        // The lowest reading is at the bottom and the highest at the top, whatever
        // they are.
        PolylineShape line = Line(Build(Widget(), "40 60"));

        Assert.Equal(1, line.Points[0].Y);
        Assert.Equal(0, line.Points[1].Y);
    }

    [Fact]
    public void AFixedScalePinsTheEndsAndClampsBeyondThem()
    {
        SparklineWidget widget = Widget();
        widget.Min = 0;
        widget.Max = 100;

        PolylineShape line = Line(Build(widget, "25 150 -10"));

        Assert.Equal(0.75, line.Points[0].Y, precision: 9);
        Assert.Equal(0, line.Points[1].Y);
        Assert.Equal(1, line.Points[2].Y);
    }

    [Fact]
    public void AFlatListSitsInTheMiddle()
    {
        // Along an edge half the stroke would be lost, and a steady value would read
        // as an empty graph.
        PolylineShape line = Line(Build(Widget(), "50 50 50"));

        Assert.All(line.Points, p => Assert.Equal(0.5, p.Y));
    }

    [Fact]
    public void WithACountTheNewestReadingIsAtTheRightEdge()
    {
        // Five slots and three readings: the line occupies the right three slots, so
        // time runs left to right from the first tick.
        SparklineWidget widget = Widget();
        widget.Points = 5;

        PolylineShape line = Line(Build(widget, "1 2 3"));

        Assert.Equal(0.5, line.Points[0].X, precision: 9);
        Assert.Equal(0.75, line.Points[1].X, precision: 9);
        Assert.Equal(1, line.Points[2].X, precision: 9);
    }

    [Fact]
    public void MoreReadingsThanTheCountAreSpreadAcrossTheWidth()
    {
        SparklineWidget widget = Widget();
        widget.Points = 2;

        PolylineShape line = Line(Build(widget, "1 2 3"));

        Assert.Equal([0, 0.5, 1], line.Points.Select(p => p.X));
    }

    [Fact]
    public void OneReadingIsABoxWithNoLineYet()
    {
        // Shown, so the box does not jump into the bar on the second tick; empty, since
        // a point is not a line.
        VisualNode node = Build(Widget(), "42");

        Assert.True(node.Visible);
        Assert.Empty(node.Shapes);
    }

    [Fact]
    public void NothingSaidIsNothingShown()
    {
        Assert.False(Build(Widget(), null).Visible);
        Assert.False(Build(Widget(), "").Visible);
    }

    [Fact]
    public void TheBoxIsTheGraphPlusItsPadding()
    {
        var widget = new SparklineWidget("g", "h", 60, 14, VisualStyle.Default, new BoxStyle(Padding: Edges.All(2)));

        VisualNode node = Build(new SparklineWidget("cpu", "cpu.history", 60, 14, VisualStyle.Default, new BoxStyle(Padding: Edges.All(2))), "1 2");

        Assert.Equal(64, node.Box.Width);
        Assert.Equal(18, node.Box.Height);
        Assert.Equal(64, widget.Build(new Dictionary<string, string?> { ["h"] = "1 2" }).Box.Width);
    }

    [Fact]
    public void TheLineCarriesItsThicknessAndWash()
    {
        SparklineWidget widget = Widget();
        widget.Thickness = 2;
        widget.Fill = new Colour(0x8D, 0xBC, 0xFF, 0x40);

        PolylineShape line = Line(Build(widget, "1 2"));

        Assert.Equal(2, line.Thickness);
        Assert.Equal(widget.Fill, line.Fill);
    }

    [Fact]
    public void ItDependsOnItsSourceAndOnWhatItsConditionsTest()
    {
        SparklineWidget widget = Widget();
        Assert.Equal(["cpu.history"], widget.Dependencies);

        widget.Conditions = [new WidgetCondition("", VisualStyle.Default, Source: "charging") { Above = 0 }];
        Assert.Equal(["cpu.history", "charging"], widget.Dependencies);
    }

    [Fact]
    public void AConditionTestsTheNewestReading()
    {
        Colour red = new(0xF3, 0x8B, 0xA8);

        SparklineWidget widget = Widget();
        widget.Conditions = [new WidgetCondition("", VisualStyle.Default with { Foreground = red }) { Above = 80 }];

        Assert.Equal(red, Line(Build(widget, "10 20 90")).Stroke);
        Assert.Equal(Ink, Line(Build(widget, "90 20 10")).Stroke);
    }

    [Fact]
    public void AValueThatHasNotChangedIsNotParsedAgain()
    {
        // The tree is rebuilt whenever anything on the bar changes - a seconds clock,
        // once a second - and the history is the same string reference each time its
        // own source has not ticked. The plot is kept with it, as the icon widget keeps
        // its bitmap; parsing sixty numbers for a value that had not changed was most
        // of what a sparkline cost per rebuild.
        SparklineWidget widget = Widget();
        string history = "1 2 3 4 5";

        IReadOnlyList<UnitPoint> first = Line(Build(widget, history)).Points;
        IReadOnlyList<UnitPoint> again = Line(Build(widget, history)).Points;
        IReadOnlyList<UnitPoint> changed = Line(Build(widget, "1 2 3 4 6")).Points;

        Assert.Same(first, again);
        Assert.NotSame(first, changed);

        // Equal content under a different reference is the same value too: the model
        // hands out the string it was given, but a host that rebuilt it would not be
        // punished for it.
        Assert.Same(changed, Line(Build(widget, new string("1 2 3 4 6".AsSpan()))).Points);
    }

    // ---- the loader ----------------------------------------------------------------

    private static (SparklineWidget Widget, IReadOnlyList<Diagnostic> Diagnostics) Load(string bar)
    {
        (TajConfig config, IReadOnlyList<Diagnostic> diagnostics) = TajConfigLoader.Load(bar);

        SparklineWidget widget = config.Default.Zones.SelectMany(z => z.Widgets).OfType<SparklineWidget>().Single();
        return (widget, diagnostics);
    }

    [Fact]
    public void TheLoaderReadsEverySetting()
    {
        (SparklineWidget widget, IReadOnlyList<Diagnostic> diagnostics) = Load("""
            bar {
                profile "default" {
                    zone "right" {
                        sparkline id="g" source="cpu.history" width=80 height=12 thickness=2 colour="#8dbcff" fill="#8dbcff40" min=0 max=100 points=40 radius=3 background="#ffffff14" on-click="exec taskmgr" {
                            when above=80 colour="#f38ba8"
                        }
                    }
                }
            }
            """);

        Assert.Empty(diagnostics);

        Assert.Equal("g", widget.Id);
        Assert.Equal("cpu.history", widget.Source);
        Assert.Equal(80, widget.Width);
        Assert.Equal(12, widget.Height);
        Assert.Equal(2, widget.Thickness);
        Assert.Equal(Ink, widget.Style.Foreground);
        Assert.Equal(new Colour(0x8D, 0xBC, 0xFF, 0x40), widget.Fill);
        Assert.Equal(0, widget.Min);
        Assert.Equal(100, widget.Max);
        Assert.Equal(40, widget.Points);
        Assert.Equal(3, widget.Style.CornerRadius);
        Assert.Equal("exec taskmgr", widget.Actions.Click);
        Assert.Single(widget.Conditions);
    }

    [Fact]
    public void TheDefaultsAreAMinuteInTheRoomOfAClock()
    {
        (SparklineWidget widget, IReadOnlyList<Diagnostic> diagnostics) = Load("""
            bar { profile "default" { zone "right" { sparkline source="h" } } }
            """);

        Assert.Empty(diagnostics);
        Assert.Equal(60, widget.Width);
        Assert.Equal(14, widget.Height);
        Assert.Equal(1, widget.Thickness);
        Assert.Null(widget.Min);
        Assert.Null(widget.Max);
        Assert.Null(widget.Points);
        Assert.Equal(Colour.Transparent, widget.Fill);
    }

    [Fact]
    public void ASparklineOnAHistoryTakesTheHistorysCount()
    {
        // So the graph fills in from the right over its first minute rather than
        // stretching two readings across the whole width.
        (SparklineWidget widget, IReadOnlyList<Diagnostic> diagnostics) = Load("""
            bar {
                source "cpu" kind="signal" history=30
                profile "default" { zone "right" { sparkline source="cpu.history" } }
            }
            """);

        Assert.Empty(diagnostics);
        Assert.Equal(30, widget.Points);
    }

    [Fact]
    public void AWrittenCountWins()
    {
        (SparklineWidget widget, _) = Load("""
            bar {
                source "cpu" kind="signal" history=30
                profile "default" { zone "right" { sparkline source="cpu.history" points=10 } }
            }
            """);

        Assert.Equal(10, widget.Points);
    }

    [Fact]
    public void ASparklineOnTheBareSourceIsPointedAtTheHistory()
    {
        (_, IReadOnlyList<Diagnostic> diagnostics) = Load("""
            bar {
                source "cpu" kind="signal" history=30
                profile "default" { zone "right" { sparkline source="cpu" } }
            }
            """);

        Diagnostic warning = Assert.Single(diagnostics, d => d.Code == "TAJ0039");
        Assert.Contains("cpu.history", warning.Hint!, StringComparison.Ordinal);
    }

    [Fact]
    public void ASparklineWithoutASourceIsAnError()
    {
        (TajConfig config, IReadOnlyList<Diagnostic> diagnostics) = TajConfigLoader.Load("""
            bar { profile "default" { zone "right" { sparkline width=60 } } }
            """);

        Diagnostic error = Assert.Single(diagnostics, d => d.Code == "TAJ0037");
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Empty(config.Default.Zones[0].Widgets);
    }

    [Fact]
    public void AScaleTheWrongWayRoundIsReportedAndDropped()
    {
        (SparklineWidget widget, IReadOnlyList<Diagnostic> diagnostics) = Load("""
            bar { profile "default" { zone "right" { sparkline source="h" min=100 max=0 } } }
            """);

        Assert.Single(diagnostics, d => d.Code == "TAJ0038");
        Assert.Null(widget.Min);
        Assert.Null(widget.Max);
    }

    [Fact]
    public void ASizeThatIsNotPositiveIsReportedAndTheDefaultUsed()
    {
        (SparklineWidget widget, IReadOnlyList<Diagnostic> diagnostics) = Load("""
            bar { profile "default" { zone "right" { sparkline source="h" width=0 height="tall" } } }
            """);

        Assert.Equal(2, diagnostics.Count(d => d.Code == "TAJ0025"));
        Assert.Equal(60, widget.Width);
        Assert.Equal(14, widget.Height);
    }

    [Fact]
    public void ItsSettingsAreNotUnknownSettingsAndItIsNotAnUnknownWidget()
    {
        (_, IReadOnlyList<Diagnostic> diagnostics) = Load("""
            bar { profile "default" { zone "right" { sparkline source="h" fill="#fff" points=5 thickness=2 on-scroll-up="x" } } }
            """);

        Assert.DoesNotContain(diagnostics, d => d.Code is "TAJ0015" or "TAJ0016");
    }
}
