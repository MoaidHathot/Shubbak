using System.Diagnostics;
using Shubbak.Config;
using Shubbak.Core.Commands;
using Shubbak.Core.Geometry;
using Shubbak.Core.Tree;
using Shubbak.Core.Wm;
using Shubbak.Ipc;

namespace Shubbak.Wm.Tests;

/// <summary>
/// Deciding which contexts hold.
/// </summary>
/// <remarks>
/// <para>
/// The rules, each pinned below: a context holds when any block of its conditions
/// holds; a pin beats the conditions either way; a context whose conditions have just
/// stopped holding lingers before letting go; a time-to-live takes a pin off by itself;
/// a lease takes it off when the connection that made it closes; a context may refer to
/// one declared after it and the answer is the same as if it had been declared before.
/// </para>
/// <para>
/// And the performance rules: nothing is evaluated until something changed, and an
/// evaluation that finds nothing changed allocates nothing.
/// </para>
/// </remarks>
public sealed class ContextEngineTests
{
    private static long Ms(double milliseconds) => (long)(milliseconds / 1000 * Stopwatch.Frequency);

    private static readonly long T0 = Ms(100_000);

    private static readonly WindowAttributes Slides = new("PowerPoint Slide Show - deck.pptx", "screenClass", "POWERPNT", null);
    private static readonly WindowAttributes Editor = new("deck.pptx - PowerPoint", "PPTFrameClass", "POWERPNT", null);

    private static (ContextEngine Engine, WindowManager Wm) Load(string source, int monitors = 2)
    {
        ConfigLoadResult result = ConfigLoader.Load(source);

        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);

        WindowManager wm = new();

        for (int i = 0; i < monitors; i++)
        {
            var bounds = new Rect(i * 1920, 0, 1920, 1080);
            var monitor = new MonitorNode($"\\\\.\\DISPLAY{i + 1}", bounds, bounds, 96) { IsPrimary = i == 0 };
            if (i == 1) monitor.Names = ["dell-right"];
            wm.AddMonitor(monitor);
        }

        wm.AddWorkspace(new WorkspaceNode("1"), wm.Root.Monitors[0]);
        wm.AddWorkspace(new WorkspaceNode(";"), wm.Root.Monitors[0]);
        wm.ActivateWorkspace(wm.Root.Monitors[0].Workspaces[0]);

        var engine = new ContextEngine();
        engine.Load(result.Config);

        return (engine, wm);
    }

    private static ContextFacts Facts(
        WindowManager wm,
        WindowRegistry? windows = null,
        DisplayTopologyKind topology = DisplayTopologyKind.Extend,
        bool remote = false,
        UserActivity? activity = UserActivity.Ordinary) =>
        new(wm.Root, wm.FocusedWorkspace, windows ?? new WindowRegistry(), topology, remote, activity);

    private const string Presenting = """
        app "slides" { title ~= "Slide Show" }
        contexts {
            context "presenting" {
                when { window app="slides" }
                linger 300
            }
        }
        """;

    // ---- detection and linger --------------------------------------------------

    [Fact]
    public void AWindowAppearingTurnsTheContextOnAndDisappearingTurnsItOffAfterTheLinger()
    {
        (ContextEngine engine, WindowManager wm) = Load(Presenting);
        ContextFacts facts = Facts(wm);

        // Nothing yet: evaluated once (the load made it dirty), nothing holds.
        Assert.Empty(engine.Evaluate(facts, T0));
        Assert.False(engine.IsActive("presenting"));

        // The slide show window shows up.
        engine.WindowSeen(0x100, Slides);
        Assert.True(engine.Dirty);

        ContextTransition on = Assert.Single(engine.Evaluate(facts, T0 + Ms(10)));
        Assert.Equal("presenting", on.Name);
        Assert.True(on.Active);
        Assert.Equal("detected", on.Source);
        Assert.Equal("window app=\"slides\"", on.Reason);

        // It goes, and for the linger the context stays on with a deadline set.
        engine.WindowGone(0x100);
        Assert.Empty(engine.Evaluate(facts, T0 + Ms(20)));
        Assert.True(engine.IsActive("presenting"));
        Assert.Equal(T0 + Ms(10) + Ms(300), engine.NextDeadlineTicks);

        // Comes back inside the linger: nothing to announce.
        engine.WindowSeen(0x101, Slides);
        Assert.Empty(engine.Evaluate(facts, T0 + Ms(200)));

        // Goes for good.
        engine.WindowGone(0x101);
        Assert.Empty(engine.Evaluate(facts, T0 + Ms(210)));

        // Not dirty and before the deadline: nothing happens, deliberately.
        Assert.Empty(engine.Evaluate(facts, T0 + Ms(400)));
        Assert.True(engine.IsActive("presenting"));

        // At the deadline, off.
        ContextTransition off = Assert.Single(engine.Evaluate(facts, T0 + Ms(200) + Ms(300)));
        Assert.False(off.Active);
        Assert.Equal("detected", off.Source);
        Assert.Equal(long.MaxValue, engine.NextDeadlineTicks);
    }

    [Fact]
    public void AWindowThatDoesNotMatchIsNotCounted()
    {
        (ContextEngine engine, WindowManager wm) = Load(Presenting);
        engine.Evaluate(Facts(wm), T0);

        engine.WindowSeen(0x100, Editor);

        // The editor is not the slide show. Nothing changed in the set, so nothing is
        // even dirty.
        Assert.False(engine.Dirty);
        Assert.Empty(engine.Evaluate(Facts(wm), T0 + Ms(1)));
    }

    [Fact]
    public void ATitleChangeCanTakeAWindowInOrOutOfACondition()
    {
        // PowerPoint keeps one window and retitles it as the show starts.
        (ContextEngine engine, WindowManager wm) = Load(Presenting);
        engine.Evaluate(Facts(wm), T0);

        engine.WindowSeen(0x100, Editor);
        Assert.Empty(engine.Evaluate(Facts(wm), T0 + Ms(1)));

        engine.WindowSeen(0x100, Slides);
        Assert.True(Assert.Single(engine.Evaluate(Facts(wm), T0 + Ms(2))).Active);

        engine.WindowSeen(0x100, Editor);
        Assert.Empty(engine.Evaluate(Facts(wm), T0 + Ms(3)));
        Assert.False(Assert.Single(engine.Evaluate(Facts(wm), T0 + Ms(3) + Ms(300))).Active);
    }

    [Fact]
    public void PruningDropsAHandleWhoseWindowIsGone()
    {
        (ContextEngine engine, WindowManager wm) = Load(Presenting);
        engine.WindowSeen(0x100, Slides);
        engine.Evaluate(Facts(wm), T0);
        Assert.True(engine.TracksAnyWindow);

        engine.Prune(_ => false);

        Assert.True(engine.Dirty);
        Assert.False(engine.TracksAnyWindow);
    }

    // ---- pins ---------------------------------------------------------------------

    [Fact]
    public void APinOnBeatsConditionsThatDoNotHold()
    {
        (ContextEngine engine, WindowManager wm) = Load(Presenting);
        engine.Evaluate(Facts(wm), T0);

        var origin = new PinOrigin("shubbak.exe (pid 42)", null);
        Assert.True(engine.Pin("presenting", ContextAction.Set, null, false, origin, T0).Accepted);

        ContextTransition on = Assert.Single(engine.Evaluate(Facts(wm), T0 + Ms(1)));
        Assert.True(on.Active);
        Assert.Equal("pinned", on.Source);
        Assert.Equal("set by shubbak.exe (pid 42)", on.Reason);

        // The window coming and going changes nothing while the pin holds.
        engine.WindowSeen(0x100, Slides);
        Assert.Empty(engine.Evaluate(Facts(wm), T0 + Ms(2)));
        engine.WindowGone(0x100);
        Assert.Empty(engine.Evaluate(Facts(wm), T0 + Ms(3) + Ms(1000)));
        Assert.True(engine.IsActive("presenting"));

        // --auto hands it back to the conditions, which do not hold: off at once, no
        // linger, because the last time a condition held is long past.
        Assert.True(engine.Pin("presenting", ContextAction.Auto, null, false, origin, T0 + Ms(2000)).Accepted);
        Assert.False(Assert.Single(engine.Evaluate(Facts(wm), T0 + Ms(2000))).Active);
    }

    [Fact]
    public void APinOffBeatsConditionsThatHold()
    {
        (ContextEngine engine, WindowManager wm) = Load(Presenting);
        engine.WindowSeen(0x100, Slides);
        engine.Evaluate(Facts(wm), T0);
        Assert.True(engine.IsActive("presenting"));

        engine.Pin("presenting", ContextAction.Clear, null, false, PinOrigin.Local, T0 + Ms(1));

        ContextTransition off = Assert.Single(engine.Evaluate(Facts(wm), T0 + Ms(1)));
        Assert.False(off.Active);
        Assert.Equal("pinned", off.Source);
        Assert.Equal("cleared by a keybinding or rule", off.Reason);

        // Back to auto: the window is still there, so on again, immediately.
        engine.Pin("presenting", ContextAction.Auto, null, false, PinOrigin.Local, T0 + Ms(2));
        Assert.True(Assert.Single(engine.Evaluate(Facts(wm), T0 + Ms(2))).Active);
    }

    [Fact]
    public void ToggleFlipsWhateverItIsNow()
    {
        (ContextEngine engine, WindowManager wm) = Load(Presenting);
        engine.Evaluate(Facts(wm), T0);

        engine.Pin("presenting", ContextAction.Toggle, null, false, PinOrigin.Local, T0);
        Assert.True(Assert.Single(engine.Evaluate(Facts(wm), T0)).Active);

        engine.Pin("presenting", ContextAction.Toggle, null, false, PinOrigin.Local, T0);
        Assert.False(Assert.Single(engine.Evaluate(Facts(wm), T0)).Active);
    }

    [Fact]
    public void ATimeToLiveTakesThePinOffByItself()
    {
        (ContextEngine engine, WindowManager wm) = Load(Presenting);
        engine.Evaluate(Facts(wm), T0);

        engine.Pin("presenting", ContextAction.Set, TimeSpan.FromSeconds(5), false, PinOrigin.Local, T0);

        Assert.True(Assert.Single(engine.Evaluate(Facts(wm), T0)).Active);
        Assert.Equal(T0 + Ms(5000), engine.NextDeadlineTicks);

        // Before the deadline, untouched and not even evaluated.
        Assert.Empty(engine.Evaluate(Facts(wm), T0 + Ms(4999)));

        // At it, the pin lapses and the conditions - which do not hold - decide.
        ContextTransition off = Assert.Single(engine.Evaluate(Facts(wm), T0 + Ms(5000)));
        Assert.False(off.Active);
        Assert.Equal("detected", off.Source);
    }

    [Fact]
    public void ALeaseDiesWithItsConnection()
    {
        (ContextEngine engine, WindowManager wm) = Load(Presenting);
        engine.Evaluate(Facts(wm), T0);

        var rasid = new PinOrigin("rasid.exe (pid 7)", ConnectionId: 12);
        Assert.True(engine.Pin("presenting", ContextAction.Set, null, lease: true, rasid, T0).Accepted);
        Assert.True(Assert.Single(engine.Evaluate(Facts(wm), T0)).Active);

        // Another connection going away releases nothing.
        Assert.Empty(engine.ReleaseLeases(99));
        Assert.Empty(engine.Evaluate(Facts(wm), T0 + Ms(1)));

        // The provider's connection going away releases the pin.
        Assert.Equal(["presenting"], engine.ReleaseLeases(12));
        Assert.False(Assert.Single(engine.Evaluate(Facts(wm), T0 + Ms(2))).Active);
    }

    [Fact]
    public void ALeaseNeedsAConnection()
    {
        (ContextEngine engine, WindowManager wm) = Load(Presenting);

        PinOutcome outcome = engine.Pin("presenting", ContextAction.Set, null, lease: true, PinOrigin.Local, T0);

        Assert.False(outcome.Accepted);
        Assert.Contains("connection that stays open", outcome.Refusal, StringComparison.Ordinal);
        Assert.Contains("--ttl", outcome.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownContextIsRefusedWithTheDeclaredOnes()
    {
        (ContextEngine engine, _) = Load(Presenting);

        PinOutcome outcome = engine.Pin("presentng", ContextAction.Set, null, false, PinOrigin.Local, T0);

        Assert.False(outcome.Accepted);
        Assert.Contains("presentng", outcome.Refusal, StringComparison.Ordinal);
        Assert.Contains("presenting", outcome.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExternalContextIsOnlyEverPinned()
    {
        (ContextEngine engine, WindowManager wm) = Load("""
            contexts { context "meeting" { } }
            """);

        Assert.Empty(engine.Evaluate(Facts(wm), T0));
        Assert.False(engine.IsActive("meeting"));

        engine.Pin("meeting", ContextAction.Set, TimeSpan.FromSeconds(5), false, new PinOrigin("rasid.exe (pid 7)", 3), T0);
        Assert.True(Assert.Single(engine.Evaluate(Facts(wm), T0)).Active);

        ContextTransition off = Assert.Single(engine.Evaluate(Facts(wm), T0 + Ms(5000)));
        Assert.False(off.Active);
        Assert.Equal("nothing has set it", off.Reason);
    }

    // ---- references --------------------------------------------------------------

    [Fact]
    public void AForwardReferenceIsDecidedInTheSameEvaluation()
    {
        (ContextEngine engine, WindowManager wm) = Load("""
            contexts {
                context "docked-meeting" { when { context "meeting"; monitors min=2 } linger 0 }
                context "meeting" { }
            }
            """);

        engine.Evaluate(Facts(wm), T0);

        engine.Pin("meeting", ContextAction.Set, null, false, PinOrigin.Local, T0);

        IReadOnlyList<ContextTransition> transitions = engine.Evaluate(Facts(wm), T0);

        // Both, in one pass, in declaration order.
        Assert.Equal(["docked-meeting", "meeting"], transitions.Select(t => t.Name));
        Assert.All(transitions, t => Assert.True(t.Active));

        engine.Pin("meeting", ContextAction.Auto, null, false, PinOrigin.Local, T0 + Ms(1));

        transitions = engine.Evaluate(Facts(wm), T0 + Ms(1));
        Assert.Equal(2, transitions.Count);
        Assert.All(transitions, t => Assert.False(t.Active));
    }

    // ---- every condition --------------------------------------------------------------

    [Fact]
    public void MonitorConditionsReadTheTree()
    {
        (ContextEngine engine, WindowManager wm) = Load("""
            monitor "dell-right" { path *= "UID4357" }
            contexts {
                context "two" { when { monitors count=2 } linger 0 }
                context "many" { when { monitors min=3 } linger 0 }
                context "docked" { when { monitor present="dell-right" } linger 0 }
                context "undocked" { when { monitor absent="dell-right" } linger 0 }
            }
            """);

        engine.Evaluate(Facts(wm), T0);

        Assert.True(engine.IsActive("two"));
        Assert.False(engine.IsActive("many"));
        Assert.True(engine.IsActive("docked"));
        Assert.False(engine.IsActive("undocked"));

        wm.RemoveMonitor(wm.Root.Monitors[1]);
        engine.MarkDirty();
        engine.Evaluate(Facts(wm), T0 + Ms(1));

        Assert.False(engine.IsActive("two"));
        Assert.False(engine.IsActive("docked"));
        Assert.True(engine.IsActive("undocked"));
    }

    [Fact]
    public void SessionConditionsReadTheFacts()
    {
        (ContextEngine engine, WindowManager wm) = Load("""
            contexts {
                context "remote" { when { remote-session } linger 0 }
                context "local" { when { !remote-session } linger 0 }
                context "extended" { when { display-topology "extend" "clone" } linger 0 }
                context "showing" { when { system-state "presenting" "fullscreen-app" } linger 0 }
            }
            """);

        engine.Evaluate(Facts(wm, remote: false, topology: DisplayTopologyKind.Extend, activity: UserActivity.Ordinary), T0);

        Assert.False(engine.IsActive("remote"));
        Assert.True(engine.IsActive("local"));
        Assert.True(engine.IsActive("extended"));
        Assert.False(engine.IsActive("showing"));

        engine.MarkDirty();
        engine.Evaluate(Facts(wm, remote: true, topology: DisplayTopologyKind.Internal, activity: UserActivity.FullScreenApp), T0 + Ms(1));

        Assert.True(engine.IsActive("remote"));
        Assert.False(engine.IsActive("local"));
        Assert.False(engine.IsActive("extended"));
        Assert.True(engine.IsActive("showing"));

        // An activity the daemon has not read yet holds nothing, and !remote-session
        // is not affected by it.
        engine.MarkDirty();
        engine.Evaluate(Facts(wm, activity: null), T0 + Ms(2));
        Assert.False(engine.IsActive("showing"));
        Assert.True(engine.IsActive("local"));
    }

    [Fact]
    public void WorkspaceConditionsReadTheTree()
    {
        (ContextEngine engine, WindowManager wm) = Load("""
            contexts {
                context "slides-up" { when { workspace active=";" } linger 0 }
                context "on-slides" { when { workspace focused=";" } linger 0 }
            }
            """);

        engine.Evaluate(Facts(wm), T0);
        Assert.False(engine.IsActive("slides-up"));
        Assert.False(engine.IsActive("on-slides"));

        wm.FocusWorkspace(";");
        engine.MarkDirty();
        engine.Evaluate(Facts(wm), T0 + Ms(1));

        Assert.True(engine.IsActive("slides-up"));
        Assert.True(engine.IsActive("on-slides"));

        // Shown on the other monitor but not focused: active, not focused.
        wm.MoveWorkspaceToMonitor(wm.Root.Monitors[1]);
        wm.FocusWorkspace("1");
        engine.MarkDirty();
        engine.Evaluate(Facts(wm), T0 + Ms(2));

        Assert.True(engine.IsActive("slides-up"));
        Assert.False(engine.IsActive("on-slides"));
    }

    [Fact]
    public void FullscreenReadsTheManagedWindows()
    {
        (ContextEngine engine, WindowManager wm) = Load("""
            app "browser" { process = "firefox" }
            contexts {
                context "video" { when { fullscreen app="browser" } linger 0 }
                context "anything" { when { fullscreen } linger 0 }
            }
            """);

        var windows = new WindowRegistry();
        var firefox = new WindowNode(0x10, new WindowIdentity { Title = "YouTube", ClassName = "MozillaWindowClass", ProcessName = "firefox" });
        var terminal = new WindowNode(0x20, new WindowIdentity { Title = "pwsh", ClassName = "CASCADIA", ProcessName = "WindowsTerminal" });
        windows.Adopt(0x10, firefox);
        windows.Adopt(0x20, terminal);

        engine.Evaluate(Facts(wm, windows), T0);
        Assert.False(engine.IsActive("video"));
        Assert.False(engine.IsActive("anything"));

        // F11 in the terminal: something is full-screen, but not the browser.
        terminal.IsNativeFullscreen = true;
        engine.MarkDirty();
        engine.Evaluate(Facts(wm, windows), T0 + Ms(1));
        Assert.False(engine.IsActive("video"));
        Assert.True(engine.IsActive("anything"));

        // F11 in the browser.
        terminal.IsNativeFullscreen = false;
        firefox.IsNativeFullscreen = true;
        engine.MarkDirty();
        engine.Evaluate(Facts(wm, windows), T0 + Ms(2));
        Assert.True(engine.IsActive("video"));
    }

    [Fact]
    public void FocusedReadsTheForegroundWindow()
    {
        (ContextEngine engine, WindowManager wm) = Load("""
            app "slides" { title ~= "Slide Show" }
            contexts { context "on-stage" { when { focused app="slides" } linger 0 } }
            """);

        Assert.True(engine.HasFocusConditions);
        engine.Evaluate(Facts(wm), T0);

        engine.ForegroundChanged(Slides);
        Assert.True(Assert.Single(engine.Evaluate(Facts(wm), T0)).Active);

        engine.ForegroundChanged(Editor);
        Assert.False(Assert.Single(engine.Evaluate(Facts(wm), T0)).Active);

        engine.ForegroundChanged(null);
        Assert.False(engine.Dirty);
    }

    [Fact]
    public void ANegatedConditionAndABlockOfSeveralAreAnded()
    {
        (ContextEngine engine, WindowManager wm) = Load("""
            app "slides" { title ~= "Slide Show" }
            contexts {
                context "rehearsing" { when { window app="slides"; !remote-session; monitors count=2 } linger 0 }
            }
            """);

        engine.WindowSeen(0x1, Slides);
        engine.Evaluate(Facts(wm, remote: false), T0);
        Assert.True(engine.IsActive("rehearsing"));

        engine.MarkDirty();
        engine.Evaluate(Facts(wm, remote: true), T0 + Ms(1));
        Assert.False(engine.IsActive("rehearsing"));
    }

    // ---- reload -----------------------------------------------------------------------

    [Fact]
    public void AReloadKeepsPinsForContextsThatSurviveAndReportsTheOnesThatDoNot()
    {
        (ContextEngine engine, WindowManager wm) = Load("""
            contexts { context "a" { } context "b" { } }
            """);

        engine.Pin("a", ContextAction.Set, null, false, PinOrigin.Local, T0);
        engine.Pin("b", ContextAction.Set, null, false, PinOrigin.Local, T0);
        engine.Evaluate(Facts(wm), T0);

        IReadOnlyList<string> lost = engine.Load(ConfigLoader.Load("""
            contexts { context "a" { } context "c" { } }
            """).Config);

        Assert.Equal(["b"], lost);

        engine.Evaluate(Facts(wm), T0 + Ms(1));
        Assert.True(engine.IsActive("a"));
        Assert.False(engine.IsActive("c"));
        Assert.Equal(2, engine.Count);
    }

    // ---- the report -------------------------------------------------------------------

    [Fact]
    public void TheReportSaysWhatEveryConditionSawAndWhoPinned()
    {
        (ContextEngine engine, WindowManager wm) = Load("""
            app "slides" { title ~= "Slide Show" }
            contexts {
                context "presenting" {
                    when { window app="slides" }
                    when { system-state "presenting"; monitors count=2 }
                    linger 300
                    gaps { inner 0 }
                    bindings { bind "alt+q" { } }
                    on-enter { wm-redraw }
                }
                context "meeting" { }
            }
            """);

        engine.Evaluate(Facts(wm), T0);
        engine.Pin("meeting", ContextAction.Set, TimeSpan.FromSeconds(10), lease: true, new PinOrigin("rasid.exe (pid 7)", 5), T0);
        engine.Evaluate(Facts(wm), T0 + Ms(100));

        IReadOnlyList<ContextReport> report = engine.Report(Facts(wm), T0 + Ms(1100));

        ContextReport presenting = report[0];
        Assert.False(presenting.Active);
        Assert.False(presenting.External);
        Assert.Equal("no block of conditions holds", presenting.Reason);
        Assert.Null(presenting.Pin);
        Assert.Equal(2, presenting.When.Count);
        Assert.False(presenting.When[0].Holds);
        Assert.Equal("window app=\"slides\"", presenting.When[0].Conditions[0].Text);
        Assert.Equal("no such window", presenting.When[0].Conditions[0].Detail);
        Assert.False(presenting.When[1].Holds);
        Assert.Equal("the shell says ordinary", presenting.When[1].Conditions[0].Detail);
        Assert.True(presenting.When[1].Conditions[1].Holds);
        Assert.Equal("2 attached", presenting.When[1].Conditions[1].Detail);
        Assert.Equal(["gaps", "1 binding(s)", "on-enter"], presenting.Effects);

        ContextReport meeting = report[1];
        Assert.True(meeting.Active);
        Assert.True(meeting.External);
        Assert.Equal("pinned", meeting.Source);
        Assert.Equal("set", meeting.Pin);
        Assert.Equal("rasid.exe (pid 7)", meeting.SetBy);
        Assert.Equal(1100, meeting.SetAgoMs);
        Assert.Equal(8900, meeting.ExpiresInMs);
        Assert.True(meeting.Leased);
        Assert.Empty(meeting.When);
    }

    [Fact]
    public void TheReportShowsALingerWithoutEndingIt()
    {
        (ContextEngine engine, WindowManager wm) = Load(Presenting);
        engine.WindowSeen(0x1, Slides);
        engine.Evaluate(Facts(wm), T0);
        engine.WindowGone(0x1);
        engine.Evaluate(Facts(wm), T0 + Ms(10));

        ContextReport presenting = Assert.Single(engine.Report(Facts(wm), T0 + Ms(110)));

        // The last detection was at T0, the linger is 300, the report is at T0 + 110.
        Assert.True(presenting.Active);
        Assert.Equal(190, presenting.LingerRemainingMs);
        Assert.True(engine.IsActive("presenting"));
    }

    // ---- the performance rules --------------------------------------------------------

    [Fact]
    public void NothingIsEvaluatedUntilSomethingChanges()
    {
        (ContextEngine engine, WindowManager wm) = Load(Presenting);
        engine.Evaluate(Facts(wm), T0);

        Assert.False(engine.Dirty);
        Assert.Equal(long.MaxValue, engine.NextDeadlineTicks);

        // Re-testing a window that is still not the slide show changes no set.
        engine.WindowSeen(0x100, Editor);
        Assert.False(engine.Dirty);
    }

    [Fact]
    public void AnEvaluationThatFindsNothingChangedAllocatesNothing()
    {
        // Rule two. The daemon calls this on every tick that follows a window event,
        // and most of those find every context exactly as it was.
        (ContextEngine engine, WindowManager wm) = Load("""
            app "slides" { title ~= "Slide Show" }
            monitor "dell-right" { path *= "UID4357" }
            contexts {
                context "presenting" { when { window app="slides" } when { system-state "presenting" } }
                context "docked" { when { monitor present="dell-right"; monitors count=2 } }
                context "meeting" { }
                context "docked-meeting" { when { context "docked"; context "meeting" } }
            }
            """);

        var windows = new WindowRegistry();
        windows.Adopt(0x10, new WindowNode(0x10, new WindowIdentity { Title = "t", ClassName = "c", ProcessName = "p" }));

        ContextFacts facts = Facts(wm, windows);
        engine.Evaluate(facts, T0);

        // Marked dirty by hand, as a window event would, with nothing actually different.
        engine.MarkDirty();

        long before = GC.GetAllocatedBytesForCurrentThread();
        IReadOnlyList<ContextTransition> transitions = engine.Evaluate(facts, T0 + Ms(1));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Empty(transitions);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void ASkippedEvaluationAllocatesNothingEither()
    {
        (ContextEngine engine, WindowManager wm) = Load(Presenting);
        ContextFacts facts = Facts(wm);
        engine.Evaluate(facts, T0);

        long before = GC.GetAllocatedBytesForCurrentThread();
        engine.Evaluate(facts, T0 + Ms(1));
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
