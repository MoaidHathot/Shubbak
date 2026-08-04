using Taj.Core;

namespace Taj.Core.Tests;

/// <summary>
/// When a bar stops waiting for a window manager that has gone.
/// </summary>
/// <remarks>
/// <para>
/// The bar used to retry for ever. That is right while it is starting - it is
/// normally launched by the window manager's own startup command and can win the
/// race, so giving up during that race would mean the bar simply never appeared.
/// </para>
/// <para>
/// It is wrong an hour later. Killing the window manager left a bar attached to
/// nothing, redrawing a stale world once a second, with no way to close it but Task
/// Manager - and killing it that way skips the appbar being unregistered, so the
/// shell can be left holding a strip of screen for a bar that no longer exists.
/// </para>
/// </remarks>
public sealed class WindowManagerTimeoutTests
{
    private static readonly TimeSpan Thirty = TimeSpan.FromSeconds(30);

    /// <summary>A timestamp the given number of seconds after another.</summary>
    private static long Later(long from, double seconds) =>
        from + (long)(seconds * System.Diagnostics.Stopwatch.Frequency);

    [Fact]
    public void ABarThatHasNeverConnectedWaitsForEver()
    {
        // The startup race. The window manager launches the bar, so for a moment
        // there is no server to connect to - and a bar that gave up then would never
        // appear at all.
        long start = System.Diagnostics.Stopwatch.GetTimestamp();

        Assert.False(ReconnectPolicy.ShouldGiveUp(
            everConnected: false, lostAtTicks: 0, now: Later(start, 3600), timeout: Thirty));
    }

    [Fact]
    public void AConnectedBarKeepsWaitingWhileTheWindowManagerIsThere()
    {
        // lostAtTicks of zero means "not currently lost".
        long start = System.Diagnostics.Stopwatch.GetTimestamp();

        Assert.False(ReconnectPolicy.ShouldGiveUp(
            everConnected: true, lostAtTicks: 0, now: Later(start, 3600), timeout: Thirty));
    }

    [Fact]
    public void ABarGivesUpOnceTheTimeoutHasPassed()
    {
        long lost = System.Diagnostics.Stopwatch.GetTimestamp();

        Assert.True(ReconnectPolicy.ShouldGiveUp(
            everConnected: true, lostAtTicks: lost, now: Later(lost, 31), timeout: Thirty));
    }

    [Fact]
    public void ABarWaitsOutTheWholeTimeoutFirst()
    {
        // A window manager being restarted is gone for a second or two. Giving up
        // then would cost the bar every time the daemon reloaded.
        long lost = System.Diagnostics.Stopwatch.GetTimestamp();

        Assert.False(ReconnectPolicy.ShouldGiveUp(
            everConnected: true, lostAtTicks: lost, now: Later(lost, 1), timeout: Thirty));

        Assert.False(ReconnectPolicy.ShouldGiveUp(
            everConnected: true, lostAtTicks: lost, now: Later(lost, 29), timeout: Thirty));
    }

    [Fact]
    public void TheBoundaryIsInclusive()
    {
        long lost = System.Diagnostics.Stopwatch.GetTimestamp();

        Assert.True(ReconnectPolicy.ShouldGiveUp(
            everConnected: true, lostAtTicks: lost, now: Later(lost, 30), timeout: Thirty));
    }

    [Fact]
    public void NoTimeoutMeansNeverGiveUp()
    {
        // window-manager-timeout 0. Someone who wants the bar to outlast anything is
        // entitled to say so.
        long lost = System.Diagnostics.Stopwatch.GetTimestamp();

        Assert.False(ReconnectPolicy.ShouldGiveUp(
            everConnected: true, lostAtTicks: lost, now: Later(lost, 86400), timeout: null));
    }

    [Fact]
    public void AShortTimeoutIsHonouredExactly()
    {
        long lost = System.Diagnostics.Stopwatch.GetTimestamp();
        TimeSpan five = TimeSpan.FromSeconds(5);

        Assert.False(ReconnectPolicy.ShouldGiveUp(true, lost, Later(lost, 4), five));
        Assert.True(ReconnectPolicy.ShouldGiveUp(true, lost, Later(lost, 6), five));
    }

    // ---- the setting -------------------------------------------------------

    private static TajConfig Load(string source)
    {
        (TajConfig config, IReadOnlyList<Shubbak.Config.Diagnostic> diagnostics) =
            TajConfigLoader.Load(source);

        Assert.DoesNotContain(
            diagnostics,
            d => d.Severity == Shubbak.Config.DiagnosticSeverity.Error);

        return config;
    }

    [Fact]
    public void TheSettingDefaultsToThirtySeconds()
    {
        Assert.Equal(Thirty, Load("bar { profile \"default\" { height 30 } }").WindowManagerTimeout);
        Assert.Equal(Thirty, TajConfigLoader.CreateDefault().WindowManagerTimeout);
    }

    [Fact]
    public void TheSettingIsReadInSeconds()
    {
        TajConfig config = Load("""
            bar {
                window-manager-timeout 5
                profile "default" { height 30 }
            }
            """);

        Assert.Equal(TimeSpan.FromSeconds(5), config.WindowManagerTimeout);
    }

    [Fact]
    public void ZeroMeansWaitForEver()
    {
        // Not "give up at once". A bar that vanished the instant the window manager
        // hiccupped would be worse than one that lingers, and nobody writes 0 meaning
        // that.
        TajConfig config = Load("""
            bar {
                window-manager-timeout 0
                profile "default" { height 30 }
            }
            """);

        Assert.Null(config.WindowManagerTimeout);
    }

    [Theory]
    [InlineData("window-manager-timeout -5")]
    [InlineData("window-manager-timeout \"soon\"")]
    [InlineData("window-manager-timeout")]
    public void NonsenseIsReportedAndFallsBackToTheDefault(string setting)
    {
        (TajConfig config, IReadOnlyList<Shubbak.Config.Diagnostic> diagnostics) =
            TajConfigLoader.Load($$"""
                bar {
                    {{setting}}
                    profile "default" { height 30 }
                }
                """);

        Assert.Contains(diagnostics, d => d.Code is "TAJ0011" or "TAJ0012");
        Assert.Equal(Thirty, config.WindowManagerTimeout);
    }
}
