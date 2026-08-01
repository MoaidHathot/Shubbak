using Shubbak.Core.Diagnostics;

namespace Shubbak.Core.Tests;

/// <summary>
/// Tests for the logging subsystem.
/// </summary>
/// <remarks>
/// <see cref="Log"/> is static process-wide state, so these are serialised into one
/// xUnit collection. Running them in parallel would have them fighting over the
/// level and the ring buffer.
/// </remarks>
[Collection("Logging")]
public sealed class LogTests : IDisposable
{
    public LogTests()
    {
        Log.ResetForTests();
        Log.ToConsole = false;
    }

    public void Dispose() => Log.ResetForTests();

    [Fact]
    public void EntriesBelowTheLevelAreNotWrittenToTheSink()
    {
        Log.Level = LogLevel.Warning;

        string path = Path.Combine(Path.GetTempPath(), $"shubbak-log-{Guid.NewGuid():N}.log");

        try
        {
            Log.OpenFile(path);

            Log.Debug(LogCategory.Wm, "debug entry");
            Log.Info(LogCategory.Wm, "info entry");
            Log.Warn(LogCategory.Wm, "warning entry");
            Log.Error(LogCategory.Wm, "error entry");

            Log.CloseFile();

            string contents = File.ReadAllText(path);

            Assert.DoesNotContain("debug entry", contents, StringComparison.Ordinal);
            Assert.DoesNotContain("info entry", contents, StringComparison.Ordinal);
            Assert.Contains("warning entry", contents, StringComparison.Ordinal);
            Assert.Contains("error entry", contents, StringComparison.Ordinal);
        }
        finally
        {
            Log.CloseFile();
            File.Delete(path);
        }
    }

    [Fact]
    public void TheRingKeepsOneLevelDeeperThanTheSinkWrites()
    {
        // The point of the ring: `shubbak diagnose` can show what just happened even
        // though the user had not thought to raise the level in advance. Without
        // this, the first question on every bug report is "can you reproduce it with
        // logging on?", which for intermittent problems usually goes nowhere.
        Log.Level = LogLevel.Information;

        Log.Debug(LogCategory.Wm, "kept in the ring");
        Log.Info(LogCategory.Wm, "written everywhere");

        IReadOnlyList<LogEntry> recent = Log.RecentEntries();

        Assert.Contains(recent, e => e.Message == "kept in the ring");
        Assert.Contains(recent, e => e.Message == "written everywhere");
    }

    [Fact]
    public void NoneSuppressesEverythingIncludingTheRing()
    {
        Log.Level = LogLevel.None;

        Log.Error(LogCategory.Wm, "should not appear");

        Assert.Empty(Log.RecentEntries());
    }

    [Fact]
    public void IsEnabledMatchesWhatIsActuallyRecorded()
    {
        // Hot paths guard message construction on IsEnabled, so a mismatch would
        // either lose entries or allocate strings that are then discarded.
        Log.Level = LogLevel.Debug;

        Assert.True(Log.IsEnabled(LogLevel.Trace));
        Assert.True(Log.IsEnabled(LogLevel.Debug));

        Log.Trace(LogCategory.Hook, "traced");
        Assert.Contains(Log.RecentEntries(), e => e.Message == "traced");

        Log.Level = LogLevel.Error;

        Assert.False(Log.IsEnabled(LogLevel.Debug));
        Assert.True(Log.IsEnabled(LogLevel.Warning));
    }

    [Fact]
    public void TheRingWrapsAndKeepsTheNewestEntries()
    {
        Log.Level = LogLevel.Trace;

        for (int i = 0; i < 5000; i++) Log.Trace(LogCategory.Wm, $"entry {i}");

        IReadOnlyList<LogEntry> recent = Log.RecentEntries();

        Assert.True(recent.Count <= 2048);
        Assert.Equal("entry 4999", recent[^1].Message);

        // The seconds before a failure are what matter, so the oldest entries are
        // the ones to discard.
        Assert.DoesNotContain(recent, e => e.Message == "entry 0");
    }

    [Fact]
    public void RecentEntriesComeBackOldestFirst()
    {
        Log.Level = LogLevel.Trace;

        Log.Trace(LogCategory.Wm, "first");
        Log.Trace(LogCategory.Wm, "second");
        Log.Trace(LogCategory.Wm, "third");

        IReadOnlyList<LogEntry> recent = Log.RecentEntries();

        Assert.Equal(["first", "second", "third"], recent.Select(e => e.Message));
    }

    [Fact]
    public void RecentEntriesCanBeLimited()
    {
        Log.Level = LogLevel.Trace;

        for (int i = 0; i < 100; i++) Log.Trace(LogCategory.Wm, $"entry {i}");

        IReadOnlyList<LogEntry> recent = Log.RecentEntries(10);

        Assert.Equal(10, recent.Count);
        Assert.Equal("entry 99", recent[^1].Message);
        Assert.Equal("entry 90", recent[0].Message);
    }

    [Fact]
    public void ExceptionsRecordTypeMessageAndStack()
    {
        Log.Level = LogLevel.Debug;

        try
        {
            throw new InvalidOperationException("something broke");
        }
        catch (InvalidOperationException ex)
        {
            Log.Error(LogCategory.Wm, "while doing a thing", ex);
        }

        IReadOnlyList<LogEntry> recent = Log.RecentEntries();

        Assert.Contains(recent, e =>
            e.Message.Contains("while doing a thing", StringComparison.Ordinal) &&
            e.Message.Contains("InvalidOperationException", StringComparison.Ordinal) &&
            e.Message.Contains("something broke", StringComparison.Ordinal));

        // The stack goes in at Debug so an Error-level log stays readable.
        Assert.Contains(recent, e => e.Level == LogLevel.Debug);
    }

    [Fact]
    public void OpeningALogFileRotatesThePreviousOne()
    {
        // A trace session can produce tens of megabytes in minutes, so files must
        // not grow without bound - but the previous run is often exactly the one
        // being investigated, so it is kept rather than deleted.
        string path = Path.Combine(Path.GetTempPath(), $"shubbak-rotate-{Guid.NewGuid():N}.log");
        string rotated = path + ".1";

        try
        {
            Log.Level = LogLevel.Information;

            Log.OpenFile(path);
            Log.Info(LogCategory.Wm, "first session");
            Log.CloseFile();

            Log.OpenFile(path);
            Log.Info(LogCategory.Wm, "second session");
            Log.CloseFile();

            Assert.Contains("second session", File.ReadAllText(path), StringComparison.Ordinal);
            Assert.True(File.Exists(rotated));
            Assert.Contains("first session", File.ReadAllText(rotated), StringComparison.Ordinal);
        }
        finally
        {
            Log.CloseFile();
            File.Delete(path);
            File.Delete(rotated);
        }
    }

    [Fact]
    public void AppendingDoesNotRotate()
    {
        string path = Path.Combine(Path.GetTempPath(), $"shubbak-append-{Guid.NewGuid():N}.log");

        try
        {
            Log.Level = LogLevel.Information;

            Log.OpenFile(path);
            Log.Info(LogCategory.Wm, "first");
            Log.CloseFile();

            Log.OpenFile(path, append: true);
            Log.Info(LogCategory.Wm, "second");
            Log.CloseFile();

            string contents = File.ReadAllText(path);

            Assert.Contains("first", contents, StringComparison.Ordinal);
            Assert.Contains("second", contents, StringComparison.Ordinal);
        }
        finally
        {
            Log.CloseFile();
            File.Delete(path);
            File.Delete(path + ".1");
        }
    }

    [Fact]
    public void FormattedEntriesAreAlignedAndCarryEverythingNeeded()
    {
        var entry = new LogEntry(
            new DateTime(2026, 8, 1, 14, 30, 45, 123, DateTimeKind.Local),
            LogLevel.Warning,
            LogCategory.Window,
            "something happened");

        string formatted = entry.Format();

        Assert.Contains("14:30:45.123", formatted, StringComparison.Ordinal);
        Assert.Contains("WRN", formatted, StringComparison.Ordinal);
        Assert.Contains("Window", formatted, StringComparison.Ordinal);
        Assert.Contains("something happened", formatted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("trace", LogLevel.Trace)]
    [InlineData("TRACE", LogLevel.Trace)]
    [InlineData("verbose", LogLevel.Trace)]
    [InlineData("dbg", LogLevel.Debug)]
    [InlineData("info", LogLevel.Information)]
    [InlineData("information", LogLevel.Information)]
    [InlineData("warn", LogLevel.Warning)]
    [InlineData("err", LogLevel.Error)]
    [InlineData("off", LogLevel.None)]
    [InlineData("silent", LogLevel.None)]
    public void LevelNamesAndAbbreviationsParse(string text, LogLevel expected)
    {
        Assert.True(Log.TryParseLevel(text, out LogLevel level));
        Assert.Equal(expected, level);
    }

    [Fact]
    public void UnknownLevelNamesAreRejectedRatherThanGuessed()
    {
        Assert.False(Log.TryParseLevel("chatty", out LogLevel level));
        Assert.Equal(LogLevel.Information, level);
    }

    [Fact]
    public void LoggingIsSafeFromManyThreads()
    {
        // The hook thread, the daemon thread and IPC threads all log.
        Log.Level = LogLevel.Trace;

        Parallel.For(0, 16, worker =>
        {
            for (int i = 0; i < 200; i++) Log.Trace(LogCategory.Wm, $"w{worker} i{i}");
        });

        Assert.Equal(16 * 200, Log.TotalEntries);
        Assert.NotEmpty(Log.RecentEntries());
    }
}

/// <summary>
/// Serialises the logging tests, which share process-wide static state.
/// </summary>
/// <remarks>
/// CA1711 objects to the "Collection" suffix, but xUnit's collection-definition
/// convention is what it is, and renaming to satisfy the analyser would obscure the
/// type's purpose.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "xUnit collection definition naming convention.")]
[CollectionDefinition("Logging", DisableParallelization = true)]
public sealed class LoggingTestCollection;
