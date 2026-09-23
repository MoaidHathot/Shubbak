using Dalil.Core;
using Shubbak.Config;
using Shubbak.Core.Commands;

namespace Dalil.Core.Tests;

/// <summary>
/// How a macro's commands are written for the pipe and filled in.
/// </summary>
/// <remarks>
/// The bug these guard: the loader validated a macro as tokens and stored it as
/// tokens joined with spaces, so a workspace called "Second Monitor" passed the check
/// and was sent as two arguments. The wire form has to mean what the tokens meant,
/// and an answer put into a placeholder has to arrive as one argument too.
/// </remarks>
public sealed class MacroTextTests
{
    [Fact]
    public void PlainTokensAreJoinedAsTheyWere()
    {
        Assert.Equal("focus --direction left", MacroText.Wire(["focus", "--direction", "left"]));
    }

    [Fact]
    public void ATokenWithASpaceIsQuotedSoItStaysOneArgument()
    {
        string wire = MacroText.Wire(["focus", "--workspace", "Second Monitor"]);

        Assert.Equal("focus --workspace \"Second Monitor\"", wire);
        Assert.True(CommandParser.TryParse(wire, default, out WmCommand? command, out _));
        Assert.Equal("Second Monitor", ((FocusWorkspaceCommand)command!).Workspace);
    }

    [Fact]
    public void ATokenThatIsAQuoteIsWrittenInTheOtherKind()
    {
        // The author's config has a workspace named `'`.
        string wire = MacroText.Wire(["focus", "--workspace", "'"]);

        Assert.Equal("focus --workspace \"'\"", wire);
        Assert.True(CommandParser.TryParse(wire, default, out WmCommand? command, out _));
        Assert.Equal("'", ((FocusWorkspaceCommand)command!).Workspace);

        Assert.Equal("focus --workspace '\"'", MacroText.Wire(["focus", "--workspace", "\""]));
    }

    [Fact]
    public void APlaceholderIsLeftBareForTheAnswerToSpell()
    {
        Assert.Equal("move --workspace {ws}", MacroText.Wire(["move", "--workspace", "{ws}"]));
    }

    [Fact]
    public void AnAnswerIsQuotedWhenItNeedsToBe()
    {
        var answers = new Dictionary<string, string> { ["ws"] = "Second Monitor" };

        string filled = MacroText.Fill("move --workspace {ws}", answers, quoted: true);

        Assert.Equal("move --workspace \"Second Monitor\"", filled);
        Assert.True(CommandParser.TryParse(filled, default, out WmCommand? command, out _));
        Assert.Equal("Second Monitor", ((MoveToWorkspaceCommand)command!).Workspace);
    }

    [Fact]
    public void AnAnswerThatNeedsNoQuotingIsWrittenBare()
    {
        var answers = new Dictionary<string, string> { ["ws"] = "3" };

        Assert.Equal("move --workspace 3", MacroText.Fill("move --workspace {ws}", answers, quoted: true));
    }

    [Fact]
    public void AnAnswerInsideQuotesIsNotQuotedAgain()
    {
        // `shell-exec "code {path}"`: the file already quoted the whole argument, and a
        // quoted answer inside it would close and reopen the quotation around itself.
        var answers = new Dictionary<string, string> { ["path"] = "C:\\My Files" };

        string filled = MacroText.Fill("shell-exec \"code {path}\"", answers, quoted: true);

        Assert.Equal("shell-exec \"code C:\\My Files\"", filled);
    }

    [Fact]
    public void TheShownTextIsFilledWithoutQuotes()
    {
        var answers = new Dictionary<string, string> { ["ws"] = "Second Monitor" };

        Assert.Equal("move --workspace Second Monitor", MacroText.Fill("move --workspace {ws}", answers, quoted: false));
    }

    [Fact]
    public void AnUnansweredPlaceholderAndBracesThatAreNotOneAreLeftAlone()
    {
        var answers = new Dictionary<string, string> { ["ws"] = "3" };

        Assert.Equal("move --workspace {other} {ws}", MacroText.Fill("move --workspace {other} {ws}", answers, quoted: true).Replace("3", "{ws}", StringComparison.Ordinal));
        Assert.Equal("say {", MacroText.Fill("say {", answers, quoted: true));
        Assert.Equal("say {}", MacroText.Fill("say {}", answers, quoted: true));
    }

    [Fact]
    public void AMacroLoadedWithAQuotedArgumentSendsItWhole()
    {
        // End to end through the loader: what the file quoted stays one argument.
        DalilConfig config = DalilConfigLoader.Load("""
            dalil {
                action "Go there" {
                    focus --workspace "Second Monitor"
                }
            }
            """);

        PaletteMacro macro = Assert.Single(config.Macros);
        Assert.Null(macro.Problem);

        string sent = Assert.Single(macro.Commands);
        Assert.True(CommandParser.TryParse(sent, default, out WmCommand? command, out _), sent);
        Assert.Equal("Second Monitor", ((FocusWorkspaceCommand)command!).Workspace);
    }
}
