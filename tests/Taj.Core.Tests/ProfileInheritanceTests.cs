using Shubbak.Config;
using Taj.Core;

namespace Taj.Core.Tests;

/// <summary>
/// Tests for how a profile inherits from the one it extends.
/// </summary>
/// <remarks>
/// <c>extends</c> is meant to let a variant change one thing. It used to substitute
/// zones wholesale the moment a profile declared any, so a variant that redefined
/// "left" and "right" silently lost "centre" - and with it the only zone that grows.
/// The remaining zones then packed against the left edge, putting the clock on the
/// wrong side. The visible symptom was alignment, which is nowhere near the cause.
/// </remarks>
public sealed class ProfileInheritanceTests
{
    private static TajConfig LoadOk(string source)
    {
        (TajConfig config, IReadOnlyList<Diagnostic> diagnostics) = TajConfigLoader.Load(source);

        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);

        return config;
    }

    private const string TwoProfiles = """
        bar {
            profile "default" {
                zone "left"   justify="start"  { workspaces }
                zone "centre" justify="center" grow=1 { text template="{{ window.title }}" }
                zone "right"  justify="end"    { text id="clock" template="{{ clock }}" }
            }

            profile "slim" extends="default" {
                height 24
                zone "right" justify="end" { text id="clock" template="{{ clock }}" }
            }
        }
        """;

    [Fact]
    public void AnUndeclaredZoneIsInherited()
    {
        BarProfile slim = LoadOk(TwoProfiles).Profiles["slim"];

        Assert.Contains(slim.Zones, z => string.Equals(z.Id, "centre", StringComparison.Ordinal));
    }

    [Fact]
    public void InheritedZonesKeepTheirOrder()
    {
        // The order is the layout. A redefined "right" that moved to the front would
        // put the clock on the left, which is exactly what happened.
        BarProfile slim = LoadOk(TwoProfiles).Profiles["slim"];

        Assert.Equal(["left", "centre", "right"], slim.Zones.Select(z => z.Id));
    }

    [Fact]
    public void TheGrowingZoneSurvivesInheritance()
    {
        // Without a zone that grows, everything packs against the leading edge and
        // `justify="end"` on the trailing zone has nothing to push against.
        BarProfile slim = LoadOk(TwoProfiles).Profiles["slim"];

        Assert.Contains(slim.Zones, z => z.Grow > 0);
    }

    [Fact]
    public void ARedeclaredZoneReplacesTheInheritedOne()
    {
        BarProfile slim = LoadOk(TwoProfiles).Profiles["slim"];

        BarZone right = Assert.Single(slim.Zones, z => string.Equals(z.Id, "right", StringComparison.Ordinal));

        // The variant's own version: one widget, not the parent's.
        Assert.Single(right.Widgets);
    }

    [Fact]
    public void OtherSettingsStillOverride()
    {
        TajConfig config = LoadOk(TwoProfiles);

        Assert.Equal(24, config.Profiles["slim"].Height);
        Assert.NotEqual(24, config.Profiles["default"].Height);
    }

    [Fact]
    public void AProfileWithNoZonesInheritsThemAll()
    {
        TajConfig config = LoadOk("""
            bar {
                profile "default" {
                    zone "left"  { workspaces }
                    zone "right" { text template="{{ clock }}" }
                }

                profile "tall" extends="default" { height 40 }
            }
            """);

        Assert.Equal(["left", "right"], config.Profiles["tall"].Zones.Select(z => z.Id));
        Assert.Equal(40, config.Profiles["tall"].Height);
    }

    [Fact]
    public void AZoneCanBeEmptiedByRedeclaringItWithNoWidgets()
    {
        // The way out, now that declaring a zone no longer discards the rest.
        TajConfig config = LoadOk("""
            bar {
                profile "default" {
                    zone "left"   { workspaces }
                    zone "centre" grow=1 { text template="{{ window.title }}" }
                }

                profile "bare" extends="default" {
                    zone "centre" grow=1 { }
                }
            }
            """);

        BarZone centre = Assert.Single(
            config.Profiles["bare"].Zones,
            z => string.Equals(z.Id, "centre", StringComparison.Ordinal));

        Assert.Empty(centre.Widgets);

        // Still grows, so the zones either side stay anchored where they were.
        Assert.True(centre.Grow > 0);
    }
}
