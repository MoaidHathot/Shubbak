using Taj.Core;
using Taj.Core.Layout;
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
        // treat as syntax - the author's config has workspaces named -, \ and '.
        var widget = new WorkspacesWidget("workspaces");

        string encoded = WorkspacesWidget.Encode(
        [
            new("'", "AI", Active: false, HasWindows: false),
            new("\\", "Presentation", Active: false, HasWindows: false),
        ]);

        VisualNode node = widget.Build(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["workspaces"] = encoded,
        });

        Assert.Equal("focus --workspace \"'\"", node.Children[0].OnClick);
        Assert.Equal("focus --workspace \"\\\"", node.Children[1].OnClick);
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
}
