using Shubbak.Config;
using Shubbak.Ui.Layout;
using Taj.Core.Widgets;

namespace Taj.Core.Tests;

/// <summary>
/// Two clicks in quick succession as a gesture of their own, and what that does to
/// the single click on the same widget.
/// </summary>
/// <remarks>
/// Windows reports a double click as a second press, so a widget with both
/// <c>on-click</c> and <c>on-double-click</c> would run the single and then the
/// double - open the mixer, then also mute - unless the single waits. The arbiter
/// is the waiting, without the clock: the host supplies the double-click time and
/// the second press, and these pin what comes out for each.
/// </remarks>
public sealed class DoubleClickTests
{
    // ---- the arbiter -----------------------------------------------------------

    [Fact]
    public void AWidgetWithOnlyAClickRunsItAtOnce()
    {
        // Nothing a second press could mean that the first did not, so no wait.
        var arbiter = new ClickArbiter();

        Assert.Equal("focus --workspace 2", arbiter.Press("focus --workspace 2", null));
        Assert.False(arbiter.IsHolding);
    }

    [Fact]
    public void AWidgetWithBothHoldsTheClick()
    {
        var arbiter = new ClickArbiter();

        Assert.Null(arbiter.Press("exec mixer", "exec mixer --mute"));
        Assert.True(arbiter.IsHolding);
    }

    [Fact]
    public void TheHeldClickRunsWhenTheTimePasses()
    {
        var arbiter = new ClickArbiter();
        arbiter.Press("exec mixer", "exec mixer --mute");

        Assert.Equal("exec mixer", arbiter.Elapsed());
        Assert.False(arbiter.IsHolding);

        // And only once.
        Assert.Null(arbiter.Elapsed());
    }

    [Fact]
    public void ASecondPressDropsTheHeldClickAndRunsTheDouble()
    {
        var arbiter = new ClickArbiter();
        arbiter.Press("exec mixer", "exec mixer --mute");

        Assert.Equal("exec mixer --mute", arbiter.DoubleClick("exec mixer", "exec mixer --mute"));
        Assert.False(arbiter.IsHolding);
        Assert.Null(arbiter.Elapsed());
    }

    [Fact]
    public void ADoubleOnAWidgetWithOnlyAClickIsASecondClick()
    {
        // Two quick clicks on a workspace have always focused it twice; the second
        // press is a press.
        var arbiter = new ClickArbiter();

        Assert.Equal("focus --workspace 2", arbiter.Press("focus --workspace 2", null));
        Assert.Equal("focus --workspace 2", arbiter.DoubleClick("focus --workspace 2", null));
    }

    [Fact]
    public void AWidgetWithOnlyADoubleClickWaitsForNothing()
    {
        // No single to hold; the first press does nothing and the second runs the
        // double.
        var arbiter = new ClickArbiter();

        Assert.Null(arbiter.Press(null, "exec mixer --mute"));
        Assert.False(arbiter.IsHolding);
        Assert.Equal("exec mixer --mute", arbiter.DoubleClick(null, "exec mixer --mute"));
    }

    [Fact]
    public void AReadoutDoesNothingEitherWay()
    {
        var arbiter = new ClickArbiter();

        Assert.Null(arbiter.Press(null, null));
        Assert.Null(arbiter.DoubleClick(null, null));
        Assert.Null(arbiter.Elapsed());
    }

    [Fact]
    public void ANewPressReplacesAHeldClick()
    {
        // The pointer moved to another widget before the time passed: the second
        // press is a first press there, and the one being held is forgotten rather
        // than run late on the wrong widget.
        var arbiter = new ClickArbiter();
        arbiter.Press("exec a", "exec aa");

        Assert.Equal("exec b", arbiter.Press("exec b", null));
        Assert.False(arbiter.IsHolding);
        Assert.Null(arbiter.Elapsed());
    }

    // ---- through the loader and onto the node ------------------------------------

    private static IWidget Widget(string widget)
    {
        (TajConfig config, IReadOnlyList<Diagnostic> diagnostics) = TajConfigLoader.Load($$"""
            bar { profile "default" { zone "right" { {{widget}} } } }
            """);

        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        return Assert.Single(Assert.Single(config.Default.Zones).Widgets);
    }

    [Fact]
    public void OnDoubleClickReachesTheNode()
    {
        IWidget widget = Widget("""text id="vol" template="{{ volume }}" on-click="exec mixer" on-double-click="exec mixer --mute" """);

        VisualNode node = widget.Build(new Dictionary<string, string?> { ["volume"] = "40%" });

        Assert.Equal("exec mixer", node.OnClick);
        Assert.Equal("exec mixer --mute", node.OnDoubleClick);
        Assert.True(node.IsInteractive);
    }

    [Fact]
    public void ADoubleClickAloneMakesTheWidgetAControl()
    {
        IWidget widget = Widget("""text id="vol" template="{{ volume }}" on-double-click="exec mixer --mute" """);

        VisualNode node = widget.Build(new Dictionary<string, string?> { ["volume"] = "40%" });

        Assert.Null(node.OnClick);
        Assert.NotNull(node.HoverStyle);
        Assert.True(node.IsInteractive);
    }

    [Fact]
    public void AnIconAndAMeterTakeItToo()
    {
        VisualNode icon = Widget("""icon id="i" source="x" on-double-click="exec a" """)
            .Build(new Dictionary<string, string?>());
        VisualNode meter = Widget("""meter id="m" source="x" on-double-click="exec b" """)
            .Build(new Dictionary<string, string?> { ["x"] = "50" });

        Assert.Equal("exec a", icon.OnDoubleClick);
        Assert.Equal("exec b", meter.OnDoubleClick);
    }

    [Fact]
    public void ItIsNotAnUnknownSettingAndItIsJudgedLikeTheOthers()
    {
        (_, IReadOnlyList<Diagnostic> diagnostics) = TajConfigLoader.Load("""
            bar {
                profile "default" {
                    zone "right" {
                        text id="a" template="a" on-double-click="exec x"
                        text id="lang" template="{{ keyboard }}" on-double-click="keyboard nxt"
                    }
                }
            }
            """);

        Assert.DoesNotContain(diagnostics, d => d.Code == "TAJ0016");

        Diagnostic warning = Assert.Single(diagnostics, d => d.Code == "TAJ0023");
        Assert.Contains("on-double-click", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRecordKnowsAboutIt()
    {
        var actions = new PointerActions(Click: "a", DoubleClick: "aa");

        Assert.True(actions.Any);
        Assert.Contains(("on-double-click", "aa"), actions.Commands());
        Assert.Contains("on-double-click", PointerActions.Keys);

        var node = new VisualNode();
        actions.ApplyTo(node);
        Assert.Equal("aa", node.OnDoubleClick);
    }
}
