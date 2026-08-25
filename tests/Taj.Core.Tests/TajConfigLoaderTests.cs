using Shubbak.Config;
using Shubbak.Core.Rendering;
using Taj.Core;
using Shubbak.Ui.Layout;
using Taj.Core.Widgets;

namespace Taj.Core.Tests;

/// <summary>
/// Tests for the bar's configuration.
/// </summary>
/// <remarks>
/// These exist because of a real bug. Every profile-level setting - height,
/// background, foreground, font, font size - was read only as a KDL property
/// (<c>height=34</c>), while the shipped config wrote them as child nodes
/// (<c>height 34</c>), which is the form the rest of the file uses. The result was an
/// entire profile's appearance being silently discarded while the config validated
/// cleanly.
/// </remarks>
public sealed class TajConfigLoaderTests
{
    private static TajConfig LoadOk(string source)
    {
        (TajConfig config, IReadOnlyList<Diagnostic> diagnostics) = TajConfigLoader.Load(source);

        Diagnostic[] errors = [.. diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)];

        Assert.True(
            errors.Length == 0,
            "Unexpected errors:\n" + string.Join("\n", errors.Select(d => d.ToString())));

        return config;
    }

    [Fact]
    public void ProfileSettingsCanBeWrittenAsChildNodes()
    {
        // The form the rest of the config uses for block settings - `general` and
        // `gaps` both do - and the one that was being silently ignored.
        TajConfig config = LoadOk("""
            bar {
                profile "default" {
                    height 34
                }
            }
            """);

        Assert.Equal(34, config.Default.Height);
    }

    [Fact]
    public void ProfileSettingsCanBeWrittenAsPropertiesOnTheHeader()
    {
        // Terser for one or two settings. Properties belong on the node itself;
        // inside a block, `height=34` is a node named `height` whose first argument
        // is the operator `=`, which is a different construct entirely.
        TajConfig config = LoadOk("""
            bar {
                profile "default" height=34 background="#102030" {
                    zone "left" { text template="{{ clock }}" }
                }
            }
            """);

        Assert.Equal(34, config.Default.Height);
        Assert.Equal(new Colour(0x10, 0x20, 0x30), config.Default.Background);
    }

    [Fact]
    public void EveryProfileSettingIsHonoured()
    {
        TajConfig config = LoadOk("""
            bar {
                profile "default" {
                    height 40
                    background "#102030"
                    foreground "#aabbcc"
                    font "Cascadia Code"
                    font-size 18
                    edge "bottom"
                    padding 12

                    zone "left" {
                        text template="{{ clock }}"
                    }
                }
            }
            """);

        BarProfile profile = config.Default;

        Assert.Equal(40, profile.Height);
        Assert.Equal(new Colour(0x10, 0x20, 0x30), profile.Background);
        Assert.Equal(BarEdge.Bottom, profile.Edge);
        Assert.Equal(12, profile.Padding.Left);

        // The font and foreground reach the widgets rather than the profile, since
        // that is where they are actually used.
        VisualNode node = Assert.Single(Assert.Single(profile.Zones).Widgets)
            .Build(new Dictionary<string, string?>(StringComparer.Ordinal) { ["clock"] = "12:00" });

        Assert.Equal("Cascadia Code", node.Style.Font.Family);
        Assert.Equal(18, node.Style.Font.Size);
        Assert.Equal(new Colour(0xAA, 0xBB, 0xCC), node.Style.Foreground);
    }

    [Fact]
    public void ExtendsInheritsWhatIsNotOverridden()
    {
        TajConfig config = LoadOk("""
            bar {
                profile "default" {
                    height 34
                    background "#102030"
                    zone "left" { text template="{{ clock }}" }
                }

                profile "slim" extends="default" {
                    height 20
                }
            }
            """);

        BarProfile slim = config.Profiles["slim"];

        Assert.Equal(20, slim.Height);
        Assert.Equal(new Colour(0x10, 0x20, 0x30), slim.Background);

        // Zones are inherited too, or a variant that only changes the height would
        // come out empty.
        Assert.Single(slim.Zones);
    }

    [Fact]
    public void HideEmptyReachesTheWorkspacesWidget()
    {
        TajConfig config = LoadOk("""
            bar {
                profile "default" {
                    zone "left" {
                        workspaces hide-empty=#true
                    }
                }
            }
            """);

        var widget = Assert.IsType<WorkspacesWidget>(
            Assert.Single(Assert.Single(config.Default.Zones).Widgets));

        Assert.True(widget.HideEmpty);
    }

    [Fact]
    public void HideEmptyDefaultsToShowingEverything()
    {
        TajConfig config = LoadOk("""
            bar {
                profile "default" {
                    zone "left" { workspaces }
                }
            }
            """);

        var widget = Assert.IsType<WorkspacesWidget>(
            Assert.Single(Assert.Single(config.Default.Zones).Widgets));

        Assert.False(widget.HideEmpty);
    }

    [Fact]
    public void ClockSourcesCarryTheirTimezone()
    {
        TajConfig config = LoadOk("""
            bar {
                source "seattle" kind="time" format="HH:mm" timezone="America/Los_Angeles"
                profile "default" { zone "left" { text template="{{ seattle }}" } }
            }
            """);

        SourceSpec spec = Assert.Single(config.Sources, s => s.Name == "seattle");

        Assert.Equal("America/Los_Angeles", spec.TimeZone);
        Assert.Equal("HH:mm", spec.Argument);
    }

    [Fact]
    public void SeveralClocksCanCoexist()
    {
        // Two clocks - local plus somewhere else - is one of the most common things
        // anyone puts on a bar.
        TajConfig config = LoadOk("""
            bar {
                source "clock"   kind="time" format="ddd d MMM HH:mm"
                source "seattle" kind="time" format="ddd d MMM HH:mm" timezone="America/Los_Angeles"
                profile "default" { zone "right" { text template="{{ seattle }} {{ clock }}" } }
            }
            """);

        Assert.Contains(config.Sources, s => s.Name == "clock" && s.TimeZone is null);
        Assert.Contains(config.Sources, s => s.Name == "seattle" && s.TimeZone is not null);
    }

    [Fact]
    public void ZoneSettingsAreHonoured()
    {
        TajConfig config = LoadOk("""
            bar {
                profile "default" {
                    zone "centre" justify="center" grow=1 gap=12 {
                        text template="{{ window.title }}"
                    }
                }
            }
            """);

        BarZone zone = Assert.Single(config.Default.Zones);

        Assert.Equal(JustifyContent.Center, zone.Justify);
        Assert.Equal(1, zone.Grow);
        Assert.Equal(12, zone.Gap);
    }

    [Fact]
    public void RulesReferencingAnUnknownProfileAreReported()
    {
        (_, IReadOnlyList<Diagnostic> diagnostics) = TajConfigLoader.Load("""
            bar {
                profile "default" { zone "left" { text template="x" } }
                rule use="does-not-exist" workspace="1"
            }
            """);

        Assert.Contains(diagnostics, d => d.Code == "TAJ0002");
    }

    [Fact]
    public void AConfigWithNoBarSectionYieldsAUsableDefault()
    {
        // Someone who has configured only the window manager should still get a bar
        // rather than a blank strip.
        (TajConfig config, _) = TajConfigLoader.Load("gaps { inner 4 }");

        Assert.NotEmpty(config.Default.Zones);
        Assert.Contains(config.Sources, s => s.Name == "clock");
    }

    [Fact]
    public void UnknownWidgetsAreIgnoredRatherThanFatal()
    {
        // A config written for a newer Taj should still produce a working bar.
        TajConfig config = LoadOk("""
            bar {
                profile "default" {
                    zone "left" {
                        text template="{{ clock }}"
                        some-future-widget
                    }
                }
            }
            """);

        Assert.Single(Assert.Single(config.Default.Zones).Widgets);
    }
}
