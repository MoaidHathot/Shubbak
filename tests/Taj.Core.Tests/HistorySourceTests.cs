using Shubbak.Config;
using Taj.Core.Sources;

namespace Taj.Core.Tests;

/// <summary>
/// Keeping the last so many readings of a source, for a sparkline to draw.
/// </summary>
/// <remarks>
/// The history lives beside the source rather than in the widget, because a widget
/// sees a snapshot of values and cannot tell a new reading from the old one - and a
/// source drops a reading equal to its predecessor on purpose. The tests here are
/// about the two things that follow: a repeat is a reading, and a full buffer of
/// repeats is still not a repaint.
/// </remarks>
public sealed class HistorySourceTests
{
    [Fact]
    public void ARepeatedReadingIsARepeatedReading()
    {
        // A CPU at a steady fifty is a flat line, not a line that has stopped.
        using var cpu = new PushSource("cpu");
        using var history = new HistorySource(cpu, 5);

        cpu.Set("50");
        cpu.Set("50");
        cpu.Set("50");

        Assert.Equal([50, 50, 50], history.Readings);
        Assert.Equal("50 50 50", history.Value);
    }

    [Fact]
    public void TheOldestReadingFallsOffTheFront()
    {
        using var cpu = new PushSource("cpu");
        using var history = new HistorySource(cpu, 3);

        foreach (string reading in new[] { "1", "2", "3", "4", "5" }) cpu.Set(reading);

        Assert.Equal([3, 4, 5], history.Readings);
        Assert.Equal("3 4 5", history.Value);
        Assert.Equal(3, history.Capacity);
    }

    [Fact]
    public void AReadingWithNoNumberIsNotARecording()
    {
        // A heading, a failure marker, an empty line: none of them measured anything.
        using var cpu = new PushSource("cpu");
        using var history = new HistorySource(cpu, 5);

        cpu.Set("cpu usage");
        cpu.Set("12%");
        cpu.Set("!");
        cpu.Set("");
        cpu.Set("15.5%");

        Assert.Equal([12, 15.5], history.Readings);
        Assert.Equal("12 15.5", history.Value);
    }

    [Fact]
    public void AFullBufferOfTheSameReadingDoesNotAnnounceAChange()
    {
        // Nothing on screen would move, so nothing should repaint - the same equality
        // check every source has, doing the same job.
        using var cpu = new PushSource("cpu");
        using var history = new HistorySource(cpu, 3);

        int changes = 0;
        history.Changed += _ => changes++;

        cpu.Set("50");
        cpu.Set("50");
        cpu.Set("50");
        Assert.Equal(3, changes);

        cpu.Set("50");
        cpu.Set("50");
        Assert.Equal(3, changes);

        cpu.Set("51");
        Assert.Equal(4, changes);
    }

    [Fact]
    public void ItListensFromBirthSoTheFirstReadingIsKept()
    {
        // The hub starts the inner source before the history, and an interval source
        // reads at once when started.
        using var ticks = new IntervalSource("t", TimeSpan.FromHours(1), () => "7");
        using var history = new HistorySource(ticks, 4);

        ticks.Start();
        history.Start();

        Assert.Equal([7], history.Readings);
    }

    [Fact]
    public void DisposingStopsListening()
    {
        using var cpu = new PushSource("cpu");
        var history = new HistorySource(cpu, 3);

        cpu.Set("1");
        history.Dispose();
        cpu.Set("2");

        Assert.Equal([1], history.Readings);
    }

    [Fact]
    public void TheNameIsTheSourceWithASuffix()
    {
        using var cpu = new PushSource("cpu");
        using var history = new HistorySource(cpu, 3);

        Assert.Equal("cpu.history", history.Name);
        Assert.Equal("cpu.history", HistorySource.NameFor("cpu"));
    }

    [Fact]
    public void FewerThanTwoReadingsIsNotAHistory()
    {
        using var cpu = new PushSource("cpu");

        Assert.Throws<ArgumentOutOfRangeException>(() => new HistorySource(cpu, 1));
    }

    [Fact]
    public void ASampleAllocatesOnlyItsValue()
    {
        // A sample is a source tick, and a one-second source ticks for as long as the
        // bar is up. The value has to be a string - that is what a source carries - and
        // that string is the one thing a sample may allocate: the readings are written
        // into it straight from the ring, through a builder that is kept.
        using var cpu = new PushSource("cpu");
        using var history = new HistorySource(cpu, 60);

        string[] readings = [.. Enumerable.Range(0, 100).Select(i => (i * 7 % 100).ToString(System.Globalization.CultureInfo.InvariantCulture))];

        for (int i = 0; i < 300; i++) cpu.Set(readings[i % readings.Length]);

        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 100; i++) cpu.Set(readings[i % readings.Length]);

        long perSample = (GC.GetAllocatedBytesForCurrentThread() - before) / 100;

        // Sixty readings of up to two digits and a space each is under two hundred
        // characters; the string that holds them, with its header, is under 512 bytes.
        // The snapshot array and sixty strings it used to make on top were five times
        // that.
        Assert.InRange(perSample, 1, 512);
        Assert.Equal(60, history.Readings.Count);
    }

    // ---- through the loader and the hub ------------------------------------------

    [Fact]
    public void HistoryOnASourceMakesASecondSource()
    {
        (TajConfig config, IReadOnlyList<Diagnostic> diagnostics) = TajConfigLoader.Load("""
            bar { source "cpu" kind="signal" history=30 }
            """);

        Assert.Empty(diagnostics);

        SourceSpec spec = Assert.Single(config.Sources, s => s.Name == "cpu");
        Assert.Equal(30, spec.History);

        List<ISource> sources = [.. TajConfigLoader.CreateSources(config.Sources)];

        try
        {
            Assert.Contains(sources, s => s.Name == "cpu");
            var history = Assert.IsType<HistorySource>(Assert.Single(sources, s => s.Name == "cpu.history"));
            Assert.Equal(30, history.Capacity);
        }
        finally
        {
            foreach (ISource source in sources) source.Dispose();
        }
    }

    [Fact]
    public void TheHistoryReachesEveryBarThroughTheHub()
    {
        (TajConfig config, _) = TajConfigLoader.Load("""
            bar {
                source "cpu" kind="signal" history=3
                profile "default" {
                    zone "right" { text template="{{ cpu }} / {{ cpu.history }}" }
                }
            }
            """);

        using var hub = new SourceHub();
        using var model = new BarModel(config.Default);

        hub.Replace(TajConfigLoader.CreateSources(config.Sources));
        hub.Attach(model);

        hub.Signal("cpu", ["10"]);
        hub.Signal("cpu", ["10"]);
        hub.Signal("cpu", ["30"]);

        Assert.Equal("30", model.GetValue("cpu"));
        Assert.Equal("10 10 30", model.GetValue("cpu.history"));
    }

    [Fact]
    public void AHistoryThatIsNotACountIsReportedAndNoneIsKept()
    {
        (TajConfig config, IReadOnlyList<Diagnostic> diagnostics) = TajConfigLoader.Load("""
            bar {
                source "a" kind="signal" history="lots"
                source "b" kind="signal" history=1
                source "c" kind="signal" history=5000
            }
            """);

        Assert.Equal(3, diagnostics.Count(d => d.Code == "TAJ0036"));
        Assert.All(config.Sources.Where(s => s.Name is "a" or "b" or "c"), s => Assert.Null(s.History));
    }

    [Fact]
    public void HistoryIsNotAnUnknownSetting()
    {
        (_, IReadOnlyList<Diagnostic> diagnostics) = TajConfigLoader.Load("""
            bar { source "cpu" kind="command" command="x" interval=2000 history=60 }
            """);

        Assert.DoesNotContain(diagnostics, d => d.Code == "TAJ0018");
    }
}
