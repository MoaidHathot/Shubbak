using Shubbak.Ipc;

namespace Shubbak.Cli.Tests;

/// <summary>
/// Printing an inspection report.
/// </summary>
/// <remarks>
/// <para>
/// The layout used to be decided in the window manager, which meant the palette - the
/// other client - recovered the fields by splitting the printed text at its column
/// padding. The daemon's whitespace was an interface for a different process and
/// nothing anywhere tested it.
/// </para>
/// <para>
/// It is decided here now, so it is tested here. People have this output pasted into
/// issues and sitting in their scrollback, which is the reason the format is held
/// still rather than tidied up.
/// </para>
/// </remarks>
public class WindowReportTextTests
{
    private static WindowReport Report(
        bool manageable = true,
        string verdict = "manageable",
        bool excludedByRule = false,
        string? path = @"C:\Program Files\msedge.exe",
        ManagedWindowReport? node = null,
        IReadOnlyList<RuleReport>? rules = null,
        IReadOnlyList<AppReport>? apps = null) =>
        new(0x3047A, "a window", "Chrome_WidgetWin_1", "msedge", path,
            10, 20, 800, 600, 0x16CF0000, 0x00040100, true, "None", false,
            manageable, verdict, "manageable", node is not null, excludedByRule,
            node, rules ?? [], apps ?? []);

    private static string[] Lines(WindowReport report, bool complete = true) =>
        WindowReportText.Format(report, complete)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .ToArray();

    private static string Line(WindowReport report, string label) =>
        Lines(report).Single(l => l.StartsWith(label, StringComparison.Ordinal));

    [Fact]
    public void TheAttributesKeepTheColumnsPeopleHavePastedIntoIssues()
    {
        Assert.Equal("handle       0x3047A", Line(Report(), "handle"));
        Assert.Equal("class        Chrome_WidgetWin_1", Line(Report(), "class"));

        // Formatted from the four numbers rather than carried as a string, so it has
        // to reproduce what Rect.ToString always produced.
        Assert.Equal("rect         (10,20 800x600)", Line(Report(), "rect"));

        // Eight digits, because the interesting part of a style is which bits are set
        // and a ragged column makes two of them impossible to compare by eye.
        Assert.Equal("style        0x16CF0000", Line(Report(), "style"));
        Assert.Equal("ex-style     0x00040100", Line(Report(), "ex-style"));
    }

    [Fact]
    public void AnUnreadablePathSaysWhyItIsUnreadable()
    {
        // Almost always elevation, and almost always the actual answer to the question
        // that brought somebody here. A blank would look like a bug in the report.
        Assert.Equal(
            "path         (unreadable - elevated process?)",
            Line(Report(path: null), "path"));
    }

    [Fact]
    public void TheVerdictCarriesTheReasonWithIt()
    {
        Assert.Equal(
            "manageable   no - window has no area",
            Line(Report(manageable: false, verdict: "window has no area"), "manageable"));
    }

    [Fact]
    public void ARuleIsTickedOrNotAndSaysWhereItLives()
    {
        WindowReport report = Report(rules:
        [
            new RuleReport("float the pip", 42, Matched: true),
            new RuleReport("browsers to 2", 51, Matched: false),
        ]);

        Assert.Contains("  [x] float the pip (line 42)", Lines(report), StringComparer.Ordinal);
        Assert.Contains("  [ ] browsers to 2 (line 51)", Lines(report), StringComparer.Ordinal);
    }

    [Fact]
    public void NoRulesAtAllIsSaidRatherThanLeftBlank()
    {
        // A heading with nothing under it reads as the report having given up.
        Assert.Contains("  (none configured)", Lines(Report()), StringComparer.Ordinal);
    }

    [Fact]
    public void AnAppThatMissedListsOnlyTheMatchersThatMissed()
    {
        WindowReport report = Report(apps:
        [
            new AppReport("browser", Matched: false, ["class ~= Chrome_WidgetWin_1"]),
            new AppReport("terminal", Matched: true, []),
        ]);

        string[] lines = Lines(report);

        Assert.Contains("  [ ] browser", lines, StringComparer.Ordinal);
        Assert.Contains("        failed: class ~= Chrome_WidgetWin_1", lines, StringComparer.Ordinal);

        // Restating the matchers of an app that matched would be reading the config
        // back at somebody who is asking why a different one did not fire.
        Assert.Contains("  [x] terminal", lines, StringComparer.Ordinal);
        Assert.Single(lines, l => l.Contains("failed:", StringComparison.Ordinal));
    }

    [Fact]
    public void AManagedWindowReportsWhatTheTreeKnows()
    {
        WindowReport report = Report(node: new ManagedWindowReport(
            7, "tiling", "3", Focused: true, Sticky: false, ["5", "9"], Scratchpad: null));

        Assert.Equal("managed      yes", Line(report, "managed"));
        Assert.Contains("  node       #7", Lines(report), StringComparer.Ordinal);
        Assert.Contains("  workspace  3", Lines(report), StringComparer.Ordinal);

        // Worded as a consequence. A window that relocates itself whenever a workspace
        // is activated reads as a fault, and "5, 9" alone does not say that is what is
        // about to happen.
        Assert.Contains(
            "  tags       5, 9 - it will follow you there",
            Lines(report),
            StringComparer.Ordinal);
    }

    [Fact]
    public void AWindowWithNoTagsSaysSoRatherThanShowingAnEmptyList()
    {
        WindowReport report = Report(node: new ManagedWindowReport(
            7, "tiling", "3", Focused: false, Sticky: false, [], Scratchpad: null));

        Assert.Contains("  tags       (none)", Lines(report), StringComparer.Ordinal);
    }

    [Fact]
    public void AStashedWindowNamesItsSlotAndAnUnstashedOneSaysNothing()
    {
        // Only when there is one. A blank scratchpad line on every managed window
        // would be a row of nothing on the overwhelmingly common case.
        WindowReport stashed = Report(node: new ManagedWindowReport(
            7, "tiling", "3", Focused: false, Sticky: false, [], "notes"));

        Assert.Contains("  scratchpad notes", Lines(stashed), StringComparer.Ordinal);

        WindowReport ordinary = Report(node: new ManagedWindowReport(
            7, "tiling", "3", Focused: false, Sticky: false, [], Scratchpad: null));

        Assert.DoesNotContain(Lines(ordinary), l => l.Contains("scratchpad", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnmanagedWindowSaysWhetherARuleIsWhy()
    {
        // Two answers with two different fixes: a rule is something the user wrote and
        // can unwrite, and everything else is not.
        Assert.Equal(
            "managed      no (excluded by a rule)",
            Line(Report(excludedByRule: true), "managed"));

        Assert.Equal("managed      no", Line(Report(), "managed"));
    }

    [Fact]
    public void AnIncompleteReportStopsAfterTheVerdict()
    {
        // The local path, with no window manager running. It cannot speak for the tree
        // or the configuration, and "managed no, rules (none configured)" there would
        // be a confident lie rather than a gap.
        string[] lines = Lines(Report(), complete: false);

        Assert.Equal("manageable   yes - manageable", lines[^1]);

        Assert.DoesNotContain(lines, l => l.StartsWith("managed", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.StartsWith("rules", StringComparison.Ordinal));
    }
}
