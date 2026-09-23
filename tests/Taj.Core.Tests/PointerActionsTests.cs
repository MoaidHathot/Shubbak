using Shubbak.Config;
using Shubbak.Ui.Layout;
using Taj.Core.Widgets;

namespace Taj.Core.Tests;

/// <summary>
/// What the pointer can do to a widget beyond a left click.
/// </summary>
/// <remarks>
/// <para>
/// <c>on-click</c> was the whole vocabulary: a volume pill could not be scrolled, a
/// clock could not open a calendar on the right button, the workspace strip could
/// not be flipped through with the wheel. Each new gesture is a command like the
/// click is, on the same path as a keybinding, so nothing here can behave differently
/// from a key that sends the same words.
/// </para>
/// <para>
/// The loader's job is to read the five settings, judge the ones it can - the bar's
/// own <c>keyboard</c> verb - and say which key it is talking about when it does.
/// </para>
/// </remarks>
public sealed class PointerActionsTests
{
    private static IWidget Widget(string source, string id)
    {
        (TajConfig config, IReadOnlyList<Diagnostic> diagnostics) = TajConfigLoader.Load(source);

        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);

        return config.Default.Zones.SelectMany(z => z.Widgets).Single(w => w.Id == id);
    }

    private static VisualNode Build(IWidget widget) =>
        widget.Build(new Dictionary<string, string?>(StringComparer.Ordinal) { ["volume"] = "40%" });

    [Fact]
    public void EveryGestureOnATextWidgetReachesTheNode()
    {
        IWidget widget = Widget("""
            bar {
                profile "default" {
                    zone "right" {
                        text id="vol" template="{{ volume }}" on-click="exec mixer" on-right-click="exec mixer --mute" on-middle-click="exec mixer --reset" on-scroll-up="exec mixer --up" on-scroll-down="exec mixer --down"
                    }
                }
            }
            """, "vol");

        VisualNode node = Build(widget);

        Assert.Equal("exec mixer", node.OnClick);
        Assert.Equal("exec mixer --mute", node.OnRightClick);
        Assert.Equal("exec mixer --reset", node.OnMiddleClick);
        Assert.Equal("exec mixer --up", node.OnScrollUp);
        Assert.Equal("exec mixer --down", node.OnScrollDown);
        Assert.True(node.IsInteractive);
    }

    [Fact]
    public void AnIconTakesTheSameGestures()
    {
        IWidget widget = Widget("""
            bar {
                profile "default" {
                    zone "right" { icon id="bell" source="B" on-scroll-up="exec louder" on-right-click="exec quiet" }
                }
            }
            """, "bell");

        VisualNode node = Build(widget);

        Assert.Equal("exec louder", node.OnScrollUp);
        Assert.Equal("exec quiet", node.OnRightClick);
        Assert.Null(node.OnClick);
    }

    [Fact]
    public void AGestureWithoutAClickStillMakesTheWidgetAControl()
    {
        // The hand cursor and the hover style used to follow on-click alone; a pill
        // that only scrolls is a control too, and should look like one.
        IWidget widget = Widget("""
            bar {
                profile "default" {
                    zone "right" { text id="vol" template="{{ volume }}" on-scroll-up="exec louder" }
                }
            }
            """, "vol");

        VisualNode node = Build(widget);

        Assert.Null(node.OnClick);
        Assert.NotNull(node.HoverStyle);
        Assert.True(node.IsInteractive);
    }

    [Fact]
    public void AReadoutIsNotAControl()
    {
        IWidget widget = Widget("""
            bar { profile "default" { zone "right" { text id="vol" template="{{ volume }}" } } }
            """, "vol");

        VisualNode node = Build(widget);

        Assert.Null(node.HoverStyle);
        Assert.False(node.IsInteractive);
    }

    [Fact]
    public void ABadKeyboardCommandOnAnyGestureNamesTheGesture()
    {
        // The same judgement on-click had, for each of the others - and the message
        // says which one, since a widget may carry five and only one be wrong.
        (_, IReadOnlyList<Diagnostic> diagnostics) = TajConfigLoader.Load("""
            bar {
                profile "default" {
                    zone "right" {
                        text id="lang" template="{{ keyboard }}" on-click="keyboard next" on-scroll-down="keyboard prv"
                    }
                }
            }
            """);

        Diagnostic warning = Assert.Single(diagnostics, d => d.Code == "TAJ0023");

        Assert.Contains("on-scroll-down", warning.Message, StringComparison.Ordinal);
        Assert.Contains("prv", warning.Message, StringComparison.Ordinal);
        Assert.Contains("on-scroll-down=", warning.Hint!, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGestureSettingsAreNotUnknownSettings()
    {
        (_, IReadOnlyList<Diagnostic> diagnostics) = TajConfigLoader.Load("""
            bar {
                profile "default" {
                    zone "right" {
                        text id="a" template="a" on-right-click="x" on-middle-click="y" on-scroll-up="z" on-scroll-down="w"
                        icon id="b" source="b" on-right-click="x" on-middle-click="y" on-scroll-up="z" on-scroll-down="w"
                    }
                }
            }
            """);

        Assert.DoesNotContain(diagnostics, d => d.Code == "TAJ0016");
    }

    [Fact]
    public void CommandsListsOnlyTheGesturesThatDoSomething()
    {
        var actions = new PointerActions(Click: "a", ScrollUp: "", RightClick: null, ScrollDown: "d");

        Assert.Equal([("on-click", "a"), ("on-scroll-down", "d")], actions.Commands());
        Assert.True(actions.Any);
        Assert.False(PointerActions.None.Any);
    }

    [Fact]
    public void TheClickShorthandIsTheSameAction()
    {
        // OnClick predates the record; everything that set it keeps working and lands
        // in the same place the loader's five settings do.
        var widget = new TemplateWidget("t", "x", VisualStyle.Default) { OnClick = "exec a" };

        Assert.Equal("exec a", widget.Actions.Click);

        widget.Actions = widget.Actions with { RightClick = "exec b" };

        Assert.Equal("exec a", widget.OnClick);
        Assert.Equal("exec b", widget.Actions.RightClick);
    }
}
