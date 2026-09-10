using Shubbak.Ipc;

namespace Shubbak.Cli.Tests;

/// <summary>
/// Printing why each context is the way it is.
/// </summary>
public sealed class ContextReportTextTests
{
    private static ContextReport Presenting(bool active) => new(
        "presenting",
        active,
        "detected",
        External: false,
        active ? "window app=\"slides\"" : "no block of conditions holds",
        Pin: null,
        SetBy: null,
        SetAgoMs: null,
        ExpiresInMs: null,
        Leased: false,
        LingerRemainingMs: null,
        When:
        [
            new WhenReport(active, [new ConditionReport("window app=\"slides\"", active, active ? "1 window(s)" : "no such window")]),
            new WhenReport(false,
            [
                new ConditionReport("system-state \"presenting\"", false, "the shell says ordinary"),
                new ConditionReport("monitors count=2", true, "2 attached"),
            ]),
        ],
        Effects: ["gaps", "borders", "1 binding(s)", "on-enter"]);

    [Fact]
    public void EveryConditionIsPrintedWithAMarkAndWhatItSaw()
    {
        string text = ContextReportText.Format([Presenting(active: false)]);

        Assert.Contains("  presenting  inactive  (detected: no block of conditions holds)", text, StringComparison.Ordinal);
        Assert.Contains("when does not hold (1 of 2)", text, StringComparison.Ordinal);
        Assert.Contains("[ ] window app=\"slides\"  - no such window", text, StringComparison.Ordinal);
        Assert.Contains("when does not hold (2 of 2)", text, StringComparison.Ordinal);
        Assert.Contains("[ ] system-state \"presenting\"  - the shell says ordinary", text, StringComparison.Ordinal);
        Assert.Contains("[x] monitors count=2  - 2 attached", text, StringComparison.Ordinal);
        Assert.Contains("changes: gaps, borders, 1 binding(s), on-enter", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnActiveContextIsMarkedAtTheMargin()
    {
        string text = ContextReportText.Format([Presenting(active: true)]);

        Assert.StartsWith("* presenting  active  (detected: window app=\"slides\")", text, StringComparison.Ordinal);
        Assert.Contains("when holds (1 of 2)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void APinnedContextSaysWhoAndForHowLong()
    {
        var meeting = new ContextReport(
            "meeting", true, "pinned", External: true, "set by ayn.exe (pid 7)",
            Pin: "set", SetBy: "ayn.exe (pid 7)", SetAgoMs: 1200, ExpiresInMs: 3800, Leased: true,
            LingerRemainingMs: null, When: [], Effects: []);

        string text = ContextReportText.Format([meeting]);

        Assert.Contains("* meeting  active, external  (pinned: set by ayn.exe (pid 7))", text, StringComparison.Ordinal);
        Assert.Contains("pinned set by ayn.exe (pid 7) 1.2 s ago, expires in 3.8 s, leased to that connection", text, StringComparison.Ordinal);

        // External and effect-free is a flag by design, so no "changes nothing" nag.
        Assert.DoesNotContain("changes nothing", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ALingeringContextSaysWhenItLetsGo()
    {
        ContextReport lingering = Presenting(active: true) with { LingerRemainingMs = 190 };

        Assert.Contains("lingering, lets go in 190 ms", ContextReportText.Format([lingering]), StringComparison.Ordinal);
    }

    [Fact]
    public void ADetectedContextWithNoEffectsIsCalledAFlag()
    {
        ContextReport flag = Presenting(active: false) with { Effects = [] };

        Assert.Contains("changes nothing; a flag", ContextReportText.Format([flag]), StringComparison.Ordinal);
    }

    [Fact]
    public void NoContextsIsSaidWithAHint()
    {
        string text = ContextReportText.Format([]);

        Assert.Contains("no contexts declared", text, StringComparison.Ordinal);
        Assert.Contains("contexts { context", text, StringComparison.Ordinal);
    }
}
