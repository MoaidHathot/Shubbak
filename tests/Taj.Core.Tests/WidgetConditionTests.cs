using Shubbak.Config;
using Taj.Core.Layout;
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
