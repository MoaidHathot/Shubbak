using Dalil.Core;
using Shubbak.Ipc;

namespace Dalil.Core.Tests;

/// <summary>
/// The things a palette can do that a keybinding cannot.
/// </summary>
/// <remarks>
/// Sending a window somewhere, acting on six of them at once, and writing the rule
/// that stops the next one going wrong. None of these need a new command in the window
/// manager - the pipe has always taken a newline-separated sequence - and all of them
/// were unreachable from anywhere but a shell.
/// </remarks>
public sealed class PaletteBulkAndRuleTests
{
    private static WindowCandidate Window(
        long handle = 0x100,
        string title = "a window",
        string className = "TestClass",
        string process = "test.exe",
        string? workspace = "1",
        string? scratchpad = null) =>
        new(handle, title, className, process, 42, false, true, null, "tiling",
            "none", workspace, true, "\\\\.\\DISPLAY1", false, false, 0,
            Scratchpad: scratchpad, Tags: null, ExclusionSummary: null);

    private static PaletteAction Find(IReadOnlyList<PaletteAction> actions, string starting) =>
        actions.First(a => a.Name.StartsWith(starting, StringComparison.Ordinal));

    // ---- sending a window somewhere ---------------------------------------------------

    [Fact]
    public void AWindowCanBeSentSomewhereAndNotOnlyBroughtHere()
    {
        // The hole this fills. The palette could bring a window here and could tag it
        // onto a workspace, and could not send it to one - despite `move --workspace`
        // being a verb the window manager has always accepted. Tagging is not a
        // substitute: a tag is a membership that makes the window follow you about,
        // which is a different and much stranger thing.
        PaletteAction move = Find(PaletteActions.For(Window(workspace: "1"), "1", ["1", "2", "3"]), "Move it to");

        Assert.NotNull(move.Children);
        Assert.Equal(2, move.Children!.Count);
        Assert.EndsWith("move --workspace 2", move.Children[0].Command, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWorkspaceAWindowIsAlreadyOnIsNotADestination()
    {
        // Unlike the tag picker, which lists it because an incomplete list of
        // memberships would read as a bug. Moving is about a destination, and "move it
        // to where it already is" is not one anybody is choosing between.
        PaletteAction move = Find(PaletteActions.For(Window(workspace: "2"), "1", ["1", "2"]), "Move it to");

        Assert.Equal("1", Assert.Single(move.Children!).Name);
    }

    [Fact]
    public void AStashedWindowIsNotOfferedADestination()
    {
        // Summoning already lands it on whichever workspace is focused, so moving it
        // would be the same action twice.
        IReadOnlyList<PaletteAction> actions =
            PaletteActions.For(Window(scratchpad: "notes"), "1", ["1", "2"]);

        Assert.DoesNotContain(actions, a => a.Name.StartsWith("Move it to", StringComparison.Ordinal));
    }

    // ---- acting on several at once ------------------------------------------------------

    [Fact]
    public void ActingOnSeveralWindowsAimsAtEachInTurnInOneMessage()
    {
        // The reason a palette is worth having over a keybinding. Moving six windows by
        // keyboard is six rounds of find-it, focus-it, move-it, with the focus landing
        // somewhere different after each one.
        IReadOnlyList<PaletteAction> actions =
            PaletteActions.ForMany(["focus-window 1", "focus-window 2"], "3");

        PaletteAction bring = Find(actions, "Bring them here");

        Assert.Equal(
            "focus-window 1\nmove --workspace 3\nfocus-window 2\nmove --workspace 3",
            bring.Command);
    }

    [Fact]
    public void TheCountIsSaidOutLoudSoNobodyActsOnMoreThanTheyMeantTo()
    {
        IReadOnlyList<PaletteAction> actions =
            PaletteActions.ForMany(["focus-window 1", "focus-window 2", "focus-window 3"], "3");

        Assert.Contains("3 windows", Find(actions, "Close them").Description, StringComparison.Ordinal);
    }

    [Fact]
    public void OneWindowIsNotThreeWindows()
    {
        IReadOnlyList<PaletteAction> actions = PaletteActions.ForMany(["focus-window 1"], "3");

        Assert.Contains("1 window", Find(actions, "Close them").Description, StringComparison.Ordinal);
        Assert.DoesNotContain("1 windows", Find(actions, "Close them").Description, StringComparison.Ordinal);
    }

    [Fact]
    public void ClosingSeveralIsStillIrreversible()
    {
        Assert.True(Find(PaletteActions.ForMany(["focus-window 1"], "3"), "Close them").Destructive);
    }

    [Fact]
    public void MarkingNothingOffersNothing()
    {
        // Rather than an action list of verbs with no subject.
        Assert.Empty(PaletteActions.ForMany([], "3"));
    }

    [Fact]
    public void AStashedWindowIsAimedAtByItsSlotEvenInABulkAction()
    {
        // Focusing a cloaked window reveals it without unstashing it, so it vanishes
        // again at the next layout pass - which reads as the palette having failed.
        Assert.Equal("scratchpad notes", PaletteActions.TargetOf(Window(scratchpad: "notes")));
    }

    [Fact]
    public void AnOrdinaryWindowIsAimedAtByItsHandle()
    {
        Assert.Equal("focus-window 256", PaletteActions.TargetOf(Window(handle: 0x100)));
    }

    // ---- writing the rule ---------------------------------------------------------------

    [Fact]
    public void ARuleIsComposedFromTheAttributesWorthMatchingOn()
    {
        string rule = RuleComposer.Rule(null, "Chrome_WidgetWin_1", "msedge.exe");

        Assert.Contains("""class "Chrome_WidgetWin_1" """.TrimEnd(), rule, StringComparison.Ordinal);
        Assert.Contains("""process "msedge.exe" """.TrimEnd(), rule, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRuleIsNamedForTheApplicationWithoutItsExtension()
    {
        // Because `rule "msedge"` reads better than `rule "msedge.exe"`, and is what
        // somebody would have typed.
        Assert.Contains("""rule "msedge" """.TrimEnd(), RuleComposer.Rule(null, "C", "msedge.exe"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheTitleIsOfferedCommentedOutRatherThanApplied()
    {
        // A title is the most inviting attribute and the worst one to match on: it
        // changes as the document changes, it is localised, and it usually contains the
        // very thing that made the window interesting for five seconds.
        string rule = RuleComposer.Rule(null, "C", "p.exe", "Inbox - Fastmail");

        Assert.Contains("""// title "Inbox - Fastmail" """.TrimEnd(), rule, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDoBlockIsLeftForTheUserToFillIn()
    {
        // Guessing is the one thing this must not do. The same window somebody wants
        // floated is one somebody else wants ignored, and a generated rule that quietly
        // did the wrong thing would be worse than no rule - it would look right.
        string rule = RuleComposer.Rule(null, "C", "p.exe");

        Assert.Contains("do {", rule, StringComparison.Ordinal);
        Assert.Contains("// float, ignore, manage", rule, StringComparison.Ordinal);
    }

    [Fact]
    public void AQuoteInATitleCannotEscapeTheStringItIsIn()
    {
        string rule = RuleComposer.Rule(null, "C", "p.exe", "a \"quoted\" name");

        Assert.Contains("\\\"quoted\\\"", rule, StringComparison.Ordinal);
    }

    [Fact]
    public void ABackslashInAPathIsEscapedBeforeTheQuotesAre()
    {
        // The other order escapes the backslashes that the quote escaping just added.
        string rule = RuleComposer.RuleFromReport("C", "p.exe", @"C:\Program Files\p.exe", null);

        Assert.Contains(@"C:\\Program Files\\p.exe", rule, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePathIsOfferedBesideTheProcessRatherThanInsteadOfIt()
    {
        // Two live matchers for the same idea would be stricter than anybody meant, and
        // silently dropping the one that is nearly always right would be worse. The
        // path matters when two applications share a process name, as every Electron
        // application on the machine does.
        string rule = RuleComposer.RuleFromReport("C", "p.exe", @"C:\p.exe", null);

        Assert.Contains("""process "p.exe" """.TrimEnd(), rule, StringComparison.Ordinal);
        Assert.Contains("// path", rule, StringComparison.Ordinal);
    }

    [Fact]
    public void ARuleWithNothingToMatchOnSaysSoRatherThanMatchingEverything()
    {
        // An empty match block matches every window on the desktop, which the window
        // manager warns about at load time and which would be a spectacularly bad thing
        // to have generated for somebody.
        string rule = RuleComposer.Rule(null, null, null);

        Assert.Contains("add a matcher here", rule, StringComparison.Ordinal);
        Assert.Contains("""rule "new rule" """.TrimEnd(), rule, StringComparison.Ordinal);
    }

    [Fact]
    public void WritingARuleIsOfferedOnEveryWindowRow()
    {
        PaletteAction write = Find(PaletteActions.For(Window(handle: 0x2A), "1"), "Write a rule");

        // It opens the rules that could be written, once the report is in: nothing is
        // sent, and nothing touches the config file until a row that says it will.
        Assert.Equal(string.Empty, write.Command);
        Assert.Equal(0x2A, write.Composes);
        Assert.Null(write.Expands);
    }

    [Fact]
    public void ARuleIsNamedForWhatItDoesToWhat()
    {
        // A configuration with a dozen rules is read by their names, and a name that
        // says only which application leaves the reader opening each one.
        Assert.Contains("rule \"ignore msedge\"", RuleComposer.Rule(null, "C", "msedge.exe", does: ["ignore"]), StringComparison.Ordinal);
        Assert.Contains("rule \"manage WhatsApp\"", RuleComposer.Rule(null, "C", "WhatsApp.exe", does: ["manage", "float"]), StringComparison.Ordinal);
        Assert.Contains("rule \"msedge on 2\"", RuleComposer.Rule(null, "C", "msedge.exe", does: ["move --workspace \"2\""]), StringComparison.Ordinal);
        Assert.Contains("rule \"float Calc\"", RuleComposer.Rule(null, "C", "Calc", does: ["float"]), StringComparison.Ordinal);
    }

    [Fact]
    public void TheVerbsGoIntoTheDoBlockOnePerLine()
    {
        string rule = RuleComposer.Rule(null, "C", "p.exe", does: ["manage", "move --workspace \"2\""]);

        Assert.Contains("        do {\n            manage\n            move --workspace \"2\"\n        }", rule, StringComparison.Ordinal);
        Assert.DoesNotContain(RuleComposer.Undecided, rule, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePathRidesBesideTheProcessWhateverTheVerb()
    {
        string rule = RuleComposer.Rule(null, "C", "p.exe", null, ["ignore"], @"C:\p.exe");

        Assert.Contains("            process \"p.exe\"\n            // path \"C:\\\\p.exe\"\n", rule, StringComparison.Ordinal);
    }

    // ---- getting the rule out -----------------------------------------------------------

    [Fact]
    public void ACompleteRuleCanBeAddedCopiedOrItsFileOpened()
    {
        // The three steps between reading a rule and having it in the file, and the
        // first takes the other two off the user's hands. It says so in its name: it is
        // the one row in the palette that writes to a file somebody maintains by hand.
        IReadOnlyList<PaletteAction> actions = PaletteActions.ForRule("rules { }", 0x2A, complete: true);

        Assert.Equal(["Add it to the config and reload", "Copy the rule", "Open the config"], actions.Select(a => a.Name));

        PaletteAction add = actions[0];
        Assert.Equal("rules { }", add.Applies!.Kdl);
        Assert.Equal(0x2A, add.Applies.Handle);
        Assert.Equal(string.Empty, add.Command);

        Assert.Equal("rules { }", actions[1].Copies);
        Assert.Equal(PaletteEntries.BuiltinOpenConfig, actions[2].Command);
    }

    [Fact]
    public void AnUndecidedRuleIsNotOfferedForAdding()
    {
        // The loader drops a rule with an empty do block, so adding one would change
        // nothing and look like it had.
        IReadOnlyList<PaletteAction> actions = PaletteActions.ForRule("rules { }", 0x2A, complete: false);

        Assert.DoesNotContain(actions, a => a.Applies is not null);
        Assert.Contains(actions, a => a.Copies is not null);
    }

    [Fact]
    public void EveryChoiceReadsOnEnterAndAddsFromCtrlEnter()
    {
        // The same text twice: what Enter opens to read is what "Add it" hands the
        // window manager and what "Copy the rule" puts on the clipboard. A copy that
        // differed from what was read would be the silent transcription error this
        // whole feature exists to remove.
        foreach (PaletteAction choice in PaletteActions.RuleChoices(Managed(), "1", ["1", "2"]).Where(c => c.Expands is not null))
        {
            Assert.NotNull(choice.Children);

            foreach (PaletteAction step in choice.Children!.Where(s => s.Applies is not null || s.Copies is not null))
                Assert.Equal(choice.Expands, step.Applies?.Kdl ?? step.Copies);
        }
    }

    [Fact]
    public void AChoiceDoesNotAdvertiseItsListAsWhatEnterDoes()
    {
        // A row that reads and also carries a list keeps the list behind Ctrl+Enter, so
        // a "3 ›" badge beside "↵ read it" would promise Enter a list it will not show.
        IReadOnlyList<PaletteEntry> rows = PaletteActions.AsEntries(PaletteActions.RuleChoices(Managed(), "1", ["1", "2"]));

        PaletteEntry ignore = rows.Single(e => e.Primary == "Ignore it");

        Assert.DoesNotContain(ignore.Badges, b => b.EndsWith('\u203A'));
        Assert.True(ignore.HasActions);
        Assert.False(string.IsNullOrEmpty(ignore.Expands));

        // "Send it to workspace..." has no text to read, so Enter opens its list and
        // the badge is telling the truth.
        Assert.Contains(rows.Single(e => e.Primary.StartsWith("Send it to", StringComparison.Ordinal)).Badges, b => b.EndsWith('\u203A'));
    }

    [Fact]
    public void ARowThatOnlyOpensAListStillSaysSo()
    {
        PaletteEntry move = PaletteActions.AsEntries(PaletteActions.For(Window(workspace: "1"), "1", ["1", "2", "3"]))
            .Single(e => e.Primary.StartsWith("Move it to", StringComparison.Ordinal));

        Assert.Contains(move.Badges, b => b.EndsWith('\u203A'));
    }

    [Fact]
    public void EveryKindOfRequestSurvivesBecomingARow()
    {
        // What the window reads when Enter is pressed. Dropped in the conversion, the
        // row would look right and do nothing.
        IReadOnlyList<PaletteEntry> rows = PaletteActions.AsEntries(
        [
            new PaletteAction("copy", "", "", Copies: "rules { }"),
            new PaletteAction("add", "", "", Applies: new RuleToAdd("rules { }", 0x2A)),
            new PaletteAction("remove", "", "", Removes: new RuleToRemove("x", 7, 0x2A)),
            new PaletteAction("compose", "", "", Composes: 0x2A),
        ]);

        Assert.Equal("rules { }", rows[0].Copies);
        Assert.Equal(new RuleToAdd("rules { }", 0x2A), rows[1].Applies);
        Assert.Equal(new RuleToRemove("x", 7, 0x2A), rows[2].Removes);
        Assert.Equal(0x2A, rows[3].Composes);
    }

    // ---- which rules are offered, and in which order --------------------------------------

    private static WindowReport Managed(string state = "Tiling", string workspace = "1", bool manageable = true, IReadOnlyList<RuleReport>? rules = null) =>
        Report(managed: true, manageable: manageable, node: new ManagedWindowReport(1, state, workspace, true, false, [], null), rules: rules);

    private static WindowReport Report(
        bool managed = false,
        bool manageable = true,
        bool excludedByRule = false,
        bool? overridable = null,
        ManagedWindowReport? node = null,
        IReadOnlyList<RuleReport>? rules = null) =>
        new(0x2A, "a window", "TestClass", "test", @"C:\test.exe", 0, 0, 800, 600, 0, 0, true, "None", false,
            manageable, manageable ? "manageable" : "window has WS_EX_TOOLWINDOW", manageable ? "manageable" : "a tool window",
            managed, excludedByRule, node, rules ?? [], [], overridable);

    private static IReadOnlyList<string> Names(WindowReport report, string? here = "1", IReadOnlyList<string>? workspaces = null) =>
        [.. PaletteActions.RuleChoices(report, here, workspaces ?? ["1", "2", "3"]).Select(c => c.Name)];

    [Fact]
    public void AManagedWindowIsOfferedIgnoringFirst()
    {
        // The first row inverts what the window is now.
        Assert.Equal(
            ["Ignore it", "Float it", "Keep it tiled", "Send it to workspace\u2026", "Match it, decide later"],
            Names(Managed()));
    }

    [Fact]
    public void AFloatingWindowIsOfferedKeepingItThatWay()
    {
        Assert.Equal(
            ["Ignore it", "Keep it floating", "Tile it", "Send it to workspace\u2026", "Match it, decide later"],
            Names(Managed(state: "Floating")));
    }

    [Fact]
    public void AWindowManagedDespiteTheFilterIsOfferedTheRuleThatKeepsItSo()
    {
        // Forced, by hand or by a rule that is not there: toggle-managed is forgotten
        // on reload, and this is the row that makes it stick.
        Assert.Equal("Manage it", Names(Managed(manageable: false))[0]);
    }

    [Fact]
    public void AWindowARuleAlreadyForcesIsNotOfferedASecond()
    {
        WindowReport report = Managed(manageable: false, rules:
        [
            new RuleReport("manage test", 12, Matched: true, ["manage"], "manage"),
        ]);

        IReadOnlyList<string> names = Names(report);

        Assert.Equal("Stop forcing it", names[0]);
        Assert.DoesNotContain("Manage it", names);
    }

    [Fact]
    public void SendingItSomewhereLeavesOutWhereItIs()
    {
        PaletteAction send = PaletteActions.RuleChoices(Managed(workspace: "2"), "1", ["1", "2", "3"])
            .Single(c => c.Name.StartsWith("Send it to", StringComparison.Ordinal));

        IReadOnlyList<PaletteAction> destinations = send.Children!;

        Assert.Equal(["1", "3"], destinations.Select(c => c.Name));
        Assert.Contains("move --workspace \"3\"", destinations[1].Expands!, StringComparison.Ordinal);
        Assert.Contains("rule \"test on 3\"", destinations[1].Expands!, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnmanagedWindowTheFilterTurnedDownIsOfferedManagingFirst()
    {
        Assert.Equal(
            ["Manage it", "Manage it, floating", "Manage it on workspace\u2026", "Ignore it", "Match it, decide later"],
            Names(Report(manageable: false, overridable: true)));
    }

    [Fact]
    public void ManagingIsNotOfferedWhereNoRuleCouldDoIt()
    {
        // A cloaked window, a child control: the filter's reason is a fact, not an
        // opinion, and a rule that looked right and did nothing is the worst thing this
        // list could hand somebody.
        IReadOnlyList<string> names = Names(Report(manageable: false, overridable: false));

        Assert.DoesNotContain(names, n => n.StartsWith("Manage", StringComparison.Ordinal));
        Assert.Equal("Ignore it", names[0]);
    }

    [Fact]
    public void AnOlderDaemonLeavesManagingOfferedWithACaveat()
    {
        // Null from a daemon that predates the field: offered, and the description says
        // it may not work.
        PaletteAction manage = PaletteActions.RuleChoices(Report(manageable: false, overridable: null), "1", ["1"])
            .Single(c => c.Name == "Manage it");

        Assert.Contains("if the filter's reason allows it", manage.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void AWindowReleasedByHandIsOfferedTheRuleThatMakesItStick()
    {
        // Excluded, and no rule says so: toggle-managed did, and a reload takes it back.
        PaletteAction ignore = PaletteActions.RuleChoices(Report(excludedByRule: true), "1", ["1"])[0];

        Assert.Equal("Ignore it", ignore.Name);
        Assert.Contains("reload would otherwise take it back", ignore.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void AWindowARuleAlreadyIgnoresIsOfferedThatRulesRemovalFirst()
    {
        WindowReport report = Report(excludedByRule: true, rules:
        [
            new RuleReport("ignore test", 42, Matched: true, ["ignore"], "manage"),
            new RuleReport("unrelated", 50, Matched: false, ["float"], "manage"),
        ]);

        IReadOnlyList<PaletteAction> choices = PaletteActions.RuleChoices(report, "1", ["1"]);

        Assert.Equal("Stop ignoring it", choices[0].Name);
        Assert.Equal(new RuleToRemove("ignore test", 42, 0x2A), choices[0].Removes);
        Assert.Contains("line 42", choices[0].Description, StringComparison.Ordinal);

        // Not offered twice, and the rule that did not match is not offered for removal.
        Assert.DoesNotContain(choices, c => c.Name == "Ignore it");
        Assert.DoesNotContain(choices, c => c.Removes?.Name == "unrelated");
    }

    [Fact]
    public void AnIgnoreRuleOnAnotherTriggerDoesNotCount()
    {
        // ignore on title-change does nothing, so the window is not "already ignored"
        // and the rule that would is still offered.
        WindowReport report = Managed(rules:
        [
            new RuleReport("late", 42, Matched: true, ["ignore"], "title-change"),
        ]);

        IReadOnlyList<string> names = Names(report);

        Assert.Contains("Ignore it", names);
        Assert.DoesNotContain("Stop ignoring it", names);
        Assert.Contains("Remove \"late\"", names);
    }

    [Fact]
    public void AMatchedShapingRuleIsOfferedForRemoval()
    {
        WindowReport report = Managed(rules:
        [
            new RuleReport("float test", 9, Matched: true, ["float"], "manage"),
        ]);

        PaletteAction remove = PaletteActions.RuleChoices(report, "1", ["1"]).Single(c => c.Name == "Remove \"float test\"");

        Assert.Equal(new RuleToRemove("float test", 9, 0x2A), remove.Removes);
        Assert.Contains("float", remove.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void TheUndecidedRuleIsAlwaysLastAndNeverAdded()
    {
        foreach (WindowReport report in new[] { Managed(), Report(manageable: false, overridable: true), Report(excludedByRule: true) })
        {
            PaletteAction last = PaletteActions.RuleChoices(report, "1", ["1"])[^1];

            Assert.Equal("Match it, decide later", last.Name);
            Assert.Contains(RuleComposer.Undecided, last.Expands!, StringComparison.Ordinal);
            Assert.DoesNotContain(last.Children!, a => a.Applies is not null);
        }
    }

    [Fact]
    public void EveryChoiceThatAddsCarriesTheWindow()
    {
        // So the answer can say what became of it.
        foreach (PaletteAction choice in PaletteActions.RuleChoices(Managed(), "1", ["1", "2"]))
        {
            foreach (PaletteAction step in Flatten(choice.Children ?? []))
            {
                if (step.Applies is { } add) Assert.Equal(0x2A, add.Handle);
            }
        }
    }

    private static IEnumerable<PaletteAction> Flatten(IEnumerable<PaletteAction> actions)
    {
        foreach (PaletteAction action in actions)
        {
            yield return action;

            foreach (PaletteAction child in Flatten(action.Children ?? []))
                yield return child;
        }
    }

    // ---- what the answer shows ------------------------------------------------------------

    [Fact]
    public void AnAdditionIsShownWithTheWayBack()
    {
        var change = new RuleChange(@"C:\me\shubbak.kdl", 412, ["ignore test"], "rule \"ignore test\" { }", Reloaded: true, "\"a window\" was released.");

        IReadOnlyList<PaletteEntry> rows = PaletteEntries.ForRuleChange(change, added: true);

        Assert.Equal("Added \"ignore test\" at line 412 of shubbak.kdl and reloaded. \"a window\" was released.", rows[0].Primary);
        Assert.Equal("Remove it again", rows[1].Primary);
        Assert.Equal(new RuleToRemove("ignore test", 412, null), rows[1].Removes);
        Assert.Equal("rule \"ignore test\" { }", rows[2].Copies);
        Assert.Equal(PaletteEntries.BuiltinOpenConfig, rows[3].Command);
    }

    [Fact]
    public void ARemovalIsShownWithTheWayBack()
    {
        var change = new RuleChange(@"C:\me\shubbak.kdl", 412, ["ignore test"], "rule \"ignore test\" { }", Reloaded: true, "\"a window\" was adopted.");

        IReadOnlyList<PaletteEntry> rows = PaletteEntries.ForRuleChange(change, added: false);

        Assert.StartsWith("Removed \"ignore test\" from line 412", rows[0].Primary, StringComparison.Ordinal);
        Assert.Equal("Put it back", rows[1].Primary);
        Assert.Equal(new RuleToAdd("rule \"ignore test\" { }", null), rows[1].Applies);
        Assert.Equal("Copy the removed rule", rows[2].Primary);
    }

    [Fact]
    public void ARefusedReloadIsSaidPlainly()
    {
        // Written, but not in force: to the user that looks exactly like an edit that
        // landed, and the row has to say otherwise.
        var change = new RuleChange(@"C:\me\shubbak.kdl", 412, ["x"], "rule \"x\" { }", Reloaded: false, null);

        Assert.Contains("the reload was refused", PaletteEntries.ForRuleChange(change, added: true)[0].Primary, StringComparison.Ordinal);
    }
}
