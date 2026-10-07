using Shubbak.Config;
using Shubbak.Core.Rendering;
using Shubbak.Ui.Layout;
using Taj.Core.Widgets;

namespace Taj.Core.Tests;

/// <summary>
/// Styling a text widget differently when its value matters.
/// </summary>
/// <remarks>
/// The bar's job is to be read at a glance. A value worth noticing should look
/// different rather than have to be read - a keyboard in a language you did not mean
/// to be in, a battery about to go, a live microphone.
/// </remarks>
public sealed class WidgetConditionTests
{
    private static TemplateWidget Load(string widget)
    {
        (TajConfig config, IReadOnlyList<Diagnostic> diagnostics) = TajConfigLoader.Load($$"""
            bar {
                profile "default" {
                    zone "right" {
                        {{widget}}
                    }
                }
            }
            """);

        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);

        BarZone zone = Assert.Single(config.Default.Zones);
        return Assert.IsType<TemplateWidget>(Assert.Single(zone.Widgets));
    }

    private static VisualNode Build(TemplateWidget widget, string value) =>
        widget.Build(new Dictionary<string, string?> { ["keyboard"] = value });

    private const string WithCondition = """
        text id="lang" template="{{ keyboard }}" colour="#a1a1a1" {
            when value="HE" colour="#f38ba8" bold=#true
        }
        """;

    [Fact]
    public void AMatchingValueTakesTheConditionalColour()
    {
        TemplateWidget widget = Load(WithCondition);

        Assert.Equal(ParseColour("#f38ba8"), Build(widget, "HE").Style.Foreground);
    }

    [Fact]
    public void AnythingElseKeepsTheOrdinaryColour()
    {
        TemplateWidget widget = Load(WithCondition);

        Assert.Equal(ParseColour("#a1a1a1"), Build(widget, "EN").Style.Foreground);
    }

    [Fact]
    public void MatchingIsCaseInsensitive()
    {
        // The source reports upper case, but nobody should have to know that to write
        // the rule.
        TemplateWidget widget = Load(WithCondition);

        Assert.Equal(ParseColour("#f38ba8"), Build(widget, "he").Style.Foreground);
    }

    [Fact]
    public void AConditionInheritsEverythingItDoesNotRestate()
    {
        // Marking a value should cost a colour, not a repetition of the whole widget.
        TemplateWidget widget = Load("""
            text id="lang" template="{{ keyboard }}" colour="#a1a1a1" font-size=17 italic=#true {
                when value="HE" colour="#f38ba8"
            }
            """);

        VisualNode node = Build(widget, "HE");

        Assert.Equal(17, node.Style.Font.Size);
        Assert.True(node.Style.Font.Italic);
    }

    [Fact]
    public void AConditionCanChangeWeight()
    {
        TemplateWidget widget = Load(WithCondition);

        Assert.True(Build(widget, "HE").Style.Font.Bold);
        Assert.False(Build(widget, "EN").Style.Font.Bold);
    }

    [Fact]
    public void TheFirstMatchWins()
    {
        // So the config reads top to bottom the way it is written.
        TemplateWidget widget = Load("""
            text id="lang" template="{{ keyboard }}" colour="#a1a1a1" {
                when value="HE" colour="#f38ba8"
                when value="HE" colour="#00ff00"
            }
            """);

        Assert.Equal(ParseColour("#f38ba8"), Build(widget, "HE").Style.Foreground);
    }

    [Fact]
    public void AWidgetWithoutConditionsIsUnaffected()
    {
        TemplateWidget widget = Load("""text id="lang" template="{{ keyboard }}" colour="#a1a1a1" """);

        Assert.Equal(ParseColour("#a1a1a1"), Build(widget, "HE").Style.Foreground);
    }

    // ---- not, and testing a source rather than the rendered text ----------------

    private const string NotSplith = """
        text id="layout" template="{{ layout | icon }}" colour="#ffffff73" {
            when of="layout" not="splith" colour="#1dfb8d" bold=#true
        }
        """;

    private static VisualNode BuildLayout(TemplateWidget widget, string layout) =>
        widget.Build(new Dictionary<string, string?> { ["layout"] = layout });

    [Fact]
    public void NotAppliesToEverythingExceptTheNamedValue()
    {
        TemplateWidget widget = Load(NotSplith);

        Assert.Equal(ParseColour("#1dfb8d"), BuildLayout(widget, "fibonacci").Style.Foreground);
        Assert.Equal(ParseColour("#1dfb8d"), BuildLayout(widget, "monocle").Style.Foreground);
        Assert.Equal(ParseColour("#1dfb8d"), BuildLayout(widget, "grid").Style.Foreground);
    }

    [Fact]
    public void NotLeavesTheNamedValueAlone()
    {
        TemplateWidget widget = Load(NotSplith);

        Assert.Equal(ParseColour("#ffffff73"), BuildLayout(widget, "splith").Style.Foreground);
    }

    [Fact]
    public void AConditionCanTestASourceRatherThanTheRenderedText()
    {
        // The layout widget renders its name as a glyph, so matching the text would
        // mean writing box-drawing characters into the config and keeping them in step
        // with whichever glyph the filter chooses. Matching the source means writing
        // the layout's name.
        TemplateWidget widget = Load(NotSplith);

        // What is actually drawn is the glyph, not the name being matched on.
        Assert.Equal("\u2502\u2502", BuildLayout(widget, "splith").Text);
        Assert.Equal("\u253C", BuildLayout(widget, "grid").Text);
    }

    [Fact]
    public void ATestedSourceBecomesADependency()
    {
        // The bar only rebuilds a widget when one of its dependencies changes. A
        // condition watching a source the template never mentions would otherwise be
        // evaluated once and never again.
        TemplateWidget widget = Load("""
            text id="x" template="{{ clock }}" colour="#ffffff73" {
                when of="layout" not="splith" colour="#1dfb8d"
            }
            """);

        Assert.Contains("clock", widget.Dependencies);
        Assert.Contains("layout", widget.Dependencies);
    }

    [Fact]
    public void AMissingSourceCountsAsEmptyRatherThanThrowing()
    {
        // A source that has not reported yet must not take the bar down, and "not
        // splith" is true of nothing at all.
        TemplateWidget widget = Load(NotSplith);

        VisualNode node = widget.Build(new Dictionary<string, string?>());

        Assert.Equal(ParseColour("#1dfb8d"), node.Style.Foreground);
    }

    // ---- numbers: above, below, and both ------------------------------------------

    private const string Battery = """
        text id="battery" template="{{ battery }}%" colour="#a1a1a1" {
            when of="battery" below=10 colour="#f38ba8" bold=#true
            when of="battery" above=89 colour="#a6e3a1"
        }
        """;

    private static VisualNode BuildBattery(TemplateWidget widget, string? battery) =>
        widget.Build(new Dictionary<string, string?> { ["battery"] = battery });

    [Fact]
    public void BelowIsStrictlyLess()
    {
        TemplateWidget widget = Load(Battery);

        Assert.Equal(ParseColour("#f38ba8"), BuildBattery(widget, "9").Style.Foreground);
        Assert.Equal(ParseColour("#f38ba8"), BuildBattery(widget, "9.9").Style.Foreground);
        Assert.Equal(ParseColour("#a1a1a1"), BuildBattery(widget, "10").Style.Foreground);
    }

    [Fact]
    public void AboveIsStrictlyGreater()
    {
        TemplateWidget widget = Load(Battery);

        Assert.Equal(ParseColour("#a6e3a1"), BuildBattery(widget, "90").Style.Foreground);
        Assert.Equal(ParseColour("#a6e3a1"), BuildBattery(widget, "100").Style.Foreground);
        Assert.Equal(ParseColour("#a1a1a1"), BuildBattery(widget, "89").Style.Foreground);
    }

    [Fact]
    public void ANumberIsReadOutOfTheValueItSitsIn()
    {
        // The source says "87%", and nobody should have to strip the sign to compare.
        TemplateWidget widget = Load(Battery);

        Assert.Equal(ParseColour("#a6e3a1"), BuildBattery(widget, "95%").Style.Foreground);
        Assert.Equal(ParseColour("#f38ba8"), BuildBattery(widget, "charge: 5 pct").Style.Foreground);
    }

    [Fact]
    public void AValueWithNoNumberFailsANumericCondition()
    {
        // A battery that has not reported is not below ten percent.
        TemplateWidget widget = Load(Battery);

        Assert.Equal(ParseColour("#a1a1a1"), BuildBattery(widget, "").Style.Foreground);
        Assert.Equal(ParseColour("#a1a1a1"), BuildBattery(widget, null).Style.Foreground);
        Assert.Equal(ParseColour("#a1a1a1"), BuildBattery(widget, "unknown").Style.Foreground);
    }

    [Fact]
    public void AboveAndBelowTogetherAreABand()
    {
        TemplateWidget widget = Load("""
            text id="cpu" template="{{ cpu }}" colour="#a1a1a1" {
                when above=40 below=80 colour="#f9e2af"
            }
            """);

        VisualNode Build(string cpu) => widget.Build(new Dictionary<string, string?> { ["cpu"] = cpu });

        Assert.Equal(ParseColour("#f9e2af"), Build("60").Style.Foreground);
        Assert.Equal(ParseColour("#a1a1a1"), Build("40").Style.Foreground);
        Assert.Equal(ParseColour("#a1a1a1"), Build("80").Style.Foreground);
        Assert.Equal(ParseColour("#a1a1a1"), Build("95").Style.Foreground);
    }

    [Fact]
    public void WithoutOfTheRenderedTextIsTheNumber()
    {
        // The template may have done the arithmetic already; the condition reads what
        // came out of it.
        TemplateWidget widget = Load("""
            text id="mem" template="{{ mem | div:1024 }}" colour="#a1a1a1" {
                when above=6 colour="#f38ba8"
            }
            """);

        VisualNode node = widget.Build(new Dictionary<string, string?> { ["mem"] = "8192" });

        Assert.Equal("8", node.Text);
        Assert.Equal(ParseColour("#f38ba8"), node.Style.Foreground);
    }

    [Fact]
    public void AQuotedThresholdIsStillANumber()
    {
        TemplateWidget widget = Load("""
            text id="cpu" template="{{ cpu }}" colour="#a1a1a1" {
                when above="80" colour="#f38ba8"
            }
            """);

        Assert.Equal(ParseColour("#f38ba8"), widget.Build(new Dictionary<string, string?> { ["cpu"] = "90" }).Style.Foreground);
    }

    [Fact]
    public void AThresholdThatIsNotANumberIsReportedAndTheConditionDropped()
    {
        (TajConfig config, IReadOnlyList<Diagnostic> diagnostics) = TajConfigLoader.Load("""
            bar {
                profile "default" {
                    zone "right" {
                        text id="cpu" template="{{ cpu }}" colour="#a1a1a1" {
                            when above="eighty" colour="#f38ba8"
                        }
                    }
                }
            }
            """);

        Diagnostic warning = Assert.Single(diagnostics, d => d.Code == "TAJ0035");
        Assert.Contains("eighty", warning.Message, StringComparison.Ordinal);

        var widget = Assert.IsType<TemplateWidget>(config.Default.Zones[0].Widgets[0]);
        Assert.Empty(widget.Conditions);
    }

    [Fact]
    public void AWhenBlockThatMatchesNothingIsReported()
    {
        // It used to be dropped without a word, and a style that can never apply looks
        // exactly like a colour that is wrong.
        (_, IReadOnlyList<Diagnostic> diagnostics) = TajConfigLoader.Load("""
            bar {
                profile "default" {
                    zone "right" {
                        text id="cpu" template="{{ cpu }}" { when colour="#f38ba8" }
                    }
                }
            }
            """);

        Assert.Single(diagnostics, d => d.Code == "TAJ0033");
    }

    [Fact]
    public void AValueAndANumberInOneBlockIsReportedAndTheNumberUsed()
    {
        (TajConfig config, IReadOnlyList<Diagnostic> diagnostics) = TajConfigLoader.Load("""
            bar {
                profile "default" {
                    zone "right" {
                        text id="cpu" template="{{ cpu }}" colour="#a1a1a1" { when value="90" above=80 colour="#f38ba8" }
                    }
                }
            }
            """);

        Assert.Single(diagnostics, d => d.Code == "TAJ0034");

        var widget = Assert.IsType<TemplateWidget>(config.Default.Zones[0].Widgets[0]);
        WidgetCondition condition = Assert.Single(widget.Conditions);

        Assert.True(condition.IsNumeric);
        Assert.True(condition.Holds("95"));
    }

    [Fact]
    public void TheNumericKeysAreNotUnknownSettings()
    {
        (_, IReadOnlyList<Diagnostic> diagnostics) = TajConfigLoader.Load("""
            bar {
                profile "default" {
                    zone "right" {
                        text id="cpu" template="{{ cpu }}" { when above=40 below=80 colour="#f9e2af" }
                    }
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void ANumericConditionAgainstASourceIsADependency()
    {
        TemplateWidget widget = Load("""
            text id="x" template="{{ clock }}" {
                when of="battery" below=10 colour="#f38ba8"
            }
            """);

        Assert.Contains("battery", widget.Dependencies);
    }

    private static Colour ParseColour(string text)
    {
        (TajConfig config, _) = TajConfigLoader.Load($$"""
            bar {
                profile "default" {
                    zone "z" { text id="t" template="x" colour="{{text}}" }
                }
            }
            """);

        return Assert.IsType<TemplateWidget>(config.Default.Zones[0].Widgets[0]).Style.Foreground;
    }
}
