using Shubbak.Config;
using Taj.Core;
using Taj.Core.Layout;
using Taj.Core.Widgets;

namespace Taj.Core.Tests;

/// <summary>
/// Parses the bar section of the author's real config.
/// </summary>
/// <remarks>
/// A config that parses without errors is not the same as a config that means what
/// it says. Unknown widget names are ignored by design, unknown attributes are simply
/// not read, and both are silent - so a mistyped key produces a bar that renders
/// perfectly and does the wrong thing. This pins the settings that have actually gone
/// wrong in use.
/// </remarks>
public sealed class RealConfigBarTests
{
    private const string BarSection = """
        bar {
            source "clock"   kind="time" format="ddd d MMM HH:mm" interval=500
            source "seattle" kind="time" format="ddd d MMM HH:mm" interval=500 timezone="America/Los_Angeles"

            profile "default" {
                height 34
                background "#1e1e2e"
                foreground "#cdd6f4"
                font "Segoe UI"
                font-size 15

                zone "left" justify="start" gap=4 {
                    workspaces hide-empty=#true \
                        active-background="#00000000" \
                        colour="#ffffffe6" \
                        empty-colour="#ffffff80" \
                        active-colour="#8dbcff" \
                        focused-colour="#1dfb8d"
                }

                zone "centre" justify="center" grow=1 {
                    text template="{{ window.title | truncate:90 }}"
                }

                zone "right" justify="end" gap=12 {
                    text id="seattle" template="{{ seattle }}" colour="#ffffff80"
                    text id="mode"    template="{{ binding_mode }}" colour="#f9e2af"
                    text id="layout"  template="{{ layout | icon }}" colour="#8dbcff" \
                         bold=#true font-size=17
                    text id="clock"   template="{{ clock }}" colour="#8dbcff" bold=#true
                }
            }

            profile "presentation" extends="default" {
                height 24
                zone "centre" justify="center" grow=1 { }
                zone "right" justify="end" gap=10 {
                    text id="clock" template="{{ clock }}" colour="#8dbcff"
                }
            }

            rule use="presentation" workspace="\\"
            rule use="presentation" workspace=";"
        }
        """;

    private static TajConfig Load()
    {
        (TajConfig config, IReadOnlyList<Diagnostic> diagnostics) = TajConfigLoader.Load(BarSection);

        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);

        return config;
    }

    private static WorkspacesWidget Workspaces(BarProfile profile) =>
        (WorkspacesWidget)profile.Zones.Single(z => z.Id == "left").Widgets[0];

    [Fact]
    public void LineContinuationsCarryTheAttributesThatFollow()
    {
        // Written across several lines because there are six of them. If the parser
        // dropped anything after the backslash, the bar would still render and every
        // setting after the first would be silently ignored.
        WorkspacesWidget workspaces = Workspaces(Load().Profiles["default"]);

        Assert.True(workspaces.ActiveStyle.Background.IsTransparent);
        Assert.Equal(new Colour(0x8D, 0xBC, 0xFF), workspaces.ActiveStyle.Foreground);
        Assert.NotNull(workspaces.FocusedStyle);
        Assert.Equal(new Colour(0x1D, 0xFB, 0x8D), workspaces.FocusedStyle.Value.Foreground);
        Assert.True(workspaces.HideEmpty);
    }

    [Fact]
    public void TheDeclaredClockOverridesTheBuiltInOne()
    {
        SourceSpec clock = Assert.Single(Load().Sources, s => s.Name == "clock");

        Assert.Equal("ddd d MMM HH:mm", clock.Argument);
    }

    [Fact]
    public void TheLayoutIndicatorIsBoldAndLarger()
    {
        BarProfile profile = Load().Profiles["default"];

        var layout = (TemplateWidget)profile.Zones
            .Single(z => z.Id == "right").Widgets.Single(w => w.Id == "layout");

        Assert.True(layout.Style.Font.Bold);
        Assert.Equal(17, layout.Style.Font.Size);
    }

    [Fact]
    public void ThePresentationProfileKeepsTheZoneThatGrows()
    {
        // Without it the remaining zones pack against the left edge and the clock
        // ends up on the wrong side of the bar.
        BarProfile presentation = Load().Profiles["presentation"];

        Assert.Equal(["left", "centre", "right"], presentation.Zones.Select(z => z.Id));
        Assert.Contains(presentation.Zones, z => z.Grow > 0);
    }

    [Fact]
    public void ThePresentationProfileStillShowsWorkspaces()
    {
        // Inherited rather than redeclared. Before zones were merged, declaring any
        // zone discarded every inherited one.
        BarProfile presentation = Load().Profiles["presentation"];

        Assert.NotNull(Workspaces(presentation));
    }

    [Fact]
    public void ThePresentationProfileEmptiesTheCentreWithoutLosingIt()
    {
        BarZone centre = Load().Profiles["presentation"].Zones.Single(z => z.Id == "centre");

        Assert.Empty(centre.Widgets);
        Assert.True(centre.Grow > 0);
    }
}
