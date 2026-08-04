using Shubbak.Config;
using Taj.Core;

namespace Taj.Core.Tests;

/// <summary>
/// Reporting bar settings the loader does not recognise.
/// </summary>
/// <remarks>
/// <para>
/// Taj dropped unknown nodes without a word, on the grounds that a config written
/// for a newer Taj should still produce a working bar. That is a good reason to keep
/// loading and no reason at all to keep quiet - the overwhelmingly more common case
/// is not a config from the future but a typo, and the only symptom was a setting
/// that appeared to do nothing.
/// </para>
/// <para>
/// They stay warnings rather than errors, so the bar still builds. This is the same
/// treatment the window manager's own loader gives its sections and settings, which
/// the bar's half of the same file had never had.
/// </para>
/// </remarks>
public sealed class UnknownBarSettingTests
{
    private static IReadOnlyList<Diagnostic> Diagnose(string source) =>
        TajConfigLoader.Load(source).Diagnostics;

    private static Diagnostic Single(string source, string code)
    {
        Diagnostic diagnostic = Assert.Single(Diagnose(source), d => d.Code == code);

        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        return diagnostic;
    }

    [Fact]
    public void AMistypedBarSettingIsReportedWithASuggestion()
    {
        Diagnostic diagnostic = Single("""
            bar {
                window-manager-timout 30
                profile "default" { height 30 }
            }
            """, "TAJ0013");

        Assert.Contains("window-manager-timout", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("window-manager-timeout", diagnostic.Hint!, StringComparison.Ordinal);
    }

    [Fact]
    public void AMistypedSourceNodeIsReported()
    {
        Diagnostic diagnostic = Single("""
            bar {
                sauce "clock" kind="time" format="HH:mm"
                profile "default" { height 30 }
            }
            """, "TAJ0013");

        Assert.Contains("source", diagnostic.Hint!, StringComparison.Ordinal);
    }

    [Fact]
    public void AMistypedProfileSettingIsReported()
    {
        Diagnostic diagnostic = Single("""
            bar {
                profile "default" { hieght 34 }
            }
            """, "TAJ0014");

        Assert.Contains("height", diagnostic.Hint!, StringComparison.Ordinal);
    }

    [Fact]
    public void AMistypedWidgetIsReported()
    {
        // The worst of them to diagnose by eye: an unknown widget kind simply does
        // not appear, and a bar with a missing widget looks like a layout problem.
        Diagnostic diagnostic = Single("""
            bar {
                profile "default" {
                    height 30
                    zone "left" { txt template="{{ clock }}" }
                }
            }
            """, "TAJ0015");

        Assert.Contains("text", diagnostic.Hint!, StringComparison.Ordinal);
    }

    [Fact]
    public void AMistypedZoneSettingIsReported()
    {
        Diagnostic diagnostic = Single("""
            bar {
                profile "default" {
                    height 30
                    zone "left" { justfy "end" }
                }
            }
            """, "TAJ0015");

        Assert.Contains("justify", diagnostic.Hint!, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsWrittenAsPropertiesAreCheckedToo()
    {
        // A setting can be a child or a property, so both have to be looked at or
        // half the ways of writing it go unchecked.
        Assert.Contains(
            Diagnose("""
                bar {
                    profile "default" hieght=34 { zone "left" { } }
                }
                """),
            d => d.Code == "TAJ0014");
    }

    [Fact]
    public void TheBarStillLoadsDespiteTheTypo()
    {
        // Warnings, not errors. A config from a newer Taj must still produce a bar,
        // and so must one with a mistake in it - the alternative is no bar at all.
        (TajConfig config, IReadOnlyList<Diagnostic> diagnostics) = TajConfigLoader.Load("""
            bar {
                window-manager-timout 30
                profile "default" { height 44 }
            }
            """);

        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal(44, config.Profiles["default"].Height);

        // And the mistyped setting really was ignored, rather than half-applied.
        Assert.Equal(TajConfig.DefaultWindowManagerTimeout, config.WindowManagerTimeout);
    }

    [Theory]
    [InlineData("source \"clock\" kind=\"time\" format=\"HH:mm\"")]
    [InlineData("window-manager-timeout 30")]
    [InlineData("rule use=\"default\"")]
    public void EverythingTheLoaderDoesUnderstandIsAccepted(string setting)
    {
        // The check is only worth having if it is silent on correct configs.
        Assert.DoesNotContain(
            Diagnose($$"""
                bar {
                    {{setting}}
                    profile "default" { height 30 }
                }
                """),
            d => d.Code is "TAJ0013" or "TAJ0014" or "TAJ0015");
    }

    [Fact]
    public void AWholeRealisticProfileIsSilent()
    {
        Assert.DoesNotContain(
            Diagnose("""
                bar {
                    source "clock" kind="time" format="HH:mm" interval=500
                    window-manager-timeout 30

                    profile "default" {
                        edge "top"
                        height 34
                        background "#1e1e2e"
                        foreground "#cdd6f4"
                        font "Segoe UI"
                        font-size 12
                        padding 8

                        zone "left" justify="start" grow=0 gap=6 {
                            workspaces
                        }

                        zone "right" justify="end" {
                            text template="{{ clock }}" colour="#ffffff" bold=#true
                            spacer width=8
                        }
                    }

                    profile "presentation" extends="default" {
                        height 20
                    }
                }
                """),
            d => d.Code is "TAJ0013" or "TAJ0014" or "TAJ0015");
    }
}
