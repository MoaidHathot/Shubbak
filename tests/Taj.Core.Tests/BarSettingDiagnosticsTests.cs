using Shubbak.Config;
using Shubbak.Ui.Layout;
using Taj.Core.Sources;
using Taj.Core.Widgets;

namespace Taj.Core.Tests;

/// <summary>
/// The bar settings that used to be silent when wrong, and the ones that are new.
/// </summary>
/// <remarks>
/// <para>
/// Every one of these was a way to write something the bar would accept and ignore:
/// a colour that did not parse fell back to the default, an <c>extends</c> naming a
/// profile that did not exist fell back to the built-ins, a source with a kind the bar
/// had never heard of produced nothing, a justify with a typo left the widgets where
/// they were. Each produced a bar that was wrong in a way nothing connected to the
/// line that caused it, and a clean <c>check-config</c>.
/// </para>
/// <para>
/// All warnings: the bar still builds, since a wrong colour is not a reason to have
/// no bar. The codes continue the TAJ series from where it stood.
/// </para>
/// </remarks>
public sealed class BarSettingDiagnosticsTests
{
    private static (TajConfig Config, IReadOnlyList<Diagnostic> Diagnostics) Load(string source) =>
        TajConfigLoader.Load(source);

    private static Diagnostic Warning(string source, string code)
    {
        Diagnostic diagnostic = Assert.Single(Load(source).Diagnostics, d => d.Code == code);

        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        return diagnostic;
    }

    // ---- dpi-scaling (TAJ0026) ----

    [Fact]
    public void DpiScalingIsOnUnlessTurnedOff()
    {
        Assert.True(Load("bar { profile \"default\" { height 30 } }").Config.DpiScaling);
        Assert.False(Load("bar { dpi-scaling #false\n profile \"default\" { height 30 } }").Config.DpiScaling);
        Assert.False(Load("bar dpi-scaling=#false { profile \"default\" { height 30 } }").Config.DpiScaling);
        Assert.True(TajConfigLoader.CreateDefault().DpiScaling);
    }

    [Fact]
    public void DpiScalingThatIsNotABooleanIsReportedAndLeftOn()
    {
        (TajConfig config, IReadOnlyList<Diagnostic> diagnostics) = Load("""
            bar {
                dpi-scaling "yes"
                profile "default" { height 30 }
            }
            """);

        Diagnostic warning = Assert.Single(diagnostics, d => d.Code == "TAJ0026");

        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.True(config.DpiScaling);
    }

    // ---- source kinds (TAJ0027, TAJ0028) ----

    [Fact]
    public void AnUnknownSourceKindIsReportedWithAGuess()
    {
        Diagnostic warning = Warning("""
            bar {
                source "cpu" kind="cmmand" command="cpu.exe"
                profile "default" { height 30 }
            }
            """, "TAJ0027");

        Assert.Contains("cmmand", warning.Message, StringComparison.Ordinal);
        Assert.Contains("command", warning.Hint!, StringComparison.Ordinal);
    }

    [Fact]
    public void TheKnownKindsAreNotReported()
    {
        (_, IReadOnlyList<Diagnostic> diagnostics) = Load("""
            bar {
                source "a" kind="time" format="HH:mm"
                source "b" kind="command" command="x.exe"
                source "c" kind="keyboard"
                profile "default" { height 30 }
            }
            """);

        Assert.DoesNotContain(diagnostics, d => d.Code is "TAJ0027" or "TAJ0028");
    }

    [Fact]
    public void ACommandSourceWithNothingToRunIsReported()
    {
        Diagnostic warning = Warning("""
            bar {
                source "cpu" kind="command"
                profile "default" { height 30 }
            }
            """, "TAJ0028");

        Assert.Contains("cpu", warning.Message, StringComparison.Ordinal);
    }

    // ---- extends (TAJ0029) ----

    [Fact]
    public void ExtendingAProfileThatDoesNotExistIsReported()
    {
        Diagnostic warning = Warning("""
            bar {
                profile "default" { height 30 }
                profile "slim" { extends "defualt"; height 20 }
            }
            """, "TAJ0029");

        Assert.Contains("defualt", warning.Message, StringComparison.Ordinal);
        Assert.Contains("default", warning.Hint!, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtendingAProfileDeclaredLaterIsReportedAsOrder()
    {
        // Profiles are read top to bottom, so the parent has to come first. The hint
        // says so rather than suggesting the name back, since the name is right.
        Diagnostic warning = Warning("""
            bar {
                profile "slim" { extends "default"; height 20 }
                profile "default" { height 30 }
            }
            """, "TAJ0029");

        Assert.Contains("before", warning.Hint!, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtendingAProfileThatExistsIsFine()
    {
        (_, IReadOnlyList<Diagnostic> diagnostics) = Load("""
            bar {
                profile "default" { height 30 }
                profile "slim" { extends "default"; height 20 }
            }
            """);

        Assert.DoesNotContain(diagnostics, d => d.Code == "TAJ0029");
    }

    // ---- edge and justify (TAJ0030) ----

    [Fact]
    public void AnUnknownEdgeIsReportedAndTheBarStaysAtTheTop()
    {
        (TajConfig config, IReadOnlyList<Diagnostic> diagnostics) = Load("""
            bar { profile "default" { edge "left"; height 30 } }
            """);

        Diagnostic warning = Assert.Single(diagnostics, d => d.Code == "TAJ0030");

        Assert.Contains("left", warning.Message, StringComparison.Ordinal);
        Assert.Equal(BarEdge.Top, config.Default.Edge);
    }

    [Fact]
    public void TopAndBottomAreTheEdgesInAnyCase()
    {
        Assert.Equal(BarEdge.Bottom, Load("bar { profile \"default\" { edge \"Bottom\" } }").Config.Default.Edge);
        Assert.Equal(BarEdge.Top, Load("bar { profile \"default\" { edge \"top\" } }").Config.Default.Edge);
        Assert.DoesNotContain(Load("bar { profile \"default\" { edge \"BOTTOM\" } }").Diagnostics, d => d.Code == "TAJ0030");
    }

    [Fact]
    public void AnUnknownJustifyIsReportedAndTheWidgetsStartAtTheLeft()
    {
        (TajConfig config, IReadOnlyList<Diagnostic> diagnostics) = Load("""
            bar { profile "default" { zone "left" { justify "centre-ish"; text id="t" template="x" } } }
            """);

        Diagnostic warning = Assert.Single(diagnostics, d => d.Code == "TAJ0030");

        Assert.Contains("centre-ish", warning.Message, StringComparison.Ordinal);
        Assert.Equal(JustifyContent.Start, config.Default.Zones[0].Justify);
    }

    [Fact]
    public void EverySpellingOfJustifyIsAccepted()
    {
        foreach (string word in new[] { "start", "center", "centre", "end", "space-between", "space-around" })
        {
            (_, IReadOnlyList<Diagnostic> diagnostics) =
                Load($"bar {{ profile \"default\" {{ zone \"left\" {{ justify \"{word}\" }} }} }}");

            Assert.DoesNotContain(diagnostics, d => d.Code == "TAJ0030");
        }
    }

    // ---- colours (TAJ0031) ----

    [Fact]
    public void AColourThatDoesNotParseIsReportedWhereverItIs()
    {
        (_, IReadOnlyList<Diagnostic> diagnostics) = Load("""
            bar {
                profile "default" {
                    background "#12345"
                    zone "left" {
                        text id="t" template="x" colour="reddish" {
                            when value="1" colour="#gg0000"
                        }
                    }
                }
            }
            """);

        Diagnostic[] warnings = [.. diagnostics.Where(d => d.Code == "TAJ0031")];

        Assert.Equal(3, warnings.Length);
        Assert.All(warnings, w => Assert.Equal(DiagnosticSeverity.Warning, w.Severity));
        Assert.Contains(warnings, w => w.Message.Contains("#12345", StringComparison.Ordinal) && w.Message.Contains("profile", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Message.Contains("reddish", StringComparison.Ordinal) && w.Message.Contains("'t'", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Message.Contains("#gg0000", StringComparison.Ordinal) && w.Message.Contains("when", StringComparison.Ordinal));
    }

    [Fact]
    public void RealColoursAreNotReported()
    {
        (_, IReadOnlyList<Diagnostic> diagnostics) = Load("""
            bar {
                profile "default" {
                    background "#1e1e2e"
                    foreground "#cdd6f4cc"
                    border "accent"
                    zone "left" {
                        text id="t" template="x" colour="#fff" background="accent 40%" hover-background="#ffffff20"
                        workspaces id="w" active-background="accent" empty-colour="#888" focused-colour="#fff"
                    }
                }
            }
            """);

        Assert.DoesNotContain(diagnostics, d => d.Code == "TAJ0031");
    }

    // ---- culture (TAJ0032) ----

    [Fact]
    public void AnUnknownCultureIsReported()
    {
        Diagnostic warning = Warning("""
            bar {
                source "clock" kind="time" format="dddd" culture="German"
                profile "default" { height 30 }
            }
            """, "TAJ0032");

        Assert.Contains("German", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AKnownCultureReachesTheClock()
    {
        (TajConfig config, IReadOnlyList<Diagnostic> diagnostics) = Load("""
            bar {
                source "clock" kind="time" format="dddd" culture="de-DE"
                profile "default" { height 30 }
            }
            """);

        Assert.DoesNotContain(diagnostics, d => d.Code is "TAJ0032" or "TAJ0018");

        SourceSpec spec = Assert.Single(config.Sources, s => s.Name == "clock");
        Assert.Equal("de-DE", spec.Culture);

        var clock = Assert.IsType<ClockSource>(TajConfigLoader.CreateSources([spec]).Single());
        Assert.Equal("de-DE", clock.Culture.Name);
    }

    // ---- interval decides what a command source is ----

    [Fact]
    public void ACommandWithAnIntervalIsPolled()
    {
        (TajConfig config, _) = Load("""
            bar {
                source "cpu" kind="command" command="cmd.exe /c echo 1" interval=5000
                source "tail" kind="command" command="cmd.exe /c echo 1"
                profile "default" { height 30 }
            }
            """);

        SourceSpec polled = Assert.Single(config.Sources, s => s.Name == "cpu");
        SourceSpec resident = Assert.Single(config.Sources, s => s.Name == "tail");

        Assert.True(polled.IntervalWasWritten);
        Assert.False(resident.IntervalWasWritten);

        ISource[] sources = [.. TajConfigLoader.CreateSources([polled, resident])];

        Assert.True(Assert.IsType<ProcessSource>(sources[0]).Polls);
        Assert.False(Assert.IsType<ProcessSource>(sources[1]).Polls);

        foreach (ISource source in sources) source.Dispose();
    }

    // ---- min-width and max-width ----

    [Fact]
    public void MinAndMaxWidthReachTheWidgetsBox()
    {
        (TajConfig config, IReadOnlyList<Diagnostic> diagnostics) = Load("""
            bar {
                profile "default" {
                    zone "centre" {
                        text id="title" template="{{ window.title }}" min-width=80 max-width=400
                        icon id="i" source="x" min-width=24
                    }
                }
            }
            """);

        Assert.DoesNotContain(diagnostics, d => d.Code == "TAJ0016");

        IWidget title = config.Default.Zones[0].Widgets[0];
        VisualNode node = title.Build(new Dictionary<string, string?>(StringComparer.Ordinal) { ["window.title"] = "x" });

        Assert.Equal(80, node.Box.MinWidth);
        Assert.Equal(400, node.Box.MaxWidth);

        IWidget icon = config.Default.Zones[0].Widgets[1];
        Assert.Equal(24, icon.Build(new Dictionary<string, string?>(StringComparer.Ordinal)).Box.MinWidth);
    }

    [Fact]
    public void AMaxWidthOfNothingIsNoMaxWidth()
    {
        (TajConfig config, _) = Load("""
            bar { profile "default" { zone "centre" { text id="t" template="x" max-width=0 min-width=-5 } } }
            """);

        VisualNode node = config.Default.Zones[0].Widgets[0].Build(new Dictionary<string, string?>(StringComparer.Ordinal));

        Assert.Null(node.Box.MaxWidth);
        Assert.Equal(0, node.Box.MinWidth);
    }
}
