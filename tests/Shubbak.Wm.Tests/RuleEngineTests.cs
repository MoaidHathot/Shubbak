using Shubbak.Config;
using Shubbak.Core.Commands;
using Shubbak.Wm;

namespace Shubbak.Wm.Tests;

/// <summary>
/// Deciding which window rules apply to a window.
/// </summary>
/// <remarks>
/// <para>
/// Rule <i>parsing</i> has always been well covered. Rule <i>evaluation</i> had no
/// tests at all, which is how <c>on="title-change"</c> and <c>on="focus"</c> came to
/// be parsed, stored, documented in the example config - and then dispatched from
/// nowhere. A rule written against a title that only appears once an application has
/// loaded, or against a window becoming focused, silently never ran.
/// </para>
/// <para>
/// The rules here are written as config text rather than constructed by hand, so
/// what is tested is the path a user's file actually takes.
/// </para>
/// </remarks>
public sealed class RuleEngineTests
{
    private static RuleEngine Load(string rules)
    {
        ConfigLoadResult result = ConfigLoader.Load(rules);

        Assert.False(
            result.HasErrors,
            "Unexpected errors:\n" + string.Join("\n", result.Errors.Select(d => d.ToString())));

        var engine = new RuleEngine();
        engine.Load(result.Config);

        return engine;
    }

    private static WindowAttributes Window(
        string title = "Untitled",
        string className = "Window",
        string process = "app") =>
        new(title, className, process, $@"C:\bin\{process}.exe");

    // ---- which triggers exist ----------------------------------------------

    [Fact]
    public void ATriggerNobodyWroteARuleForHasNone()
    {
        // The question asked on the tick path before the attributes are built, because
        // building them costs four Win32 calls and a process handle. Almost every
        // configuration has no rule on title change or focus.
        RuleEngine engine = Load("""
            rules {
                rule "ignore it" {
                    match { process = "chrome" }
                    do { ignore }
                }
            }
            """);

        Assert.True(engine.HasRulesFor(RuleTrigger.OnManage));
        Assert.False(engine.HasRulesFor(RuleTrigger.OnTitleChange));
        Assert.False(engine.HasRulesFor(RuleTrigger.OnFocus));
    }

    [Fact]
    public void ARuleOnTitleChangeIsFoundUnderTitleChange()
    {
        // The bug this covers: these were parsed and stored and then never dispatched.
        RuleEngine engine = Load("""
            rules {
                rule "meeting" on="title-change" {
                    match { title regex="Meeting in progress" }
                    do { float }
                }
            }
            """);

        Assert.True(engine.HasRulesFor(RuleTrigger.OnTitleChange));
        Assert.Single(engine.For(RuleTrigger.OnTitleChange));

        // And not under any other trigger, which is what made it look like it worked.
        Assert.False(engine.HasRulesFor(RuleTrigger.OnManage));
        Assert.Empty(engine.For(RuleTrigger.OnManage));
    }

    [Fact]
    public void ARuleOnFocusIsFoundUnderFocus()
    {
        RuleEngine engine = Load("""
            rules {
                rule "on focus" on="focus" {
                    match { process = "code" }
                    do { toggle-floating }
                }
            }
            """);

        Assert.True(engine.HasRulesFor(RuleTrigger.OnFocus));
        Assert.Single(engine.For(RuleTrigger.OnFocus));
    }

    [Fact]
    public void RulesKeepTheOrderTheyWereWrittenIn()
    {
        // Rules are applied in sequence and later ones can undo earlier ones, so the
        // order in the file is part of their meaning.
        RuleEngine engine = Load("""
            rules {
                rule "first"  { match { process = "a" } do { float } }
                rule "second" { match { process = "b" } do { tile } }
                rule "third"  { match { process = "c" } do { float } }
            }
            """);

        Assert.Equal(
            ExpectedOrder,
            engine.For(RuleTrigger.OnManage).Select(rule => rule.Name));
    }

    private static readonly string[] ExpectedOrder = ["first", "second", "third"];

    [Fact]
    public void AnEngineThatHasLoadedNothingAnswersEverythingEmpty()
    {
        var engine = new RuleEngine();

        Assert.False(engine.HasRulesFor(RuleTrigger.OnManage));
        Assert.Empty(engine.For(RuleTrigger.OnManage));
        Assert.False(engine.ShouldIgnore(Window()));
        Assert.False(engine.ShouldForceManage(Window()));
    }

    [Fact]
    public void ReloadingReplacesTheIndexRatherThanAddingToIt()
    {
        var engine = new RuleEngine();

        engine.Load(ConfigLoader.Load("""
            rules { rule "gone" { match { process = "chrome" } do { ignore } } }
            """).Config);

        Assert.True(engine.ShouldIgnore(Window(process: "chrome")));

        engine.Load(ConfigLoader.Load("rules { }").Config);

        // Deleting a rule and reloading has to actually delete it - the whole point of
        // the reload path forgetting its cached verdicts.
        Assert.False(engine.ShouldIgnore(Window(process: "chrome")));
        Assert.Empty(engine.For(RuleTrigger.OnManage));
    }

    // ---- ignore and manage -------------------------------------------------

    [Fact]
    public void AnIgnoreRuleIsFoundOnlyForWindowsItMatches()
    {
        RuleEngine engine = Load("""
            rules {
                rule "ignore chrome" {
                    match { process = "chrome" }
                    do { ignore }
                }
            }
            """);

        Assert.True(engine.ShouldIgnore(Window(process: "chrome")));
        Assert.False(engine.ShouldIgnore(Window(process: "notepad")));
    }

    [Fact]
    public void AManageRuleOverridesTheBuiltInFilter()
    {
        RuleEngine engine = Load("""
            rules {
                rule "whatsapp" {
                    match { process = "WhatsApp" }
                    do { manage }
                }
            }
            """);

        Assert.True(engine.ShouldForceManage(Window(process: "WhatsApp")));
        Assert.False(engine.ShouldForceManage(Window(process: "notepad")));

        // And it is not an ignore rule, which is the other half of the same question.
        Assert.False(engine.ShouldIgnore(Window(process: "WhatsApp")));
    }

    [Fact]
    public void OnlyManageTriggeredRulesDecideAdoption()
    {
        // Adoption happens before a window has a stable title or focus, so a rule on
        // a later trigger cannot be allowed to answer that question.
        RuleEngine engine = Load("""
            rules {
                rule "later" on="title-change" {
                    match { process = "chrome" }
                    do { ignore }
                }
            }
            """);

        Assert.False(engine.ShouldIgnore(Window(process: "chrome")));
    }

    [Fact]
    public void ARuleWithSeveralCommandsIsStillFoundByTheOneThatMatters()
    {
        RuleEngine engine = Load("""
            rules {
                rule "several" {
                    match { process = "chrome" }
                    do {
                        move --workspace "2"
                        ignore
                    }
                }
            }
            """);

        Assert.True(engine.ShouldIgnore(Window(process: "chrome")));
    }

    // ---- matching ----------------------------------------------------------

    [Fact]
    public void MatchesUsesTheAppDefinitionsFromTheSameConfig()
    {
        // Rules can match by app name, and the definitions live in a different section.
        // Loading the two separately is how they would silently stop matching.
        RuleEngine engine = Load("""
            app "browser" { process = "chrome" }

            rules {
                rule "ignore browsers" {
                    match { app "browser" }
                    do { ignore }
                }
            }
            """);

        WindowRule rule = Assert.Single(engine.For(RuleTrigger.OnManage));

        Assert.True(engine.Matches(rule, Window(process: "chrome")));
        Assert.False(engine.Matches(rule, Window(process: "firefox")));
        Assert.True(engine.ShouldIgnore(Window(process: "chrome")));
    }

    [Fact]
    public void MatchesAnswersPerRuleRatherThanForTheWholeSet()
    {
        // ApplyRules walks the rules for a trigger and asks about each one, so this is
        // the call that decides whether a rule's commands run at all.
        RuleEngine engine = Load("""
            rules {
                rule "chrome" on="focus" { match { process = "chrome" } do { float } }
                rule "code"   on="focus" { match { process = "code" }   do { tile } }
            }
            """);

        IReadOnlyList<WindowRule> rules = engine.For(RuleTrigger.OnFocus);

        Assert.Equal(2, rules.Count);
        Assert.True(engine.Matches(rules[0], Window(process: "chrome")));
        Assert.False(engine.Matches(rules[1], Window(process: "chrome")));
    }

    [Fact]
    public void TitleAndClassAreMatchableAsWellAsProcess()
    {
        RuleEngine engine = Load("""
            rules {
                rule "by title" { match { title = "Save As" } do { ignore } }
                rule "by class" { match { class = "Shell_TrayWnd" } do { ignore } }
            }
            """);

        Assert.True(engine.ShouldIgnore(Window(title: "Save As")));
        Assert.True(engine.ShouldIgnore(Window(className: "Shell_TrayWnd")));
        Assert.False(engine.ShouldIgnore(Window(title: "Untitled", className: "Window")));
    }
}
