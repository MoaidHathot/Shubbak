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
        PaletteAction write = Find(PaletteActions.For(Window(), "1"), "Write a rule");

        // It composes rather than commands: nothing is sent, and nothing touches the
        // config file. A window manager that edited your configuration behind you would
        // be a worse idea than a little typing.
        Assert.Equal(string.Empty, write.Command);
        Assert.Contains("TestClass", write.Expands!, StringComparison.Ordinal);
    }
}
