using Shubbak.Core.Animation;
using Shubbak.Core.Commands;
using Shubbak.Core.Wm;

namespace Shubbak.Config.Tests;

/// <summary>
/// Reading contexts: named conditions on the desktop that layer overrides on the
/// configuration while they hold.
/// </summary>
/// <remarks>
/// <para>
/// The loader cannot know what the desktop looks like, so what it checks is what it
/// can: that every condition is one the daemon knows how to ask, that every name a
/// condition or an effect refers to was declared, that a context cannot decide
/// itself, and that the overrides read as deltas rather than resets.
/// </para>
/// <para>
/// The cascade is tested here too, because it is pure: "what does the config look like
/// while presenting and docked" is a question that needs no desktop to answer.
/// </para>
/// </remarks>
public sealed class ContextDefinitionTests
{
    private static ConfigLoadResult Load(string source) => ConfigLoader.Load(source);

    private const string Preamble = """
        app "slides" { title ~= "Slide Show" }
        monitor "dell-left" { path *= "UID4355" }
        monitor "projector" { !internal }
        workspaces {
            workspace "1"
            workspace ";" display-name="Slides"
        }
        """;

    // ---- the shape ---------------------------------------------------------

    [Fact]
    public void AContextReadsEveryKindOfConditionAndEffect()
    {
        ConfigLoadResult result = Load(Preamble + """
            contexts {
                context "presenting" {
                    when { window app="slides" }
                    when { system-state "presenting" "fullscreen-app" }
                    linger 500

                    gaps { inner 0; outer { top 0 } }
                    window-effects { border #false }
                    animation { enabled #false }
                    bindings { bind "alt+shift+q" { } }
                    rules {
                        rule "pop-ups off stage" {
                            match { process ~= "Teams" }
                            do { move --workspace "1" }
                        }
                    }
                    workspaces { workspace ";" monitor="projector" }
                    on-enter { focus --workspace ";" }
                    on-exit { wm-redraw }
                }
            }
            """);

        Assert.Empty(result.Diagnostics);

        ContextDefinition presenting = Assert.Single(result.Config.Contexts);

        Assert.Equal("presenting", presenting.Name);
        Assert.False(presenting.IsExternal);
        Assert.Equal(TimeSpan.FromMilliseconds(500), presenting.Linger);

        Assert.Equal(2, presenting.When.Count);
        var window = Assert.IsType<WindowCondition>(Assert.Single(presenting.When[0]));
        Assert.Equal(WindowConditionKind.Present, window.Kind);
        Assert.Equal("slides", window.App);
        var state = Assert.IsType<SystemStateCondition>(Assert.Single(presenting.When[1]));
        Assert.Equal([UserActivity.Presenting, UserActivity.FullScreenApp], state.AnyOf);

        ContextEffects effects = presenting.Effects;
        Assert.True(effects.Any);
        Assert.Equal(0, effects.Gaps!.Inner);
        Assert.Equal(0, effects.Gaps.Top);
        Assert.Null(effects.Gaps.Left);
        Assert.False(effects.Effects!.Border);
        Assert.Null(effects.Effects.FocusedColour);
        Assert.False(effects.Animation!.Enabled);

        Keybinding disarmed = Assert.Single(effects.Bindings);
        Assert.Empty(disarmed.Commands);

        Assert.Equal("pop-ups off stage", Assert.Single(effects.Rules).Name);
        WorkspaceHome home = Assert.Single(effects.Workspaces);
        Assert.Equal(";", home.Workspace);
        Assert.Equal("projector", home.MonitorName);
        Assert.IsType<FocusWorkspaceCommand>(Assert.Single(effects.OnEnter));
        Assert.IsType<RedrawCommand>(Assert.Single(effects.OnExit));
    }

    [Fact]
    public void AContextWithNoWhenIsExternalAndNotAMistake()
    {
        // The extension primitive. A process that watches something the window manager
        // does not sets this over the pipe; the file says what it does.
        ConfigLoadResult result = Load("""
            contexts { context "meeting" { } }
            """);

        Assert.Empty(result.Diagnostics);

        ContextDefinition meeting = Assert.Single(result.Config.Contexts);
        Assert.True(meeting.IsExternal);
        Assert.False(meeting.Effects.Any);
        Assert.Equal(ContextDefinition.DefaultLinger, meeting.Linger);
    }

    [Fact]
    public void ContextsIsNotAnUnknownSectionAndUnknownSettingsInsideAreNamed()
    {
        ConfigLoadResult result = Load("""
            contexts { context "x" { lingr 500 } }
            """);

        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "SHB0427");

        Diagnostic warning = Assert.Single(result.Diagnostics, d => d.Code == "SHB0428");
        Assert.Contains("lingr", warning.Message, StringComparison.Ordinal);
        Assert.Contains("linger", warning.Hint!, StringComparison.Ordinal);
    }

    // ---- conditions ----------------------------------------------------------

    [Fact]
    public void EveryConditionParsesAndDescribesItself()
    {
        ConfigLoadResult result = Load(Preamble + """
            contexts {
                context "a" { when { context "b" } }
                context "b" {
                    when {
                        window app="slides"
                        focused { process = "POWERPNT" }
                        fullscreen
                        !fullscreen app="slides"
                        workspace active=";"
                        workspace focused="1"
                        monitors count=2
                        monitors min=1 max=3
                        monitor present="dell-left"
                        monitor absent="projector"
                        display-topology "extend" "clone"
                        remote-session
                        !remote-session
                        remote-session #false
                        system-state "presenting"
                        !context "a"
                    }
                }
            }
            """);

        // The a<->b pair is a cycle: b refers to a, a refers to b. One edge is dropped.
        Assert.Single(result.Diagnostics, d => d.Code == "SHB0450");

        ContextDefinition b = result.Config.Contexts[1];
        string[] described = [.. b.When[0].Select(c => c.Describe())];

        Assert.Contains("window app=\"slides\"", described);
        Assert.Contains("focused { process=\"POWERPNT\" }", described);
        Assert.Contains("fullscreen", described);
        Assert.Contains("!fullscreen app=\"slides\"", described);
        Assert.Contains("workspace active=\";\"", described);
        Assert.Contains("workspace focused=\"1\"", described);
        Assert.Contains("monitors count=2", described);
        Assert.Contains("monitors min=1 max=3", described);
        Assert.Contains("monitor present=\"dell-left\"", described);
        Assert.Contains("monitor absent=\"projector\"", described);
        Assert.Contains("display-topology \"extend\" \"clone\"", described);
        Assert.Contains("remote-session", described);

        // Three ways of saying "not remote": !remote-session, remote-session #false.
        Assert.Equal(2, described.Count(d => d == "!remote-session"));
        Assert.Contains("system-state \"presenting\"", described);
    }

    [Fact]
    public void AnUnknownConditionIsNamedWithASuggestion()
    {
        ConfigLoadResult result = Load("""
            contexts { context "x" { when { monitro count=2 } } }
            """);

        Diagnostic error = Assert.Single(result.Diagnostics, d => d.Code == "SHB0446");
        Assert.Contains("monitro", error.Message, StringComparison.Ordinal);
        Assert.Contains("monitors", error.Hint!, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyWhenBlockIsAnErrorRatherThanAlwaysTrue()
    {
        ConfigLoadResult result = Load("""
            contexts { context "x" { when { } } }
            """);

        Assert.Single(result.Diagnostics, d => d.Code == "SHB0449");

        // The block is dropped, which leaves the context external rather than
        // permanently on.
        Assert.True(Assert.Single(result.Config.Contexts).IsExternal);
    }

    [Theory]
    [InlineData("window app=\"slide\"", "SHB0447", "slides")]
    [InlineData("monitor present=\"projecter\"", "SHB0447", "projector")]
    [InlineData("window", "SHB0448", "app=")]
    [InlineData("focused", "SHB0448", "app=")]
    [InlineData("workspace", "SHB0448", "active=")]
    [InlineData("workspace active=\"1\" focused=\"1\"", "SHB0448", "active=")]
    [InlineData("monitors", "SHB0448", "count=")]
    [InlineData("monitors count=-1", "SHB0448", "zero or more")]
    [InlineData("monitor", "SHB0448", "present=")]
    [InlineData("display-topology \"mirror\"", "SHB0448", "extend")]
    [InlineData("display-topology", "SHB0448", "extend")]
    [InlineData("system-state \"sleeping\"", "SHB0448", "presenting")]
    [InlineData("remote-session \"yes\"", "SHB0419", null)]
    [InlineData("context", "SHB0448", "meeting")]
    [InlineData("context \"nope\"", "SHB0447", null)]
    public void ABadConditionIsReportedAndDropped(string condition, string code, string? hint)
    {
        ConfigLoadResult result = Load(Preamble + $$"""
            contexts { context "x" { when { {{condition}} } } }
            """);

        Diagnostic diagnostic = Assert.Single(result.Diagnostics, d => d.Code == code);

        if (hint is not null) Assert.Contains(hint, diagnostic.Hint!, StringComparison.OrdinalIgnoreCase);

        // Dropped: the block is left with nothing and so goes too (with its own error
        // about being empty only when it started out that way, which it did not).
        Assert.True(Assert.Single(result.Config.Contexts).IsExternal);
    }

    [Fact]
    public void FullscreenAloneIsAllowedBecauseItMeansSomething()
    {
        ConfigLoadResult result = Load("""
            contexts { context "video" { when { fullscreen } } }
            """);

        Assert.Empty(result.Diagnostics);

        var condition = Assert.IsType<WindowCondition>(Assert.Single(result.Config.Contexts[0].When[0]));
        Assert.True(condition.IsUnconstrained);
    }

    [Fact]
    public void AContextMayReferToOneDeclaredBelowIt()
    {
        ConfigLoadResult result = Load("""
            contexts {
                context "docked-meeting" { when { context "meeting"; monitors min=2 } }
                context "meeting" { }
            }
            """);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(2, result.Config.Contexts[0].When[0].Count);
    }

    [Fact]
    public void ACycleIsBrokenAtTheReferenceThatClosesIt()
    {
        ConfigLoadResult result = Load("""
            contexts {
                context "a" { when { context "b" } }
                context "b" { when { context "c" } }
                context "c" { when { context "a" } }
            }
            """);

        Diagnostic error = Assert.Single(result.Diagnostics, d => d.Code == "SHB0450");
        Assert.Contains("depends on", error.Message, StringComparison.Ordinal);

        // Exactly one edge removed; what remains is a chain, which is decidable.
        int references = result.Config.Contexts.Sum(c => c.AllConditions.OfType<ContextReferenceCondition>().Count());
        Assert.Equal(2, references);
    }

    [Fact]
    public void ASelfReferenceIsACycleOfOne()
    {
        ConfigLoadResult result = Load("""
            contexts { context "a" { when { context "a" } } }
            """);

        Assert.Single(result.Diagnostics, d => d.Code == "SHB0450");
        Assert.True(Assert.Single(result.Config.Contexts).IsExternal);
    }

    // ---- effects -------------------------------------------------------------

    [Fact]
    public void ADuplicateKeepsTheFirstAndSaysSo()
    {
        ConfigLoadResult result = Load("""
            contexts {
                context "x" { linger 100 }
                context "x" { linger 200 }
            }
            """);

        Assert.Single(result.Diagnostics, d => d.Code == "SHB0445");
        Assert.Equal(TimeSpan.FromMilliseconds(100), Assert.Single(result.Config.Contexts).Linger);
    }

    [Fact]
    public void ANegativeLingerIsRefused()
    {
        ConfigLoadResult result = Load("""
            contexts { context "x" { linger -5 } }
            """);

        Assert.Single(result.Diagnostics, d => d.Code == "SHB0448");
        Assert.Equal(ContextDefinition.DefaultLinger, Assert.Single(result.Config.Contexts).Linger);
    }

    [Fact]
    public void AnEmptyBindingIsLegalInsideAContextAndNowhereElse()
    {
        ConfigLoadResult inContext = Load("""
            contexts { context "x" { bindings { bind "alt+shift+q" { } } } }
            """);

        Assert.DoesNotContain(inContext.Diagnostics, d => d.Code == "SHB0408");
        Assert.Empty(Assert.Single(inContext.Config.Contexts[0].Effects.Bindings).Commands);

        ConfigLoadResult atTopLevel = Load("""
            keybindings { bind "alt+shift+q" { } }
            """);

        Assert.Single(atTopLevel.Diagnostics, d => d.Code == "SHB0408");
        Assert.Empty(atTopLevel.Config.Keybindings);
    }

    [Fact]
    public void AWorkspaceHomeMustNameADeclaredWorkspaceAndAMonitor()
    {
        ConfigLoadResult undeclared = Load(Preamble + """
            contexts { context "x" { workspaces { workspace "9" monitor="projector" } } }
            """);

        Diagnostic error = Assert.Single(undeclared.Diagnostics, d => d.Code == "SHB0448");
        Assert.Contains("'9'", error.Message, StringComparison.Ordinal);
        Assert.Empty(undeclared.Config.Contexts[0].Effects.Workspaces);

        ConfigLoadResult noMonitor = Load(Preamble + """
            contexts { context "x" { workspaces { workspace ";" } } }
            """);

        Assert.Single(noMonitor.Diagnostics, d => d.Code == "SHB0448");

        ConfigLoadResult badMonitor = Load(Preamble + """
            contexts { context "x" { workspaces { workspace ";" monitor="projecter" } } }
            """);

        Assert.Single(badMonitor.Diagnostics, d => d.Code == "SHB0442");

        ConfigLoadResult byPosition = Load(Preamble + """
            contexts { context "x" { workspaces { workspace ";" monitor=1 } } }
            """);

        Assert.Empty(byPosition.Diagnostics);
        Assert.Equal(1, Assert.Single(byPosition.Config.Contexts[0].Effects.Workspaces).MonitorIndex);
    }

    [Fact]
    public void ARuleInsideAContextIsCheckedLikeAnyOther()
    {
        ConfigLoadResult result = Load("""
            contexts {
                context "x" {
                    rules { rule "nothing" { match { process = "a" } do { } } }
                }
            }
            """);

        // No commands: the rule is dropped with the same warning a top-level one gets.
        Assert.Single(result.Diagnostics, d => d.Code == "SHB0418");
        Assert.Empty(result.Config.Contexts[0].Effects.Rules);
    }

    // ---- references from commands ---------------------------------------------

    [Fact]
    public void ABindingThatSetsAnUndeclaredContextIsReported()
    {
        ConfigLoadResult result = Load("""
            contexts { context "presenting" { } }
            keybindings { bind "alt+p" { context --toggle "presentng" } }
            """);

        Diagnostic warning = Assert.Single(result.Diagnostics, d => d.Code == "SHB0451");
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("presentng", warning.Message, StringComparison.Ordinal);
        Assert.Contains("presenting", warning.Hint!, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOnEnterThatSetsAnUndeclaredContextIsReportedToo()
    {
        ConfigLoadResult result = Load("""
            contexts {
                context "a" { on-enter { context --set "b" } }
            }
            """);

        Diagnostic warning = Assert.Single(result.Diagnostics, d => d.Code == "SHB0451");
        Assert.Contains("on-enter of context 'a'", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AContextBindingIsCheckedForUndeclaredModesAndMonitors()
    {
        ConfigLoadResult result = Load("""
            contexts {
                context "a" {
                    bindings {
                        bind "alt+1" { wm-enable-binding-mode --name "nope" }
                        bind "alt+2" { move-workspace --monitor "nowhere" }
                    }
                }
            }
            """);

        Assert.Single(result.Diagnostics, d => d.Code == "SHB0434");
        Assert.Single(result.Diagnostics, d => d.Code == "SHB0443");
    }

    // ---- the cascade ----------------------------------------------------------

    private static ShubbakConfig Base() => Load("""
        gaps { inner 6; outer { top 26; right 4; bottom 4; left 4 } }
        window-effects { border #true; focused-colour "#8dbcff"; unfocused-colour "#a1a1a1" }
        animation { enabled #true; window-move duration=140 curve="ease-out" }
        monitor "projector" { !internal }
        workspaces { workspace "1" monitor=0; workspace ";" monitor=0 }
        rules { rule "base" { match { process = "a" } do { float } } }
        contexts {
            context "presenting" {
                gaps { inner 0; outer { top 0 } }
                window-effects { border #false }
                animation { enabled #false; window-move duration=0 }
                bindings { bind "alt+shift+q" { }; bind "alt+p" { wm-redraw } }
                rules { rule "stage" { match { process = "b" } do { float } } }
                workspaces { workspace ";" monitor="projector" }
            }
            context "docked" {
                gaps { inner 12 }
                bindings { bind "alt+p" { wm-reload-config } }
                workspaces { workspace "1" monitor=1 }
            }
        }
        """).Config;

    [Fact]
    public void NoActiveContextsIsTheBaseUntouched()
    {
        ShubbakConfig config = Base();

        EffectiveConfig effective = ContextCascade.Apply(config, []);

        Assert.Same(config, effective.Config);
        Assert.Empty(effective.OverlayBindings);
    }

    [Fact]
    public void AnOverrideIsADeltaNotAReset()
    {
        ShubbakConfig config = Base();

        EffectiveConfig effective = ContextCascade.Apply(config, [config.Contexts[0]]);

        // Written: inner and top. Not written: the other three sides, kept.
        Assert.Equal(0, effective.Config.InnerGap);
        Assert.Equal(0, effective.Config.OuterGap.Top);
        Assert.Equal(4, effective.Config.OuterGap.Left);
        Assert.Equal(4, effective.Config.OuterGap.Right);

        // Border off, colours kept for when it comes back.
        Assert.False(effective.Config.Effects.Enabled);
        Assert.Equal("#8dbcff", effective.Config.Effects.FocusedColour);

        // Animation off, the move profile's duration zeroed and its curve kept.
        Assert.False(effective.Config.Animation.Enabled);
        Assert.Equal(TimeSpan.Zero, effective.Config.Animation.WindowMove.Duration);
        Assert.Equal(Easing.EaseOut, effective.Config.Animation.WindowMove.Curve);
    }

    [Fact]
    public void LaterContextsWinAndRulesAccumulate()
    {
        ShubbakConfig config = Base();

        EffectiveConfig effective = ContextCascade.Apply(config, [config.Contexts[0], config.Contexts[1]]);

        // docked comes after presenting and sets inner 12 over presenting's 0.
        Assert.Equal(12, effective.Config.InnerGap);
        Assert.Equal(0, effective.Config.OuterGap.Top);

        // Rules: base, then presenting's, in that order - a context's rule is consulted
        // after the file's.
        Assert.Equal(["base", "stage"], effective.Config.Rules.Select(r => r.Name));

        // Bindings: both overlays, later last, so a table built from them in order has
        // docked's alt+p.
        Assert.Equal(3, effective.OverlayBindings.Count);
        Assert.IsType<ReloadConfigCommand>(effective.OverlayBindings[2].Commands[0]);

        // Homes: each context moved a different workspace.
        Assert.Equal("projector", effective.Config.Workspaces.Single(w => w.Name == ";").BindToMonitorName);
        Assert.Null(effective.Config.Workspaces.Single(w => w.Name == ";").BindToMonitor);
        Assert.Equal(1, effective.Config.Workspaces.Single(w => w.Name == "1").BindToMonitor);
    }

    [Fact]
    public void TheOrderIsDeclarationOrderWhateverOrderTheyWereActivatedIn()
    {
        // The caller passes the active contexts in declaration order; this pins that the
        // cascade honours whatever order it is given, so the caller's contract is the
        // whole story.
        ShubbakConfig config = Base();

        EffectiveConfig reversed = ContextCascade.Apply(config, [config.Contexts[1], config.Contexts[0]]);

        Assert.Equal(0, reversed.Config.InnerGap);
    }

    [Fact]
    public void TheFileIsNotChangedByTheCascade()
    {
        ShubbakConfig config = Base();

        ContextCascade.Apply(config, [config.Contexts[0]]);

        Assert.Equal(6, config.InnerGap);
        Assert.True(config.Effects.Enabled);
        Assert.Single(config.Rules);
        Assert.Equal(0, config.Workspaces.Single(w => w.Name == ";").BindToMonitor);
    }
}
