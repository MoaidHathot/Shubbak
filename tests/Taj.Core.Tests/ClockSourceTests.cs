using Taj.Core.Sources;

namespace Taj.Core.Tests;

/// <summary>Tests for the clock source.</summary>
public sealed class ClockSourceTests
{
    /// <summary>Waits for a source to produce a value.</summary>
    private static string? WaitForValue(ClockSource source, TimeSpan timeout)
    {
        using var produced = new ManualResetEventSlim();

        void OnChanged(ISource _) => produced.Set();

        source.Changed += OnChanged;

        try
        {
            source.Start();

            // Start publishes synchronously, so the value is usually there already.
            if (source.Value is not null) return source.Value;

            produced.Wait(timeout);
            return source.Value;
        }
        finally
        {
            source.Changed -= OnChanged;
        }
    }

    [Fact]
    public void ProducesAValueImmediately()
    {
        // A bar that shows nothing for the first second looks broken.
        using var clock = new ClockSource("clock", "HH:mm", TimeSpan.FromSeconds(1));

        Assert.NotNull(WaitForValue(clock, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void UsesTheGivenFormat()
    {
        using var clock = new ClockSource("clock", "yyyy", TimeSpan.FromSeconds(1));

        string? value = WaitForValue(clock, TimeSpan.FromSeconds(2));

        Assert.Equal(DateTime.Now.Year.ToString(System.Globalization.CultureInfo.InvariantCulture), value);
    }

    [Fact]
    public void RendersAnotherTimezone()
    {
        // The second clock on a bar - a colleague's or a datacentre's local time - is
        // one of the most common things anyone puts there.
        using var local = new ClockSource("local", "HH", TimeSpan.FromSeconds(1));
        using var pacific = new ClockSource(
            "pacific", "HH", TimeSpan.FromSeconds(1), "Pacific Standard Time");

        string? localValue = WaitForValue(local, TimeSpan.FromSeconds(2));
        string? pacificValue = WaitForValue(pacific, TimeSpan.FromSeconds(2));

        Assert.NotNull(localValue);
        Assert.NotNull(pacificValue);

        DateTimeOffset expected = TimeZoneInfo.ConvertTime(
            DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"));

        Assert.Equal(expected.ToString("HH", System.Globalization.CultureInfo.InvariantCulture), pacificValue);
    }

    [Fact]
    public void AcceptsAnIanaIdentifier()
    {
        // People copy identifiers from wherever they find them, and
        // "America/Los_Angeles" failing while "Pacific Standard Time" works is an
        // unhelpful distinction to impose.
        //
        // Asserted against the actual converted time rather than merely "not null",
        // because falling back to local time also produces a value - which is exactly
        // how an earlier version of this passed while the feature was broken by
        // InvariantGlobalization being enabled.
        using var clock = new ClockSource(
            "pacific", "HH:mm", TimeSpan.FromSeconds(1), "America/Los_Angeles");

        string? value = WaitForValue(clock, TimeSpan.FromSeconds(2));

        DateTimeOffset expected = TimeZoneInfo.ConvertTime(
            DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles"));

        Assert.Equal(
            expected.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
            value);
    }

    [Fact]
    public void AForeignTimezoneActuallyDiffersFromLocal()
    {
        // The guard that a silent fallback cannot satisfy. Skipped in the unlikely
        // event the machine really is on Pacific time.
        TimeZoneInfo pacific = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");

        if (TimeZoneInfo.Local.BaseUtcOffset == pacific.BaseUtcOffset) return;

        using var local = new ClockSource("local", "zzz", TimeSpan.FromSeconds(1));
        using var remote = new ClockSource(
            "remote", "zzz", TimeSpan.FromSeconds(1), "America/Los_Angeles");

        string? localValue = WaitForValue(local, TimeSpan.FromSeconds(2));
        string? remoteValue = WaitForValue(remote, TimeSpan.FromSeconds(2));

        Assert.NotEqual(localValue, remoteValue);
    }

    [Fact]
    public void AnUnknownTimezoneFallsBackToLocalTime()
    {
        // Better a clock showing the wrong zone than a blank space the user has to
        // investigate.
        using var clock = new ClockSource(
            "broken", "HH", TimeSpan.FromSeconds(1), "Middle-earth/Shire");

        string? value = WaitForValue(clock, TimeSpan.FromSeconds(2));

        Assert.Equal(DateTime.Now.ToString("HH", System.Globalization.CultureInfo.InvariantCulture), value);
    }

    [Fact]
    public void AnEmptyFormatFallsBackToATime()
    {
        using var clock = new ClockSource("clock", "", TimeSpan.FromSeconds(1));

        string? value = WaitForValue(clock, TimeSpan.FromSeconds(2));

        Assert.NotNull(value);
        Assert.Contains(":", value, StringComparison.Ordinal);
    }

    [Fact]
    public void UnchangedValuesDoNotFire()
    {
        // The interval is how often the value is checked, not how often the bar
        // redraws. A clock showing minutes polled every 100ms must not cause ten
        // redraws a second.
        using var clock = new ClockSource("clock", "yyyy", TimeSpan.FromMilliseconds(100));

        int changes = 0;
        clock.Changed += _ => Interlocked.Increment(ref changes);

        clock.Start();
        Thread.Sleep(600);

        Assert.Equal(1, Volatile.Read(ref changes));
    }
}
