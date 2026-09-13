using Shubbak.Config;
using Shubbak.Core.Geometry;
using Taj.Core;
using Shubbak.Ui.Layout;

namespace Taj.Core.Tests;

/// <summary>
/// Lays the bar out and checks where things land.
/// </summary>
/// <remarks>
/// Every other bar test asserts what was parsed. None of them asserts where anything
/// ends up on screen, which is the only thing the user sees - and the bug that
/// prompted these was precisely a clock that parsed correctly and drew in the wrong
/// place. Switching to the presentation profile moved it from the right-hand edge to
/// the left, and nothing about the config or the parsed model said so.
/// </remarks>
public sealed class BarLayoutTests
{
    private const int BarWidth = 1920;

    private const string Config = """
        bar {
            profile "default" {
                height 34
                padding 10
                font-size 15

                zone "left" justify="start" gap=2 { workspaces }
                zone "centre" justify="center" grow=1 { text template="{{ window.title }}" }
                zone "right" justify="end" gap=14 {
                    text id="seattle" template="{{ seattle }}"
                    text id="clock" template="{{ clock }}"
                }
            }

            profile "presentation" extends="default" {
                height 26
                zone "centre" justify="center" grow=1 { }
                zone "right" justify="end" gap=10 {
                    text id="clock" template="{{ clock }}"
                }
            }
        }

        """;

    private static TajConfig Load()
    {
        (TajConfig config, IReadOnlyList<Diagnostic> diagnostics) = TajConfigLoader.Load(Config);

        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);

        return config;
    }

    private static VisualNode Arrange(BarProfile profile)
    {
        var model = new BarModel(profile);

        model.SetValue("workspaces", "1|One|1|1|1\t2|Two|0|1|0");
        model.SetValue("window.title", "Something");
        model.SetValue("clock", "Sun 2 Aug 23:15");
        model.SetValue("seattle", "Sun 2 Aug 13:15");

        VisualNode tree = model.Build();

        new FlexLayout(new FixedTextMeasurer()).Arrange(tree, new Rect(0, 0, BarWidth, profile.Height));

        return tree;
    }

    private static VisualNode Find(VisualNode root, string id) =>
        root.SelfAndDescendants().Single(n => n.Id == id);

    [Fact]
    public void TheClockSitsOnTheRightInTheDefaultProfile()
    {
        VisualNode clock = Find(Arrange(Load().Profiles["default"]), "clock");

        Assert.True(
            clock.Rect.Left > BarWidth / 2,
            $"clock is at x={clock.Rect.Left}, which is not the right-hand half of a {BarWidth}px bar.");
    }

    [Fact]
    public void TheClockStaysOnTheRightInThePresentationProfile()
    {
        // The bug. Switching profiles moved it to the left because the variant lost
        // the only zone that grows, and everything then packed against the leading
        // edge.
        VisualNode clock = Find(Arrange(Load().Profiles["presentation"]), "clock");

        Assert.True(
            clock.Rect.Left > BarWidth / 2,
            $"clock is at x={clock.Rect.Left}, which is not the right-hand half of a {BarWidth}px bar.");
    }

    [Fact]
    public void TheClockEndsNearTheRightEdgeInBothProfiles()
    {
        TajConfig config = Load();

        int defaultRight = Find(Arrange(config.Profiles["default"]), "clock").Rect.Right;
        int slimRight = Find(Arrange(config.Profiles["presentation"]), "clock").Rect.Right;

        // Both are padded by the same amount, so both should finish at the same place.
        Assert.Equal(defaultRight, slimRight);

        Assert.True(
            defaultRight >= BarWidth - 40,
            $"clock ends at x={defaultRight}, well short of the {BarWidth}px edge.");
    }

    [Fact]
    public void WorkspacesStayOnTheLeftInBothProfiles()
    {
        TajConfig config = Load();

        foreach (string name in (string[])["default", "presentation"])
        {
            VisualNode workspaces = Find(Arrange(config.Profiles[name]), "workspaces");

            Assert.True(
                workspaces.Rect.Left < BarWidth / 4,
                $"{name}: workspaces are at x={workspaces.Rect.Left}, not against the left edge.");
        }
    }

    [Fact]
    public void NothingOverflowsTheBar()
    {
        // Overflow is invisible in the model and obvious on screen: a widget drawn
        // past the edge is simply clipped away.
        TajConfig config = Load();

        foreach (string name in (string[])["default", "presentation"])
        {
            VisualNode tree = Arrange(config.Profiles[name]);

            foreach (VisualNode node in tree.SelfAndDescendants())
            {
                if (!node.Visible || node.Rect.IsEmpty) continue;

                Assert.True(
                    node.Rect.Right <= BarWidth,
                    $"{name}: '{node.Id}' ends at x={node.Rect.Right}, past the {BarWidth}px edge.");

                Assert.True(node.Rect.Left >= 0, $"{name}: '{node.Id}' starts at x={node.Rect.Left}.");
            }
        }
    }

    [Fact]
    public void EverythingFitsWithinTheBarHeight()
    {
        TajConfig config = Load();

        foreach (string name in (string[])["default", "presentation"])
        {
            BarProfile profile = config.Profiles[name];

            foreach (VisualNode node in Arrange(profile).SelfAndDescendants())
            {
                if (!node.Visible || node.Rect.IsEmpty) continue;

                Assert.True(
                    node.Rect.Bottom <= profile.Height,
                    $"{name}: '{node.Id}' reaches y={node.Rect.Bottom} in a {profile.Height}px bar.");
            }
        }
    }

    [Fact]
    public void ALongTitleCostsTheTitleAndNotTheClock()
    {
        // The centre zone grows, so it is the one that gives when a title is longer
        // than the room. Before this rule the overflow was shared out proportionally
        // and the clock lost width to a long title - which is why configs capped the
        // title at a character count that knew nothing about the bar's actual width.
        TajConfig config = Load();
        BarProfile profile = config.Profiles["default"];

        var model = new BarModel(profile);
        model.SetValue("workspaces", "1|One|1|1|1\t2|Two|0|1|0");
        model.SetValue("clock", "Sun 2 Aug 23:15");
        model.SetValue("seattle", "Sun 2 Aug 13:15");

        model.SetValue("window.title", "Short");
        VisualNode withShort = model.Build();
        new FlexLayout(new FixedTextMeasurer()).Arrange(withShort, new Rect(0, 0, BarWidth, profile.Height));

        model.SetValue("window.title", new string('x', 400));
        VisualNode withLong = model.Build();
        new FlexLayout(new FixedTextMeasurer()).Arrange(withLong, new Rect(0, 0, BarWidth, profile.Height));

        // The readouts keep exactly the room they had.
        Assert.Equal(Find(withShort, "clock").Rect, Find(withLong, "clock").Rect);
        Assert.Equal(Find(withShort, "seattle").Rect, Find(withLong, "seattle").Rect);
        Assert.Equal(Find(withShort, "workspaces").Rect, Find(withLong, "workspaces").Rect);

        // The title filled what was left and no more; the renderer clips it there.
        VisualNode title = Find(withLong, "text");
        Assert.True(title.Rect.Width > 0);
        Assert.True(title.Rect.Right <= Find(withLong, "right").Rect.Left);
        Assert.True(title.Rect.Width < title.ContentSize.Width, "the title should have been cut to the room available");
    }

    [Fact]
    public void ATitleThatFitsIsNotCut()
    {
        TajConfig config = Load();
        VisualNode title = Find(Arrange(config.Profiles["default"]), "text");

        Assert.Equal(title.ContentSize.Width, title.Rect.Width);
    }
}
