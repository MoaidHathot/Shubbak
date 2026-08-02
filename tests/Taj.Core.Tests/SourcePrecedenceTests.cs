using Shubbak.Config;
using Taj.Core;

namespace Taj.Core.Tests;

/// <summary>
/// Tests for how declared sources interact with the built-in ones.
/// </summary>
/// <remarks>
/// <c>clock</c> and <c>date</c> exist without being declared, so a config that
/// declares either is redefining rather than adding. Getting that precedence wrong is
/// invisible: the bar renders, nothing errors, and the setting simply has no effect.
/// It shipped that way - a config asking for a date got the built-in time-only
/// format, while a differently named clock in the same file worked perfectly, which
/// made it look like a formatting problem rather than a precedence one.
/// </remarks>
public sealed class SourcePrecedenceTests
{
    private static TajConfig LoadOk(string source)
    {
        (TajConfig config, IReadOnlyList<Diagnostic> diagnostics) = TajConfigLoader.Load(source);

        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);

        return config;
    }

    private static SourceSpec Find(TajConfig config, string name) =>
        Assert.Single(config.Sources, s =>
            string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void TheBuiltInClockIsAvailableWithoutBeingDeclared()
    {
        TajConfig config = LoadOk("""
            bar {
                profile "default" {
                    zone "right" { text template="{{ clock }}" }
                }
            }
            """);

        Assert.Equal("HH:mm", Find(config, "clock").Argument);
    }

    [Fact]
    public void DeclaringAClockReplacesTheBuiltInOne()
    {
        // The bug. The built-in was registered first and the model keeps the first
        // registration by name, so this format was silently discarded.
        TajConfig config = LoadOk("""
            bar {
                source "clock" kind="time" format="ddd d MMM HH:mm"

                profile "default" {
                    zone "right" { text template="{{ clock }}" }
                }
            }
            """);

        Assert.Equal("ddd d MMM HH:mm", Find(config, "clock").Argument);
    }

    [Fact]
    public void DeclaringADateReplacesTheBuiltInOne()
    {
        TajConfig config = LoadOk("""
            bar {
                source "date" kind="time" format="yyyy-MM-dd"

                profile "default" {
                    zone "right" { text template="{{ date }}" }
                }
            }
            """);

        Assert.Equal("yyyy-MM-dd", Find(config, "date").Argument);
    }

    [Fact]
    public void ReplacingOneBuiltInLeavesTheOtherAlone()
    {
        TajConfig config = LoadOk("""
            bar {
                source "clock" kind="time" format="HH:mm:ss"

                profile "default" {
                    zone "right" { text template="{{ clock }} {{ date }}" }
                }
            }
            """);

        Assert.Equal("HH:mm:ss", Find(config, "clock").Argument);
        Assert.Equal("ddd d MMM", Find(config, "date").Argument);
    }

    [Fact]
    public void ADeclaredTimezoneSurvives()
    {
        // The asymmetry that made the original bug so confusing: a source with no
        // built-in twin always worked, so the problem looked like formatting.
        TajConfig config = LoadOk("""
            bar {
                source "clock"   kind="time" format="ddd d MMM HH:mm"
                source "seattle" kind="time" format="ddd d MMM HH:mm" timezone="America/Los_Angeles"

                profile "default" {
                    zone "right" { text template="{{ seattle }} {{ clock }}" }
                }
            }
            """);

        Assert.Equal("ddd d MMM HH:mm", Find(config, "clock").Argument);
        Assert.Equal("America/Los_Angeles", Find(config, "seattle").TimeZone);
    }

    [Fact]
    public void ADuplicateDeclarationWarnsRatherThanSilentlyLosing()
    {
        (TajConfig config, IReadOnlyList<Diagnostic> diagnostics) = TajConfigLoader.Load("""
            bar {
                source "clock" kind="time" format="HH:mm:ss"
                source "clock" kind="time" format="yyyy"

                profile "default" {
                    zone "right" { text template="{{ clock }}" }
                }
            }
            """);

        Assert.Contains(diagnostics, d => d.Code == "TAJ0006");

        // First wins, and the warning says so.
        Assert.Equal("HH:mm:ss", Find(config, "clock").Argument);
    }
}
