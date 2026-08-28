using Dalil.Core;
using Shubbak.Ipc;

namespace Dalil.Core.Tests;

/// <summary>
/// Turning what the window manager reports into rows a person can read.
/// </summary>
/// <remarks>
/// Judgements rather than plumbing, which is why they are tested apart from the
/// model: what a window with no title is called, which states earn a badge and which
/// are ordinary, and what pressing Enter on a row actually does.
/// </remarks>
public sealed class PaletteEntriesTests
{
    private static WindowCandidate Window(
        long handle = 0x100,
        string title = "a window",
        string className = "TestClass",
        string process = "test",
        bool managed = true,
        string? reason = null,
        string? state = "tiling",
        string concealment = "none",
        string? workspace = "1",
        bool sticky = false,
        bool elevated = false,
        long focusSequence = 0,
        string? summary = null) =>
        new(handle, title, className, process, 42, elevated, managed, reason, state,
            concealment, workspace, true, "\\\\.\\DISPLAY1", sticky, false, focusSequence,
            Scratchpad: null, Tags: null, ExclusionSummary: summary);

    [Fact]
    public void AnUnmanagedWindowSaysWhyOnTheRowItself()
    {
        // The window manager has always sent this and the palette threw it away, so
        // the list could say a window was unmanaged and never why - leaving the reason
        // three keystrokes down an action list nobody knew was there.
        PaletteEntry entry = Assert.Single(PaletteEntries.ForWindows(
        [
            Window(managed: false, workspace: null, summary: "not an Alt+Tab target"),
        ]));

        Assert.Contains("not an Alt+Tab target", entry.Secondary, StringComparison.Ordinal);

        // Beside the process, not instead of it. Which application it belongs to is
        // still how somebody recognises the row.
        Assert.Contains("test", entry.Secondary, StringComparison.Ordinal);
    }

    [Fact]
    public void AManagedWindowSaysWhereItIsRatherThanWhyItIsNotManaged()
    {
        PaletteEntry entry = Assert.Single(PaletteEntries.ForWindows([Window(workspace: "3")]));

        Assert.Contains("3", entry.Secondary, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLongReasonIsUsedWhenThereIsNoShortOne()
    {
        // What an older window manager sends. A clipped sentence still says more than
        // nothing, and it is searchable in full however much of it a row can draw.
        PaletteEntry entry = Assert.Single(PaletteEntries.ForWindows(
        [
            Window(managed: false, workspace: null, reason: "window has no area"),
        ]));

        Assert.Contains("window has no area", entry.Secondary, StringComparison.Ordinal);
    }

    [Fact]
    public void SkippedShowsOnlyTheWindowsThatWereSkipped()
    {
        // The whole reason it earns a prefix of its own. The window list already shows
        // everything; this is the "what is being passed over" view.
        IReadOnlyList<PaletteEntry> entries = PaletteEntries.ForSkipped(
        [
            Window(handle: 1, title: "managed one"),
            Window(handle: 2, title: "skipped one", managed: false, workspace: null,
                summary: "not an Alt+Tab target"),
        ]);

        Assert.Equal("skipped one", Assert.Single(entries).Primary);
    }

    [Fact]
    public void SkippedOffersTheReportRatherThanOnlyTheWindow()
    {
        // Enter in this mode asks why, because going to a window you have just been
        // told is not managed answers nothing.
        PaletteEntry entry = Assert.Single(PaletteEntries.ForSkipped(
        [
            Window(handle: 0x2A, managed: false, workspace: null, summary: "no title"),
        ]));

        Assert.Equal(0x2A, entry.Explains);
    }

    [Fact]
    public void SkippedPutsTheAnswerableOnesFirst()
    {
        // A rule is something the user wrote and can unwrite; a child window with no
        // area is a fact about Win32. Recency would sort these almost equally, because
        // most of them have never been focused.
        IReadOnlyList<PaletteEntry> entries = PaletteEntries.ForSkipped(
        [
            Window(handle: 1, title: "win32 says no", managed: false, workspace: null,
                summary: "a child window"),
            Window(handle: 2, title: "your rule says no", managed: false, workspace: null,
                summary: "excluded by a rule"),
            Window(handle: 3, title: "not yet", managed: false, workspace: null,
                summary: "not adopted yet"),
        ]);

        Assert.Equal(
            ["your rule says no", "not yet", "win32 says no"],
            entries.OrderByDescending(e => e.Rank).Select(e => e.Primary));
    }

    [Fact]
    public void EveryWindowStillCarriesItsActionsWhenSkipped()
    {
        // Ctrl+Enter has to reach "Manage it" from here. That is the fix for most of
        // what this mode shows, and a list that diagnoses without offering the remedy
        // is half a feature.
        PaletteEntry entry = Assert.Single(PaletteEntries.ForSkipped(
        [
            Window(managed: false, workspace: null, summary: "not adopted yet"),
        ]));

        Assert.NotNull(entry.Actions);
        Assert.Contains(entry.Actions!, a => a.Name == "Manage it");
    }

    [Fact]
    public void EnterOnAWindowFocusesItByHandle()
    {
        PaletteEntry entry = Assert.Single(PaletteEntries.ForWindows([Window(handle: 0x1D0076)]));

        // One command covers both cases. For a managed window the daemon switches
        // workspace and raises it; for one the tree has never heard of it falls
        // through to revealing - uncloaking, restoring and foregrounding.
        Assert.Equal("focus-window 1900662", entry.Command);
    }

    [Fact]
    public void RecencyBecomesTheRowsRank()
    {
        PaletteEntry entry = Assert.Single(PaletteEntries.ForWindows([Window(focusSequence: 17)]));

        Assert.Equal(17, entry.Rank);
    }

    [Fact]
    public void AWindowWithNoTitleIsNamedByItsClass()
    {
        PaletteEntry entry = Assert.Single(
            PaletteEntries.ForWindows([Window(title: "", className: "Ghost")]));

        // An untitled window may be exactly the one that has gone wrong. A blank row
        // would be unsearchable and unselectable, which is the opposite of useful.
        Assert.Equal("(Ghost)", entry.Primary);
    }

    [Fact]
    public void TheProcessAndWorkspaceAreSearchableToo()
    {
        PaletteEntry entry = Assert.Single(
            PaletteEntries.ForWindows([Window(title: "Untitled document", process: "notepad", workspace: "3")]));

        // Finding a window by its application when the title says nothing about it is
        // most of what this is for.
        Assert.Contains("notepad", entry.Secondary, StringComparison.Ordinal);
        Assert.Contains("3", entry.Secondary, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnmanagedWindowSaysSo()
    {
        PaletteEntry entry = Assert.Single(
            PaletteEntries.ForWindows([Window(managed: false, reason: "window is elevated", state: null)]));

        Assert.Contains("unmanaged", entry.Badges);
    }

    [Fact]
    public void UnmanagedWindowsCanBeLeftOut()
    {
        IReadOnlyList<PaletteEntry> entries = PaletteEntries.ForWindows(
            [Window(handle: 1), Window(handle: 2, managed: false, state: null)],
            includeUnmanaged: false);

        Assert.Single(entries);
    }

    [Fact]
    public void ShubbaksOwnConcealmentIsNotWorthABadge()
    {
        PaletteEntry entry = Assert.Single(
            PaletteEntries.ForWindows([Window(managed: true, concealment: "cloaked", workspace: "7")]));

        // Every managed window on an inactive workspace is cloaked, because that is
        // how Shubbak conceals them. Badging all of them would mark most of the list
        // and mean nothing - and "on workspace 7" has already said where it is.
        Assert.DoesNotContain("cloaked", entry.Badges);
    }

    [Fact]
    public void ConcealmentByAnyoneElseIsWorthABadge()
    {
        PaletteEntry cloaked = Assert.Single(
            PaletteEntries.ForWindows([Window(managed: false, concealment: "cloaked", state: null)]));

        PaletteEntry hidden = Assert.Single(
            PaletteEntries.ForWindows([Window(concealment: "hidden")]));

        PaletteEntry minimised = Assert.Single(
            PaletteEntries.ForWindows([Window(concealment: "minimised", state: "minimised")]));

        Assert.Contains("cloaked", cloaked.Badges);

        // The one a user cannot undo without help: a window left hidden by a window
        // manager that died is invisible to Alt+Tab and to the taskbar both.
        Assert.Contains("hidden", hidden.Badges);
        Assert.Contains("minimised", minimised.Badges);
    }

    [Fact]
    public void StickyAndElevatedAreShown()
    {
        PaletteEntry entry = Assert.Single(
            PaletteEntries.ForWindows([Window(sticky: true, elevated: true)]));

        Assert.Contains("sticky", entry.Badges);
        Assert.Contains("elevated", entry.Badges);
    }

    [Fact]
    public void TilingIsTheOrdinaryStateAndEarnsNothing()
    {
        PaletteEntry entry = Assert.Single(PaletteEntries.ForWindows([Window(state: "tiling")]));

        Assert.Empty(entry.Badges);
    }

    // ---- commands -------------------------------------------------------------

    [Fact]
    public void ACommandWithNoArgumentsRunsWhenChosen()
    {
        PaletteEntry entry = Assert.Single(PaletteEntries.ForCommands(
            [new CommandInfo("wm-toggle-pause", "Suspend tiling, or resume it", [], [])]));

        Assert.Equal("wm-toggle-pause", entry.Command);
    }

    [Fact]
    public void ACommandThatNeedsArgumentsIsOfferedRatherThanRun()
    {
        PaletteEntry entry = Assert.Single(PaletteEntries.ForCommands(
            [new CommandInfo("focus-window", "Focus a window by handle", ["windowhandle"], [])]));

        // An empty command means "put this in the search box to finish". Running it
        // bare would be refused by the parser and read as a broken palette.
        Assert.Equal(string.Empty, entry.Command);
        Assert.Contains("<windowhandle>", entry.Badges);
    }

    // ---- workspaces and layouts -------------------------------------------------

    [Fact]
    public void AWorkspaceIsChosenByName()
    {
        PaletteEntry entry = Assert.Single(PaletteEntries.ForWorkspaces(
            [new WorkspaceInfo(1, "3", "code", true, true, "m", "splith", 2, 0, 0, true)]));

        Assert.Equal("focus --workspace 3", entry.Command);

        // Shown by its label, searched by it too: someone who named a workspace
        // "code" will type "code", not "3".
        Assert.Equal("code", entry.Primary);
        Assert.Contains("focused", entry.Badges);
    }

    [Fact]
    public void BusyWorkspacesComeFirst()
    {
        IReadOnlyList<PaletteEntry> entries = PaletteEntries.ForWorkspaces(
        [
            new WorkspaceInfo(1, "1", "", false, false, "m", "splith", 0, 0, 0),
            new WorkspaceInfo(2, "2", "", false, true, "m", "splith", 5, 0, 0),
        ]);

        // An empty workspace is somewhere to go, not something to find.
        Assert.True(entries[1].Rank > entries[0].Rank);
    }

    [Fact]
    public void ALayoutIsSetWhenChosen()
    {
        PaletteEntry entry = Assert.Single(PaletteEntries.ForLayouts(["fibonacci"]));

        Assert.Equal("layout --set fibonacci", entry.Command);
    }

    // ---- membership --------------------------------------------------------

    private static WindowCandidate Tagged(string workspace, params string[] tags) =>
        new(0x900, "a window", "TestClass", "test", 42, false, true, null, "tiling",
            "none", workspace, true, "\\\\.\\DISPLAY1", false, false, 0, null, tags);

    [Fact]
    public void ATaggedWindowSaysWhereItWillFollowYou()
    {
        // Tagging records complete membership, so the set always contains the
        // workspace the window is already on - see WindowManager.Tag, which explains
        // why that is needed for it to be able to come back.
        PaletteEntry entry = Assert.Single(PaletteEntries.ForWindows([Tagged("3", "3", "-")]));

        Assert.Contains("also on -", entry.Badges);
    }

    [Fact]
    public void TheWorkspaceItIsAlreadyOnIsNotListed()
    {
        PaletteEntry entry = Assert.Single(PaletteEntries.ForWindows([Tagged("3", "3", "-")]));

        // "also on 3, -" for a window sitting on 3 is the noisier half of the answer.
        Assert.DoesNotContain(entry.Badges, b => b.Contains('3'));
    }

    [Fact]
    public void AWindowTaggedOnlyToItsOwnWorkspaceFollowsNowhere()
    {
        Assert.Empty(PaletteEntries.FollowsTo(Tagged("3", "3")));

        PaletteEntry entry = Assert.Single(PaletteEntries.ForWindows([Tagged("3", "3")]));
        Assert.DoesNotContain(entry.Badges, b => b.StartsWith("also on", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUntaggedWindowSaysNothingAboutFollowing()
    {
        PaletteEntry entry = Assert.Single(PaletteEntries.ForWindows([Window()]));

        Assert.DoesNotContain(entry.Badges, b => b.StartsWith("also on", StringComparison.Ordinal));
    }

    [Fact]
    public void ATaggedWindowIsOfferedAWayToStopIt()
    {
        IReadOnlyList<PaletteAction> actions = PaletteActions.For(Tagged("3", "3", "-"), "3");

        // The escape hatch was previously discoverable only by reading your own
        // configuration, which is not where somebody looks when a window is moving on
        // its own.
        PaletteAction stop = Assert.Single(actions, a => a.Name.StartsWith("Stop it", StringComparison.Ordinal));

        Assert.EndsWith("tag --clear", stop.Command, StringComparison.Ordinal);
        Assert.Contains("-", stop.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUntaggedWindowIsNotOfferedIt()
    {
        // Offering to clear tags that do not exist advertises a problem the user does
        // not have.
        Assert.DoesNotContain(
            PaletteActions.For(Window(), "1"),
            a => a.Name.StartsWith("Stop it", StringComparison.Ordinal));
    }
    private static WorkspaceInfo Workspace(
        string name = "1",
        string layout = "splith",
        string monitor = "\\\\.\\DISPLAY1",
        int windows = 2,
        bool focused = true) =>
        new(1, name, name, true, windows > 0, monitor, layout, windows, 0, 0, focused);

    private static MonitorInfoDto Monitor(
        string deviceId = "\\\\.\\DISPLAY1",
        bool primary = true,
        uint dpi = 96,
        int width = 2560,
        int height = 1440,
        string? showing = "3") =>
        new(1, deviceId, primary, dpi, 0, 0, width, height, showing);

    [Fact]
    public void AWorkspaceSaysWhichLayoutItIsUsing()
    {
        // Already fetched and, until now, thrown away. "Which workspace is the one on
        // fibonacci" was a question the list could answer and did not.
        PaletteEntry entry = Assert.Single(PaletteEntries.ForWorkspaces([Workspace(layout: "fibonacci")]));

        Assert.Contains("fibonacci", entry.Secondary, StringComparison.Ordinal);
        Assert.Contains("2 windows", entry.Secondary, StringComparison.Ordinal);
    }

    [Fact]
    public void AWorkspaceNamesItsScreenOnlyWhenThereIsMoreThanOne()
    {
        // On a single display the answer is the same on every row, which is noise
        // dressed as information.
        Assert.DoesNotContain(
            "DISPLAY1",
            Assert.Single(PaletteEntries.ForWorkspaces([Workspace()])).Secondary,
            StringComparison.Ordinal);

        Assert.Contains(
            "DISPLAY1",
            Assert.Single(PaletteEntries.ForWorkspaces([Workspace()], severalMonitors: true)).Secondary,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AWindowNamesItsScreenOnlyWhenThereIsMoreThanOne()
    {
        Assert.DoesNotContain(
            "DISPLAY1",
            Assert.Single(PaletteEntries.ForWindows([Window()])).Secondary,
            StringComparison.Ordinal);

        Assert.Contains(
            "DISPLAY1",
            Assert.Single(PaletteEntries.ForWindows([Window()], severalMonitors: true)).Secondary,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ADeviceIdIsShortenedToThePartAPersonReads()
    {
        // The rest is punctuation Windows insists on.
        Assert.Equal("DISPLAY2", PaletteEntries.ShortMonitor(@"\\.\DISPLAY2"));
        Assert.Equal(string.Empty, PaletteEntries.ShortMonitor(null));
    }

    [Fact]
    public void TheLayoutInUseIsMarked()
    {
        // So the list says where you are as well as where you could go.
        IReadOnlyList<PaletteEntry> entries =
            PaletteEntries.ForLayouts(["splith", "fibonacci"], current: "fibonacci");

        Assert.Empty(entries[0].Badges);
        Assert.Contains("in use", entries[1].Badges);
    }

    [Fact]
    public void ChoosingAMonitorGoesToTheWorkspaceItIsShowing()
    {
        // The window manager has no command that names a display, and activating what
        // is on it amounts to the same thing.
        PaletteEntry entry = Assert.Single(PaletteEntries.ForMonitors([Monitor(showing: "7")]));

        Assert.Equal("DISPLAY1", entry.Primary);
        Assert.Equal("focus --workspace 7", entry.Command);
        Assert.Contains("primary", entry.Badges);
    }

    [Fact]
    public void AMonitorAtAnUnusualScaleSaysSo()
    {
        // 96 is the ordinary case and earns no badge; anything else is worth knowing,
        // because scaling is behind most "why is it the wrong size" questions.
        Assert.DoesNotContain("96 dpi", Assert.Single(PaletteEntries.ForMonitors([Monitor()])).Badges);

        Assert.Contains(
            "144 dpi",
            Assert.Single(PaletteEntries.ForMonitors([Monitor(dpi: 144)])).Badges);
    }

    [Fact]
    public void AReportBecomesRowsOfLabelAndValue()
    {
        // The value is the searched half and the label the dim one, because somebody
        // narrowing a long report types the thing they are looking for - a class name,
        // a path - not the word "class".
        IReadOnlyList<PaletteEntry> entries = PaletteEntries.ForReport(
            Report(handle: 0x3047A, className: "Chrome_WidgetWin_1"));

        PaletteEntry handle = entries.First(e => e.Secondary == "handle");

        Assert.Equal("0x3047A", handle.Primary);

        PaletteEntry className = entries.First(e => e.Secondary == "class");

        Assert.Equal("Chrome_WidgetWin_1", className.Primary);
    }

    [Fact]
    public void AReportSaysWhyAWindowIsNotManageable()
    {
        // The whole point of the report. The verdict and the sentence explaining it
        // belong on one row, because "no" on its own is the half that says nothing.
        IReadOnlyList<PaletteEntry> entries = PaletteEntries.ForReport(
            Report(manageable: false, verdict: "window has no area"));

        Assert.Equal(
            "no - window has no area",
            entries.First(e => e.Secondary == "manageable").Primary);
    }

    [Fact]
    public void AnUnmanagedWindowSaysWhetherARuleIsWhy()
    {
        // Two different answers with two different fixes: a rule is something the user
        // wrote and can unwrite, and everything else is not.
        Assert.Equal(
            "no - excluded by a rule",
            PaletteEntries.ForReport(Report(excludedByRule: true))
                .First(e => e.Secondary == "managed").Primary);

        Assert.Equal(
            "no",
            PaletteEntries.ForReport(Report())
                .First(e => e.Secondary == "managed").Primary);
    }

    [Fact]
    public void ARuleRowSaysWhetherItMatchedAndWhereItLives()
    {
        // The line number is what makes the answer actionable: a rule that did not
        // match is only useful if you can go and look at it.
        IReadOnlyList<PaletteEntry> entries = PaletteEntries.ForReport(Report(rules:
        [
            new RuleReport("float the pip", 42, Matched: true),
            new RuleReport("browsers to 2", 51, Matched: false),
        ]));

        PaletteEntry matched = entries.First(e => e.Primary.StartsWith("float", StringComparison.Ordinal));

        Assert.Equal("rule  [x]", matched.Secondary);
        Assert.Contains("line 42", matched.Primary, StringComparison.Ordinal);

        Assert.Equal(
            "rule  [ ]",
            entries.First(e => e.Primary.StartsWith("browsers", StringComparison.Ordinal)).Secondary);
    }

    [Fact]
    public void NoRulesIsSaidOutLoudRatherThanLeftBlank()
    {
        // An absent section reads as the report having failed. "None configured" is
        // the answer to "why did my rule not fire" when there are no rules at all.
        Assert.Equal(
            "(none configured)",
            PaletteEntries.ForReport(Report()).First(e => e.Secondary == "rules").Primary);
    }

    [Fact]
    public void AnAppThatMissedListsTheMatchersThatMissed()
    {
        // What turns "my rule does not fire" into a one-glance diagnosis: the rule is
        // usually fine, and one matcher in the app definition is what missed.
        IReadOnlyList<PaletteEntry> entries = PaletteEntries.ForReport(Report(apps:
        [
            new AppReport("browser", Matched: false, ["class ~= Chrome_WidgetWin_1"]),
        ]));

        Assert.Equal("app  [ ]", entries.First(e => e.Primary == "browser").Secondary);

        Assert.Equal(
            "class ~= Chrome_WidgetWin_1",
            entries.First(e => e.Secondary == "failed").Primary);
    }

    [Fact]
    public void ReportRowsKeepTheirOrder()
    {
        // A report is an argument read top to bottom. Sorting it by rank the way the
        // other lists are sorted would shuffle the reasoning.
        IReadOnlyList<PaletteEntry> entries = PaletteEntries.ForReport(Report());

        for (int i = 1; i < entries.Count; i++)
            Assert.True(entries[i - 1].Rank > entries[i].Rank);
    }

    [Fact]
    public void AReportRowDoesNothingWhenChosen()
    {
        // It is something to read, not something to run. Choosing one opens it rather
        // than sending anything, which is why Expands carries the text and Command
        // stays empty.
        Assert.All(
            PaletteEntries.ForReport(Report()),
            e => Assert.Equal(string.Empty, e.Command));
    }

    [Fact]
    public void EveryReportRowCanBeOpenedInFull()
    {
        // A row is drawn on one line and clipped, and the values worth opening a
        // report for - a path, the sentence about elevation - are the long ones.
        Assert.All(
            PaletteEntries.ForReport(Report()),
            e => Assert.False(string.IsNullOrEmpty(e.Expands)));
    }

    [Fact]
    public void AnExpandedRowCarriesItsLabelAsWellAsItsValue()
    {
        // Copied on its own, "0x3047A" says nothing about what it is. The row on
        // screen has the label beside it; the copy has to carry it too.
        string expanded = PaletteEntries.ForReport(Report(handle: 0x3047A))
            .First(e => e.Secondary == "handle").Expands!;

        Assert.Contains("handle", expanded, StringComparison.Ordinal);
        Assert.Contains("0x3047A", expanded, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedReportStillSaysSomething()
    {
        // An empty list would read as "the palette is broken" rather than "the window
        // manager would not answer" - and Push refuses an empty frame, so the request
        // would appear to do nothing at all.
        Assert.Equal(
            "no such window",
            Assert.Single(PaletteEntries.ForReportFailure("no such window")).Primary);
    }

    private static WindowReport Report(
        long handle = 0x100,
        string className = "TestClass",
        bool manageable = true,
        string verdict = "manageable",
        bool excludedByRule = false,
        ManagedWindowReport? node = null,
        IReadOnlyList<RuleReport>? rules = null,
        IReadOnlyList<AppReport>? apps = null) =>
        new(handle, "a window", className, "test", @"C:\test.exe", 0, 0, 800, 600,
            0x16CF0000, 0x00040100, true, "None", false, manageable, verdict, "manageable",
            node is not null, excludedByRule, node, rules ?? [], apps ?? []);

    /// <summary>A stand-in for the renderer: every character the same width.</summary>
    /// <remarks>
    /// Fixed-width on purpose. The real measurer is a proportional font behind a GDI
    /// device context, and a test that needed one would be testing the font.
    /// </remarks>
    private static int Measure(string text) => text.Length * 10;

    [Fact]
    public void AValueThatFitsIsLeftAsOneLine()
    {
        PaletteEntry entry = Assert.Single(PaletteEntries.ForWrapped("short", 500, Measure));

        Assert.Equal("short", entry.Primary);
    }

    [Fact]
    public void AValueTooLongIsBrokenBetweenWords()
    {
        // Greedy: as many words as fit, then the next line. Words stay whole, because
        // a sentence broken mid-word is harder to read than one broken a little early.
        IReadOnlyList<PaletteEntry> entries =
            PaletteEntries.ForWrapped("one two three four", 100, Measure);

        Assert.Equal(["one two", "three four"], entries.Select(e => e.Primary));

        // Nothing lost and nothing invented: the words come back in order.
        Assert.Equal("one two three four", string.Join(' ', entries.Select(e => e.Primary)));
    }

    [Fact]
    public void AWordWithNoSpacesIsBrokenAnyway()
    {
        // A path or a regular expression has nowhere to break politely, and those are
        // exactly the values worth opening in full. Refusing to break would put the
        // whole thing on one clipped line, which is the problem this solves.
        IReadOnlyList<PaletteEntry> entries = PaletteEntries.ForWrapped(
            @"C:\Program_Files\a_very_long_directory\msedge.exe", 100, Measure);

        Assert.True(entries.Count > 1);
        Assert.All(entries, e => Assert.True(Measure(e.Primary) <= 100));

        Assert.Equal(
            @"C:\Program_Files\a_very_long_directory\msedge.exe",
            string.Concat(entries.Select(e => e.Primary)));
    }

    [Fact]
    public void NoLineIsWiderThanTheRoomItHas()
    {
        IReadOnlyList<PaletteEntry> entries = PaletteEntries.ForWrapped(
            "the window runs at a higher integrity level than Shubbak so Windows refuses to move it",
            200,
            Measure);

        Assert.All(entries, e => Assert.True(
            Measure(e.Primary) <= 200,
            $"\"{e.Primary}\" is {Measure(e.Primary)} wide"));
    }

    [Fact]
    public void WrappedLinesKeepTheirOrder()
    {
        // A sentence sorted by rank is not a sentence.
        IReadOnlyList<PaletteEntry> entries =
            PaletteEntries.ForWrapped("one two three four five six", 100, Measure);

        for (int i = 1; i < entries.Count; i++)
            Assert.True(entries[i - 1].Rank > entries[i].Rank);
    }

    [Fact]
    public void AWrappedLineDoesNothingAndCannotBeOpenedAgain()
    {
        // Otherwise Enter on a wrapped line would expand it into itself, for ever.
        IReadOnlyList<PaletteEntry> entries =
            PaletteEntries.ForWrapped("one two three four", 100, Measure);

        Assert.All(entries, e =>
        {
            Assert.Equal(string.Empty, e.Command);
            Assert.Null(e.Expands);
        });
    }

    [Fact]
    public void AWidthThatCanHoldNothingDoesNotHang()
    {
        // Guards the loop rather than the look of it. Asking for a smaller prefix for
        // ever is the failure mode here, and it would take the message loop with it.
        IReadOnlyList<PaletteEntry> entries = PaletteEntries.ForWrapped("something", 0, Measure);

        Assert.Equal("something", Assert.Single(entries).Primary);
    }

    [Fact]
    public void WrappingSomethingEmptyStillProducesARow()
    {
        // Push refuses an empty frame, so returning nothing would make Enter appear to
        // do nothing at all.
        Assert.Single(PaletteEntries.ForWrapped(string.Empty, 100, Measure));
    }
}