using Shubbak.Config;
using Taj.Core;
using Taj.Core.Widgets;

namespace Taj.Core.Tests;

/// <summary>
/// <c>keyboard next</c> and friends: the one click command the bar performs itself.
/// </summary>
/// <remarks>
/// The switch itself is a message posted to the window in front and cannot be
/// exercised here. What can be is the boundary: which strings are the bar's and which
/// go to the window manager, what each of the bar's means, and what the loader says
/// about one that means nothing.
/// </remarks>
public sealed class KeyboardCommandTests
{
    // ---- whose command is it -----------------------------------------------

    [Theory]
    [InlineData("keyboard next")]
    [InlineData("keyboard previous")]
    [InlineData("keyboard he")]
    [InlineData("Keyboard Next")]
    [InlineData("  keyboard   next  ")]
    [InlineData("keyboard")]
    [InlineData("keyboard nxt")]
    public void AnythingStartingWithTheVerbIsTheBars(string command) =>
        Assert.True(KeyboardCommand.Recognises(command));

    [Theory]
    [InlineData("focus --workspace \"1\"")]
    [InlineData("wm-resume")]
    [InlineData("signal ayn microphone mute")]
    [InlineData("keyboards next")]
    [InlineData("")]
    [InlineData(null)]
    public void EverythingElseIsTheWindowManagers(string? command) =>
        Assert.False(KeyboardCommand.Recognises(command));

    // ---- what it means -----------------------------------------------------

    [Theory]
    [InlineData("keyboard next", KeyboardChange.Next, null)]
    [InlineData("keyboard NEXT", KeyboardChange.Next, null)]
    [InlineData("keyboard previous", KeyboardChange.Previous, null)]
    [InlineData("keyboard prev", KeyboardChange.Previous, null)]
    [InlineData("keyboard he", KeyboardChange.Language, "HE")]
    [InlineData("keyboard EN", KeyboardChange.Language, "EN")]
    public void TheArgumentSaysWhichLayout(string command, KeyboardChange change, string? language)
    {
        Assert.True(KeyboardCommand.TryParse(command, out KeyboardCommand? parsed, out string? problem), problem);

        Assert.Equal(change, parsed!.Change);
        Assert.Equal(language, parsed.Language);
    }

    [Theory]
    [InlineData("keyboard", "which layout")]
    [InlineData("keyboard nxt", "'nxt'")]
    [InlineData("keyboard next please", "one word")]
    [InlineData("keyboard hebrew", "'hebrew'")]
    [InlineData("keyboard h3", "'h3'")]
    [InlineData("wm-resume", "not a keyboard command")]
    public void ACommandThatMeansNothingSaysWhy(string command, string expectedInProblem)
    {
        Assert.False(KeyboardCommand.TryParse(command, out KeyboardCommand? parsed, out string? problem));

        Assert.Null(parsed);
        Assert.Contains(expectedInProblem, problem!, StringComparison.Ordinal);
        Assert.Contains("keyboard next", problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void ItReadsBackAsItWasWritten()
    {
        Assert.Equal("keyboard next", new KeyboardCommand(KeyboardChange.Next).ToString());
        Assert.Equal("keyboard previous", new KeyboardCommand(KeyboardChange.Previous).ToString());
        Assert.Equal("keyboard he", new KeyboardCommand(KeyboardChange.Language, "HE").ToString());
    }

    // ---- the loader --------------------------------------------------------

    [Fact]
    public void TheIndicatorBecomesAControl()
    {
        // The reason any of this exists: the language indicator, clickable, with the
        // same hover every other control has.
        (TajConfig config, IReadOnlyList<Diagnostic> diagnostics) = TajConfigLoader.Load("""
            bar {
                source "keyboard" kind="keyboard" interval=250
                profile "default" {
                    zone "right" {
                        text id="lang" template="{{ keyboard }}" on-click="keyboard next" {
                            when value="HE" colour="#f38ba8"
                        }
                    }
                }
            }
            """);

        Assert.DoesNotContain(diagnostics, d => d.Code.StartsWith("TAJ", StringComparison.Ordinal));

        var widget = (TemplateWidget)config.Profiles["default"].Zones[0].Widgets[0];
        Assert.Equal("keyboard next", widget.OnClick);

        Shubbak.Ui.Layout.VisualNode node = widget.Build(new Dictionary<string, string?> { ["keyboard"] = "EN" });

        Assert.Equal("keyboard next", node.OnClick);
        Assert.NotNull(node.HoverStyle);
    }

    [Fact]
    public void AKeyboardCommandTheBarCannotPerformIsPointedOut()
    {
        // The symptom otherwise is a control that does nothing when clicked, which
        // looks exactly like a control that was never wired up.
        (_, IReadOnlyList<Diagnostic> diagnostics) = TajConfigLoader.Load("""
            bar {
                profile "default" {
                    zone "right" { text id="lang" template="{{ keyboard }}" on-click="keyboard nxt" }
                }
            }
            """);

        Diagnostic warning = Assert.Single(diagnostics, d => d.Code == "TAJ0023");

        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("lang", warning.Message, StringComparison.Ordinal);
        Assert.Contains("nxt", warning.Message, StringComparison.Ordinal);
        Assert.Contains("keyboard next", warning.Hint!, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWindowManagersCommandsAreNotJudgedHere()
    {
        // The bar has no catalogue of the window manager's verbs and must not pretend
        // to; a command it does not recognise is the window manager's to refuse.
        (_, IReadOnlyList<Diagnostic> diagnostics) = TajConfigLoader.Load("""
            bar {
                profile "default" {
                    zone "right" { text id="x" template="x" on-click="some-future-verb --flag" }
                }
            }
            """);

        Assert.DoesNotContain(diagnostics, d => d.Code == "TAJ0023");
    }
}
