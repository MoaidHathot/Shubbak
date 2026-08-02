using Shubbak.Core.Diagnostics;

namespace Shubbak.Core.Tests;

/// <summary>
/// Tests that a disabled log call costs nothing.
/// </summary>
/// <remarks>
/// <para>
/// <c>Log.Debug(category, $"...")</c> reads as free when debug logging is off, and was
/// not: the caller built the string before the call, so the formatting and the
/// allocation happened regardless. On the window manager's tick that was a string per
/// keystroke and per window event, permanently.
/// </para>
/// <para>
/// The waste was the smaller half. Allocating on the message loop means collecting on
/// the message loop, and a collection suspends every thread - including the one
/// holding a keystroke the user is waiting on.
/// </para>
/// </remarks>
[Collection("Logging")]
public sealed class LogAllocationTests : IDisposable
{
    public LogAllocationTests()
    {
        Log.ResetForTests();
        Log.ToConsole = false;
    }

    public void Dispose() => Log.ResetForTests();

    /// <summary>Records whether it was asked to render itself.</summary>
    private sealed class Tattletale
    {
        public int Renders { get; private set; }

        public override string ToString()
        {
            Renders++;
            return "rendered";
        }
    }

    [Fact]
    public void ADisabledDebugCallFormatsNothing()
    {
        // Warning, not Information. The ring records one level below the sink so
        // `shubbak diagnose` can explain what just happened without the user having
        // enabled logging beforehand - so at Information, Debug messages are still
        // built, for the ring. Skipping them would trade a real diagnostic ability
        // for an allocation, which is the wrong way round.
        Log.Level = LogLevel.Warning;

        var value = new Tattletale();

        Log.Debug(LogCategory.Hook, $"a keystroke: {value}");

        Assert.Equal(0, value.Renders);
    }

    [Fact]
    public void ADisabledTraceCallFormatsNothing()
    {
        // Trace is two levels below Information, so it is outside the ring as well -
        // which is what makes this the case that matters. Trace is where the
        // per-keystroke and per-window-event messages live.
        Log.Level = LogLevel.Information;

        var value = new Tattletale();

        Log.Trace(LogCategory.Window, $"an event: {value}");

        Assert.Equal(0, value.Renders);
    }

    [Fact]
    public void ADebugCallIsStillBuiltForTheRingAtInformation()
    {
        // Deliberate, and worth pinning: the cost buys the diagnostic report its
        // history. Anyone tempted to "optimise" this away should fail here first.
        Log.Level = LogLevel.Information;

        var value = new Tattletale();

        Log.Debug(LogCategory.Hook, $"a keystroke: {value}");

        Assert.Equal(1, value.Renders);

        Assert.Contains(
            Log.RecentEntries(),
            entry => entry.Message.Contains("a keystroke: rendered", StringComparison.Ordinal));
    }

    [Fact]
    public void AnEnabledDebugCallStillFormats()
    {
        // The other half of the bargain: skipping work must not mean losing messages.
        Log.Level = LogLevel.Debug;

        var value = new Tattletale();

        Log.Debug(LogCategory.Hook, $"a keystroke: {value}");

        Assert.Equal(1, value.Renders);

        Assert.Contains(
            Log.RecentEntries(),
            entry => entry.Message.Contains("a keystroke: rendered", StringComparison.Ordinal));
    }

    [Fact]
    public void AnEnabledTraceCallStillFormats()
    {
        Log.Level = LogLevel.Trace;

        var value = new Tattletale();

        Log.Trace(LogCategory.Window, $"an event: {value}");

        Assert.Equal(1, value.Renders);
    }

    [Fact]
    public void RaisingTheLevelStopsTheFormatting()
    {
        // Level changes at runtime through `shubbak log-level`, so the decision has to
        // be made per call rather than once at startup.
        var value = new Tattletale();

        Log.Level = LogLevel.Debug;
        Log.Debug(LogCategory.Hook, $"{value}");
        Assert.Equal(1, value.Renders);

        Log.Level = LogLevel.Error;
        Log.Debug(LogCategory.Hook, $"{value}");
        Assert.Equal(1, value.Renders);
    }

    [Fact]
    public void FormattingIsSkippedOnceTheRingHasStoppedCaringToo()
    {
        // The ring is what keeps Debug alive at Information. Raise the level past it
        // and the message is genuinely free.
        Log.Level = LogLevel.Error;

        var value = new Tattletale();

        Log.Debug(LogCategory.Hook, $"{value}");
        Log.Trace(LogCategory.Window, $"{value}");

        Assert.Equal(0, value.Renders);
    }
}
