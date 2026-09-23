using Shubbak.Config;
using Shubbak.Core.Rendering;
using Taj.Core;
using Shubbak.Ui.Layout;
using Taj.Core.Sources;
using Taj.Core.Widgets;

namespace Taj.Core.Tests;

/// <summary>Tests for the template engine.</summary>
public sealed class TemplateTests
{
    private static readonly Dictionary<string, string?> Values = new(StringComparer.Ordinal)
    {
        ["clock"] = "14:30",
        ["title"] = "A very long window title that would push everything else off the bar",
        ["empty"] = "",
        ["nothing"] = null,
    };

    [Fact]
    public void SubstitutesValues()
    {
        Assert.Equal("It is 14:30", Template.Render("It is {{ clock }}", Values));
    }

    [Fact]
    public void HandlesSeveralPlaceholders()
    {
        Assert.Equal("14:30 | 14:30", Template.Render("{{ clock }} | {{ clock }}", Values));
    }

    [Fact]
    public void TextWithNoPlaceholdersPassesThrough()
    {
        Assert.Equal("static", Template.Render("static", Values));
    }

    [Fact]
    public void UnknownSourcesRenderAsEmpty()
    {
        // Blank rather than an error marker: a source that has not produced its first
        // value yet is normal during the first second of a session.
        Assert.Equal("x  y", Template.Render("x {{ missing }} y", Values));
    }

    [Fact]
    public void NullValuesRenderAsEmpty()
    {
        Assert.Equal("[]", Template.Render("[{{ nothing }}]", Values));
    }

    [Fact]
    public void TruncateShortensLongValues()
    {
        // The filter that earns its place: window titles are unbounded and would
        // otherwise push everything else off the bar.
        string result = Template.Render("{{ title | truncate:20 }}", Values);

        Assert.Equal(20, result.Length);
        Assert.EndsWith("\u2026", result, StringComparison.Ordinal);
    }

    [Fact]
    public void TruncateLeavesShortValuesAlone()
    {
        Assert.Equal("14:30", Template.Render("{{ clock | truncate:20 }}", Values));
    }

    [Theory]
    [InlineData("{{ clock | upper }}", "14:30")]
    [InlineData("{{ title | lower | truncate:6 }}", "a ver\u2026")]
    [InlineData("{{ empty | default:none }}", "none")]
    [InlineData("{{ clock | default:none }}", "14:30")]
    [InlineData("{{ clock | replace::,h }}", "14h30")]
    [InlineData("{{ clock | then:\uE720 }}", "\uE720")]
    [InlineData("{{ empty | then:\uE720 }}", "")]
    [InlineData("{{ nothing | then:on }}", "")]
    [InlineData("{{ clock | then:on | upper }}", "ON")]
    public void FiltersApplyAndChain(string template, string expected)
    {
        Assert.Equal(expected, Template.Render(template, Values));
    }

    [Fact]
    public void UnknownFiltersPassTheValueThrough()
    {
        // A typo degrades to a plain value rather than blanking the widget.
        Assert.Equal("14:30", Template.Render("{{ clock | nonsense }}", Values));
    }

    [Fact]
    public void UnterminatedPlaceholdersAreShownLiterally()
    {
        // Visible on the bar, rather than silently swallowing the rest of the line.
        Assert.Equal("before {{ clock", Template.Render("before {{ clock", Values));
    }

    [Fact]
    public void DependenciesListTheSourcesUsed()
    {
        IReadOnlyList<string> dependencies =
            Template.Dependencies("{{ clock }} - {{ title | truncate:10 }} - {{ clock }}");

        Assert.Equal(["clock", "title"], dependencies);
    }

    [Fact]
    public void DependenciesOfAStaticTemplateAreEmpty()
    {
        Assert.Empty(Template.Dependencies("no placeholders here"));
    }
}

/// <summary>Tests for the built-in widgets.</summary>
public sealed class WidgetTests
{
    [Fact]
    public void TemplateWidgetProducesATextNode()
    {
        var widget = new TemplateWidget("clock", "{{ clock }}", VisualStyle.Default);

        VisualNode node = widget.Build(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["clock"] = "14:30",
        });

        Assert.Equal(VisualKind.Text, node.Kind);
        Assert.Equal("14:30", node.Text);
        Assert.True(node.Visible);
    }

    [Fact]
    public void TemplateWidgetHidesItselfWhenEmpty()
    {
        // An empty box with padding and a background looks like a rendering fault.
        var widget = new TemplateWidget("battery", "{{ battery }}", VisualStyle.Default);

        VisualNode node = widget.Build(new Dictionary<string, string?>(StringComparer.Ordinal));

        Assert.False(node.Visible);
    }

    [Fact]
    public void TemplateWidgetDeclaresItsDependencies()
    {
        var widget = new TemplateWidget("both", "{{ a }} {{ b }}", VisualStyle.Default);

        Assert.Equal(["a", "b"], widget.Dependencies);
    }

    [Fact]
    public void WorkspacesWidgetProducesOneChildPerWorkspace()
    {
        var widget = new WorkspacesWidget("workspaces");

        string encoded = WorkspacesWidget.Encode(
        [
            new("1", "Firefox", Active: true, HasWindows: true),
            new("2", "Edge", Active: false, HasWindows: true),
            new("3", "3", Active: false, HasWindows: false),
        ]);

        VisualNode node = widget.Build(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["workspaces"] = encoded,
        });

        Assert.Equal(3, node.Children.Count);
        Assert.Equal("Firefox", node.Children[0].Text);
        Assert.Equal("3", node.Children[2].Text);
    }

    [Fact]
    public void WorkspacesWidgetQuotesNamesInItsClickCommands()
    {
        // Workspace names include characters the command tokeniser would otherwise
        // treat as syntax - the author's config has workspaces named -, \ and '. The
        // spelling is the shared one, so a name the tokeniser reads back whole as it
        // is written is written as it is, and one that is itself a double quote goes
        // in single ones rather than becoming an empty string.
        var widget = new WorkspacesWidget("workspaces");

        string encoded = WorkspacesWidget.Encode(
        [
            new("'", "AI", Active: false, HasWindows: false),
            new("\\", "Presentation", Active: false, HasWindows: false),
            new("\"", "Quoted", Active: false, HasWindows: false),
            new("Second Monitor", "Two", Active: false, HasWindows: false),
        ]);

        VisualNode node = widget.Build(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["workspaces"] = encoded,
        });

        Assert.Equal("focus --workspace \"'\"", node.Children[0].OnClick);
        Assert.Equal("focus --workspace \\", node.Children[1].OnClick);
        Assert.Equal("focus --workspace '\"'", node.Children[2].OnClick);
        Assert.Equal("focus --workspace \"Second Monitor\"", node.Children[3].OnClick);
    }

    [Fact]
    public void WorkspacesWidgetCanHideEmptyOnes()
    {
        var widget = new WorkspacesWidget("workspaces") { HideEmpty = true };

        string encoded = WorkspacesWidget.Encode(
        [
            new("1", "1", Active: true, HasWindows: false),
            new("2", "2", Active: false, HasWindows: true),
            new("3", "3", Active: false, HasWindows: false),
        ]);

        VisualNode node = widget.Build(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["workspaces"] = encoded,
        });

        // The active one is kept even though it is empty; otherwise the indicator
        // would show nothing at all on a freshly switched-to workspace.
        Assert.Equal(2, node.Children.Count);
    }

    [Fact]
    public void WorkspaceEncodingRoundTrips()
    {
        WorkspacesWidget.WorkspaceEntry[] entries =
        [
            new("1", "Firefox", true, true),
            new("-", "Chat", false, false),
        ];

        WorkspacesWidget.WorkspaceEntry[] decoded =
            [.. WorkspacesWidget.Decode(WorkspacesWidget.Encode(entries))];

        Assert.Equal(entries, decoded);
    }

    [Fact]
    public void WorkspacesWidgetHandlesAMissingValue()
    {
        var widget = new WorkspacesWidget("workspaces");

        VisualNode node = widget.Build(new Dictionary<string, string?>(StringComparer.Ordinal));

        Assert.Empty(node.Children);
    }
}

/// <summary>Tests for colour parsing.</summary>
public sealed class ColourTests
{
    [Theory]
    [InlineData("#fff", 255, 255, 255, 255)]
    [InlineData("#000", 0, 0, 0, 255)]
    [InlineData("#8dbcff", 0x8D, 0xBC, 0xFF, 255)]
    [InlineData("8dbcff", 0x8D, 0xBC, 0xFF, 255)]
    [InlineData("#8dbcff80", 0x8D, 0xBC, 0xFF, 0x80)]
    [InlineData("#ABC", 0xAA, 0xBB, 0xCC, 255)]
    public void ParsesHexColours(string text, int r, int g, int b, int a)
    {
        Assert.True(Colour.TryParse(text, out Colour colour));

        Assert.Equal(r, colour.R);
        Assert.Equal(g, colour.G);
        Assert.Equal(b, colour.B);
        Assert.Equal(a, colour.A);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("#ff")]
    [InlineData("#gggggg")]
    [InlineData("not a colour")]
    public void RejectsMalformedColours(string? text)
    {
        Assert.False(Colour.TryParse(text, out _));
    }

    [Fact]
    public void AlphaIsLastMatchingCss()
    {
        // Win32 spells it #AARRGGBB. Config is written by people who know CSS, and
        // silently reinterpreting their colours would be baffling.
        Assert.True(Colour.TryParse("#11223344", out Colour colour));

        Assert.Equal(0x11, colour.R);
        Assert.Equal(0x44, colour.A);
    }

    [Fact]
    public void LerpBlendsBetweenColours()
    {
        Colour mid = Colour.Black.Lerp(Colour.White, 0.5);

        Assert.Equal(128, mid.R);
        Assert.Equal(128, mid.G);
        Assert.Equal(128, mid.B);
    }

    [Fact]
    public void RoundTripsThroughItsStringForm()
    {
        Assert.True(Colour.TryParse("#8dbcff", out Colour colour));
        Assert.True(Colour.TryParse(colour.ToString(), out Colour again));

        Assert.Equal(colour, again);
    }
}

/// <summary>Tests for the bar model.</summary>
public sealed class BarModelTests
{
    private static BarProfile Profile(params IWidget[] widgets) => new(
        "test",
        BarEdge.Top,
        26,
        Colour.Black,
        Edges.All(4),
        [new BarZone("left", JustifyContent.Start, 1, 4, widgets)]);

    [Fact]
    public void BuildProducesAZonePerProfileZone()
    {
        using var model = new BarModel(Profile(
            new TemplateWidget("clock", "{{ clock }}", VisualStyle.Default)));

        model.SetValue("clock", "14:30");
        VisualNode root = model.Build();

        VisualNode zone = Assert.Single(root.Children);
        Assert.Equal("left", zone.Id);
        Assert.Equal("14:30", Assert.Single(zone.Children).Text);
    }

    [Fact]
    public void SettingTheSameValueDoesNotMarkTheBarDirty()
    {
        // A bar that redraws on every event flickers; one that redraws on a timer
        // burns battery for nothing.
        using var model = new BarModel(Profile(
            new TemplateWidget("clock", "{{ clock }}", VisualStyle.Default)));

        model.SetValue("clock", "14:30");
        model.Build();

        Assert.False(model.IsDirty);

        model.SetValue("clock", "14:30");
        Assert.False(model.IsDirty);

        model.SetValue("clock", "14:31");
        Assert.True(model.IsDirty);
    }

    [Fact]
    public void ChangingProfileMarksTheBarDirty()
    {
        using var model = new BarModel(Profile());
        model.Build();

        model.Profile = Profile(new TemplateWidget("x", "y", VisualStyle.Default));

        Assert.True(model.IsDirty);
    }

    [Fact]
    public void AWidgetThatThrowsDoesNotBlankTheBar()
    {
        using var model = new BarModel(Profile(
            new ThrowingWidget(),
            new TemplateWidget("clock", "{{ clock }}", VisualStyle.Default)));

        model.SetValue("clock", "14:30");
        VisualNode root = model.Build();

        // The working widget still rendered.
        Assert.Contains(root.SelfAndDescendants(), n => n.Text == "14:30");
    }

    [Fact]
    public void PushSourcesFeedTheModel()
    {
        using var model = new BarModel(Profile(
            new TemplateWidget("title", "{{ title }}", VisualStyle.Default)));

        var source = new PushSource("title");
        model.AddSource(source);

        source.Set("hello");

        Assert.Equal("hello", model.GetValue("title"));
        Assert.Contains(model.Build().SelfAndDescendants(), n => n.Text == "hello");
    }

    [Fact]
    public void PushSourcesSuppressUnchangedValues()
    {
        var source = new PushSource("title");
        int changes = 0;
        source.Changed += _ => changes++;

        source.Set("a");
        source.Set("a");
        source.Set("b");

        Assert.Equal(2, changes);
    }

    private sealed class ThrowingWidget : IWidget
    {
        public string Id => "broken";

        public IReadOnlyList<string> Dependencies => [];

        public VisualNode Build(IReadOnlyDictionary<string, string?> values) =>
            throw new InvalidOperationException("widget is broken");
    }
}

/// <summary>Tests for bar profile selection.</summary>
public sealed class BarProfileSelectorTests
{
    private static BarProfile Make(string name) =>
        new(name, BarEdge.Top, 26, Colour.Black, Edges.Zero, []);

    private static BarProfileSelector Selector(params BarRule[] rules)
    {
        Dictionary<string, BarProfile> profiles = new(StringComparer.Ordinal)
        {
            ["default"] = Make("default"),
            ["presentation"] = Make("presentation"),
            ["minimal"] = Make("minimal"),
        };

        return new BarProfileSelector(profiles, rules, profiles["default"]);
    }

    [Fact]
    public void FallsBackWhenNothingMatches()
    {
        Assert.Equal("default", Selector().Select("1", 0).Name);
    }

    [Fact]
    public void MatchesOnWorkspace()
    {
        BarProfileSelector selector = Selector(new BarRule("presentation", Workspace: "\\"));

        Assert.Equal("presentation", selector.Select("\\", 0).Name);
        Assert.Equal("default", selector.Select("1", 0).Name);
    }

    [Fact]
    public void MatchesOnMonitor()
    {
        BarProfileSelector selector = Selector(new BarRule("minimal", MonitorIndex: 1));

        Assert.Equal("minimal", selector.Select("1", 1).Name);
        Assert.Equal("default", selector.Select("1", 0).Name);
    }

    [Fact]
    public void MatchesOnAMonitorsName()
    {
        // The names the window manager's configuration gives a display arrive with the
        // snapshot, so a bar rule can say the same word a workspace binding says.
        BarProfileSelector selector = Selector(new BarRule("minimal", MonitorName: "projector"));

        Assert.Equal("minimal", selector.Select("1", 0, ["projector"]).Name);
        Assert.Equal("minimal", selector.Select("1", 3, ["dell", "PROJECTOR"]).Name);
        Assert.Equal("default", selector.Select("1", 0, ["dell"]).Name);

        // Told nothing about names - an older window manager, or a display nothing
        // names - a rule that wants one does not match, rather than matching everything.
        Assert.Equal("default", selector.Select("1", 0).Name);
        Assert.Equal("default", selector.Select("1", 0, []).Name);
    }

    [Fact]
    public void ANameAndAWorkspaceTogetherBothHaveToHold()
    {
        BarProfileSelector selector = Selector(
            new BarRule("presentation", Workspace: ";", MonitorName: "projector"));

        Assert.Equal("presentation", selector.Select(";", 0, ["projector"]).Name);
        Assert.Equal("default", selector.Select(";", 0, ["laptop"]).Name);
        Assert.Equal("default", selector.Select("1", 0, ["projector"]).Name);
    }

    [Fact]
    public void TheLoaderReadsANumberAsAPositionAndAWordAsAName()
    {
        (TajConfig config, IReadOnlyList<Diagnostic> diagnostics) = TajConfigLoader.Load("""
            bar {
                profile "default" { height 26 }
                profile "minimal" { height 20 }
                rule use="minimal" monitor=1
                rule use="minimal" monitor="1"
                rule use="minimal" monitor="projector"
            }
            """);

        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal(3, config.Rules.Count);

        Assert.Equal(1, config.Rules[0].MonitorIndex);
        Assert.Null(config.Rules[0].MonitorName);

        // Quoted, still a position: the value type is not a signal of what was meant.
        Assert.Equal(1, config.Rules[1].MonitorIndex);
        Assert.Null(config.Rules[1].MonitorName);

        Assert.Null(config.Rules[2].MonitorIndex);
        Assert.Equal("projector", config.Rules[2].MonitorName);
    }

    [Fact]
    public void FirstMatchingRuleWins()
    {
        BarProfileSelector selector = Selector(
            new BarRule("presentation", Workspace: "1"),
            new BarRule("minimal", Workspace: "1"));

        Assert.Equal("presentation", selector.Select("1", 0).Name);
    }

    [Fact]
    public void ARuleNamingAnUnknownProfileIsSkipped()
    {
        // Skipping rather than failing keeps the bar on screen when a profile is
        // renamed and one rule is left behind.
        BarProfileSelector selector = Selector(
            new BarRule("does-not-exist", Workspace: "1"),
            new BarRule("minimal", Workspace: "1"));

        Assert.Equal("minimal", selector.Select("1", 0).Name);
    }

    [Fact]
    public void MatchesOnAContext()
    {
        // The contexts the window manager holds arrive with the snapshot, so a bar rule
        // can say the same word the contexts section declares.
        BarProfileSelector selector = Selector(new BarRule("presentation", Context: "presenting"));

        Assert.Equal("presentation", selector.Select("1", 0, [], ["presenting"]).Name);
        Assert.Equal("presentation", selector.Select("1", 0, [], ["docked", "PRESENTING"]).Name);
        Assert.Equal("default", selector.Select("1", 0, [], ["docked"]).Name);

        // Told nothing about contexts - an older window manager, or none held - a rule
        // that wants one does not match, rather than matching everything.
        Assert.Equal("default", selector.Select("1", 0).Name);
        Assert.Equal("default", selector.Select("1", 0, [], []).Name);
    }

    [Fact]
    public void AContextAndAWorkspaceTogetherBothHaveToHold()
    {
        BarProfileSelector selector = Selector(
            new BarRule("presentation", Workspace: ";", Context: "presenting"));

        Assert.Equal("presentation", selector.Select(";", 0, [], ["presenting"]).Name);
        Assert.Equal("default", selector.Select(";", 0, [], ["docked"]).Name);
        Assert.Equal("default", selector.Select("1", 0, [], ["presenting"]).Name);
    }

    [Fact]
    public void AContextRuleBeforeAGeneralOneIsHowNotPresentingIsWritten()
    {
        // No negation on a bar rule: first match wins, so the rule for the context goes
        // first and the general rule after it covers every other case.
        BarProfileSelector selector = Selector(
            new BarRule("presentation", Context: "presenting"),
            new BarRule("minimal", Workspace: "1"));

        Assert.Equal("presentation", selector.Select("1", 0, [], ["presenting"]).Name);
        Assert.Equal("minimal", selector.Select("1", 0, [], []).Name);
    }

    [Fact]
    public void TheLoaderReadsAContext()
    {
        (TajConfig config, IReadOnlyList<Diagnostic> diagnostics) = TajConfigLoader.Load("""
            contexts {
                context "presenting" { }
            }
            bar {
                profile "default" { height 26 }
                profile "minimal" { height 20 }
                rule use="minimal" context="presenting"
                rule use="minimal" context="presenting" monitor="projector"
            }
            """);

        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(diagnostics, d => d.Code == "TAJ0020");
        Assert.Equal(2, config.Rules.Count);

        Assert.Equal("presenting", config.Rules[0].Context);
        Assert.Null(config.Rules[0].MonitorName);

        Assert.Equal("presenting", config.Rules[1].Context);
        Assert.Equal("projector", config.Rules[1].MonitorName);
    }

    [Fact]
    public void ARuleOnAContextNobodyDeclaresIsReportedAndKept()
    {
        // A warning with a guess, and the rule stays: it is correct as written and can
        // never match, which is worth saying and not worth losing the bar over.
        (TajConfig config, IReadOnlyList<Diagnostic> diagnostics) = TajConfigLoader.Load("""
            contexts {
                context "presenting" { }
                context "docked" { }
            }
            bar {
                profile "default" { height 26 }
                rule use="default" context="presentng"
            }
            """);

        Diagnostic warning = Assert.Single(diagnostics, d => d.Code == "TAJ0020");
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("presentng", warning.Message, StringComparison.Ordinal);
        Assert.Contains("presenting", warning.Hint!, StringComparison.Ordinal);
        Assert.Equal("presentng", Assert.Single(config.Rules).Context);
    }

    [Fact]
    public void ARuleOnAContextWhenNoneAreDeclaredSaysWhereToDeclareOne()
    {
        (_, IReadOnlyList<Diagnostic> diagnostics) = TajConfigLoader.Load("""
            bar {
                profile "default" { height 26 }
                rule use="default" context="presenting"
            }
            """);

        Diagnostic warning = Assert.Single(diagnostics, d => d.Code == "TAJ0020");
        Assert.Contains("contexts { }", warning.Hint!, StringComparison.Ordinal);
    }
}

/// <summary>Tests for what the bar says about the contexts the window manager holds.</summary>
public sealed class ActiveContextsTests
{
    [Fact]
    public void NothingHeldIsAnEmptyValueSoTheWidgetHides()
    {
        Assert.Equal(string.Empty, ActiveContexts.Label(null));
        Assert.Equal(string.Empty, ActiveContexts.Label([]));
    }

    [Fact]
    public void TheNamesAreJoinedTheWayTheCommandLineJoinsThem()
    {
        Assert.Equal("presenting", ActiveContexts.Label(["presenting"]));
        Assert.Equal("presenting, docked", ActiveContexts.Label(["presenting", "docked"]));
    }

    [Fact]
    public void TheKeyIsTheOneTemplatesUse()
    {
        Assert.Equal("contexts", ActiveContexts.Key);
    }

    [Fact]
    public void TwoAnswersAreTheSameOnlyWhenTheyAgreeInContentAndOrder()
    {
        Assert.True(ActiveContexts.Same([], []));
        Assert.True(ActiveContexts.Same(["a", "b"], ["a", "b"]));

        Assert.False(ActiveContexts.Same(null, []));
        Assert.False(ActiveContexts.Same(["a"], ["a", "b"]));
        Assert.False(ActiveContexts.Same(["a", "b"], ["b", "a"]));
        Assert.False(ActiveContexts.Same(["a"], ["A"]));
    }

    [Fact]
    public void EachContextGetsAValueOfItsOwnThatEmptiesWhenItDrops()
    {
        // The first answer: every context that holds is written as its name.
        Assert.Equal(
            [new("context.camera-in-use", "camera-in-use"), new("context.meeting", "meeting")],
            ActiveContexts.Changes(null, ["camera-in-use", "meeting"]));

        // The camera goes: its key is written empty once, so the widget showing it hides.
        Assert.Equal(
            [new("context.meeting", "meeting"), new("context.camera-in-use", "")],
            ActiveContexts.Changes(["camera-in-use", "meeting"], ["meeting"]));

        // Nothing held before and nothing now: nothing to write.
        Assert.Empty(ActiveContexts.Changes([], []));
    }

    [Fact]
    public void TheKeysAreTheOnesTemplatesUse()
    {
        Assert.Equal("context.meeting", ActiveContexts.KeyFor("meeting"));
        Assert.Equal("context.", ActiveContexts.KeyPrefix);
    }
}

/// <summary>Tests for how a clickable widget admits it is one.</summary>
public sealed class WidgetHoverTests
{
    private static readonly Dictionary<string, string?> None = new(StringComparer.Ordinal);

    private static TemplateWidget Widget(string source) =>
        (TemplateWidget)TajConfigLoader.Load($$"""
            bar {
                profile "default" {
                    height 30
                    zone "right" {
                        {{source}}
                    }
                }
            }
            """).Config.Profiles["default"].Zones.Single().Widgets.Single();

    [Fact]
    public void OnlyAClickableWidgetReactsToThePointer()
    {
        // A readout that lit up when hovered would be claiming to be a control.
        Assert.Null(Widget("""text template="{{ clock }}" """).Build(None).HoverStyle);
        Assert.NotNull(Widget("""text template="{{ clock }}" on-click="wm-redraw" """).Build(None).HoverStyle);
    }

    [Fact]
    public void ABareGlyphGainsTheSameFaintPillTheWorkspacesUse()
    {
        VisualNode node = Widget("""text template="x" colour="#a6e3a1" on-click="signal ayn microphone mute" """).Build(None);

        Assert.Equal(new Colour(0xFF, 0xFF, 0xFF, 0x1A), node.HoverStyle!.Value.Background);
        Assert.Equal(node.Style.Foreground, node.HoverStyle.Value.Foreground);
        Assert.Equal(4, node.HoverStyle.Value.CornerRadius);
    }

    [Fact]
    public void APillHoversAsALighterPill()
    {
        VisualNode node = Widget("""text template="x" colour="#1e1e2e" background="#f38ba8" on-click="wm-resume" """).Build(None);

        Colour rest = node.Style.Background;
        Colour hover = node.HoverStyle!.Value.Background;

        Assert.True(hover.R >= rest.R && hover.G > rest.G && hover.B > rest.B, "lighter, towards white");
        Assert.Equal(rest.A, hover.A);
    }

    [Fact]
    public void TheHoverFollowsTheStyleInForceNotTheDefault()
    {
        // A `when` turned it red; hovering must not snap it back to green.
        var values = new Dictionary<string, string?> { ["keyboard"] = "HE" };

        VisualNode node = Widget("""
            text template="{{ keyboard }}" colour="#a6e3a1" on-click="wm-redraw" {
                when value="HE" background="#f38ba8"
            }
            """).Build(values);

        Assert.Equal(new Colour(0xF3, 0x8B, 0xA8), node.Style.Background);
        Assert.NotEqual(new Colour(0xFF, 0xFF, 0xFF, 0x1A), node.HoverStyle!.Value.Background);
        Assert.True(node.HoverStyle.Value.Background.R >= 0xF3);
    }

    [Fact]
    public void WrittenHoverColoursWin()
    {
        VisualNode node = Widget("""
            text template="x" on-click="wm-redraw" hover-background="#ffffff" hover-colour="#000000"
            """).Build(None);

        Assert.Equal(Colour.White, node.HoverStyle!.Value.Background);
        Assert.Equal(Colour.Black, node.HoverStyle.Value.Foreground);
    }

    [Fact]
    public void AHoverColourWithoutAClickIsPointedOut()
    {
        (_, IReadOnlyList<Diagnostic> diagnostics) = TajConfigLoader.Load("""
            bar {
                profile "default" {
                    height 30
                    zone "right" { text id="clock" template="{{ clock }}" hover-background="#ffffff" }
                }
            }
            """);

        Diagnostic warning = Assert.Single(diagnostics, d => d.Code == "TAJ0022");
        Assert.Contains("clock", warning.Message, StringComparison.Ordinal);
        Assert.Contains("on-click", warning.Hint!, StringComparison.Ordinal);
    }
}

/// <summary>Tests for typography and icons per widget.</summary>
public sealed class WidgetFontTests
{
    [Fact]
    public void AWidgetCanUseAFontFamilyOfItsOwn()
    {
        // An icon font on one widget beside text in the profile's face on the rest:
        // the camera glyph is in Segoe Fluent Icons and the clock is not.
        (TajConfig config, IReadOnlyList<Diagnostic> diagnostics) = TajConfigLoader.Load("""
            contexts { context "camera-in-use" { } }
            bar {
                profile "default" {
                    height 30
                    font "Segoe UI"
                    zone "right" {
                        text id="camera" template="{{ context.camera-in-use | then:\uE722 }}" font="Segoe Fluent Icons" {
                            when of="context.camera-in-use" value="camera-in-use" colour="#f38ba8" font="Segoe MDL2 Assets"
                        }
                        text id="clock" template="{{ clock }}"
                    }
                }
            }
            """);

        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(diagnostics, d => d.Code is "TAJ0016" or "TAJ0017" or "TAJ0021");

        var zone = config.Profiles["default"].Zones.Single();
        var camera = Assert.IsType<TemplateWidget>(zone.Widgets[0]);
        var clock = Assert.IsType<TemplateWidget>(zone.Widgets[1]);

        Assert.Equal("Segoe Fluent Icons", camera.Style.Font.Family);
        Assert.Equal("Segoe MDL2 Assets", camera.Conditions.Single().Style.Font.Family);
        Assert.Equal("Segoe UI", clock.Style.Font.Family);
    }

    [Fact]
    public void AWidgetReadingAContextNobodyDeclaresIsPointedOut()
    {
        (_, IReadOnlyList<Diagnostic> diagnostics) = TajConfigLoader.Load("""
            contexts { context "camera-in-use" { } context "meeting" { } }
            bar {
                profile "default" {
                    height 30
                    zone "right" {
                        text template="{{ context.camera-on | then:x }}"
                        text template="{{ clock }}" {
                            when of="context.meting" value="meeting" colour="#f38ba8"
                        }
                    }
                }
            }
            """);

        Diagnostic[] warnings = [.. diagnostics.Where(d => d.Code == "TAJ0021")];

        Assert.Equal(2, warnings.Length);
        Assert.All(warnings, w => Assert.Equal(DiagnosticSeverity.Warning, w.Severity));
        // Too far from any name for a guess, so the declared list; one letter off, so a guess.
        Assert.Contains("camera-on", warnings[0].Message, StringComparison.Ordinal);
        Assert.Contains("Declared: camera-in-use, meeting", warnings[0].Hint!, StringComparison.Ordinal);
        Assert.Contains("context.meeting", warnings[1].Hint!, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeclaredContextSourceIsSilentAndSoIsEveryOtherSource()
    {
        (_, IReadOnlyList<Diagnostic> diagnostics) = TajConfigLoader.Load("""
            contexts { context "meeting" { } }
            bar {
                profile "default" {
                    height 30
                    zone "right" {
                        text template="{{ context.meeting | then:x }} {{ contexts }} {{ clock }}" {
                            when of="binding_mode" value="resize" colour="#f38ba8"
                        }
                    }
                }
            }
            """);

        Assert.DoesNotContain(diagnostics, d => d.Code == "TAJ0021");
    }
}
