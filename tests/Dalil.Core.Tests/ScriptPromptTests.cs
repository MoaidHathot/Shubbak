using Dalil.Core;
using Shubbak.Config;

namespace Dalil.Core.Tests;

/// <summary>
/// A question whose choices a program prints: <c>param "p" run="..."</c>.
/// </summary>
/// <remarks>
/// <para>
/// The palette had every list the window manager knows and no way to show one it
/// does not - projects, notes, bookmarks, the things a launcher is for. The bar's
/// answer to the same gap is a program that prints lines, and this is the palette's:
/// a <c>param</c> whose choices are whatever a program prints when the question is
/// asked, one per line, run on demand so a palette opened for a window pays nothing.
/// </para>
/// <para>
/// What the host and the window rely on: a first question a program answers is a row
/// that carries the question rather than a list; a later one is carried by the row
/// that leads to it; the program's lines become rows the way any list's values do,
/// with a tab separating what is shown from what is substituted; and a row that would
/// run <c>shell-exec</c> over a pipe that refuses it says so instead of doing nothing.
/// </para>
/// </remarks>
public sealed class ScriptPromptTests
{
    private static readonly CompletionSources Desktop = new(["1", "2"], ["splith", "monocle"], [], []);

    private static PaletteMacro Macro(string source) =>
        Assert.Single(DalilConfigLoader.Load(source).Macros);

    private static PaletteEntry Row(string source) =>
        Assert.Single(PaletteEntries.ForMacros([Macro(source)], Desktop, labels: null));

    // ---- the loader ----------------------------------------------------------------

    [Fact]
    public void RunNamesAProgramWhoseLinesAreTheChoices()
    {
        MacroParam prompt = Assert.Single(Macro("""
            dalil {
                action "Open a project..." {
                    param "p" run="pwsh -NoProfile -File projects.ps1"
                    focus --workspace "{p}"
                }
            }
            """).Prompts);

        Assert.Equal(MacroParamSource.Script, prompt.Source);
        Assert.Equal("pwsh -NoProfile -File projects.ps1", prompt.Run);
        Assert.Empty(prompt.Literals);
    }

    [Fact]
    public void RunCanBeWrittenAsAChildForACommandLineWithQuotesInIt()
    {
        MacroParam prompt = Assert.Single(Macro("""
            dalil {
                action "Open..." {
                    param "p" { run "\"C:\\Program Files\\tool\\list.exe\" --all" }
                    focus --workspace "{p}"
                }
            }
            """).Prompts);

        Assert.Equal("\"C:\\Program Files\\tool\\list.exe\" --all", prompt.Run);
    }

    [Fact]
    public void WrittenOutChoicesStillWinAndRunWinsOverAList()
    {
        // values= is a statement of the whole set; run= is more than the file could
        // write; from= is what the window manager knows. In that order.
        Assert.Equal(MacroParamSource.Literals, Assert.Single(Macro("""
            dalil { action "A" { param "p" run="x.exe" values="a b"; focus --workspace "{p}" } }
            """).Prompts).Source);

        Assert.Equal(MacroParamSource.Script, Assert.Single(Macro("""
            dalil { action "A" { param "p" run="x.exe" from="workspaces"; focus --workspace "{p}" } }
            """).Prompts).Source);
    }

    [Fact]
    public void RunWithNothingToRunIsAnErrorAtTheLine()
    {
        DalilConfigLoad load = DalilConfigLoader.Validate("""
            dalil { action "A" { param "p" run=""; focus --workspace "{p}" } }
            """);

        Diagnostic error = Assert.Single(load.Diagnostics, d => d.Code == "DAL0019");

        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains("nothing to run", error.Message, StringComparison.Ordinal);
        Assert.False(load.Usable);
    }

    [Fact]
    public void ACommandUsingTheAnswerStillParsesWithTheAnswerUnknown()
    {
        // The line is checked with a stand-in, as every prompt's is, so a typo in the
        // verb is caught while the value the program will print is not pretended to.
        DalilConfigLoad load = DalilConfigLoader.Validate("""
            dalil { action "A" { param "p" run="x.exe"; focsu --workspace "{p}" } }
            """);

        Assert.Contains(load.Diagnostics, d => d.Code == "DAL0007");
    }

    // ---- shell-exec over the pipe --------------------------------------------------

    [Fact]
    public void AnActionThatRunsShellExecIsSaidToBeRefusedUnlessTheFileAllowsIt()
    {
        DalilConfigLoad refused = DalilConfigLoader.Validate("""
            dalil { action "Edit" { shell-exec notepad } }
            """);

        Diagnostic warning = Assert.Single(refused.Diagnostics, d => d.Code == "DAL0020");
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("allow-shell-exec-over-ipc", warning.Hint!, StringComparison.Ordinal);

        // And the row says so, rather than closing the palette and doing nothing.
        PaletteMacro macro = Assert.Single(refused.Config.Macros);
        Assert.Contains("allow-shell-exec-over-ipc", macro.Problem!, StringComparison.Ordinal);

        PaletteEntry row = Assert.Single(PaletteEntries.ForMacros(refused.Config.Macros, Desktop, labels: null));
        Assert.True(row.Unavailable);
        Assert.Contains("cannot run", row.Badges);
    }

    [Theory]
    [InlineData("general { allow-shell-exec-over-ipc #true }")]
    [InlineData("general allow-shell-exec-over-ipc=#true")]
    public void AFileThatAllowsShellExecOverThePipeIsNotWarned(string general)
    {
        DalilConfigLoad allowed = DalilConfigLoader.Validate($$"""
            {{general}}
            dalil { action "Edit" { shell-exec notepad } }
            """);

        Assert.DoesNotContain(allowed.Diagnostics, d => d.Code == "DAL0020");
        Assert.Null(Assert.Single(allowed.Config.Macros).Problem);
    }

    [Fact]
    public void AFileThatTurnsItOffExplicitlyIsWarnedLikeOneThatSaysNothing()
    {
        DalilConfigLoad off = DalilConfigLoader.Validate("""
            general { allow-shell-exec-over-ipc #false }
            dalil { action "Open..." { param "p" run="x.exe"; shell-exec code "{p}" } }
            """);

        Assert.Single(off.Diagnostics, d => d.Code == "DAL0020");
    }

    // ---- the rows ------------------------------------------------------------------

    [Fact]
    public void AFirstQuestionAProgramAnswersIsARowCarryingTheQuestion()
    {
        PaletteEntry row = Row("""
            dalil {
                action "Open a project..." {
                    param "p" run="list.exe"
                    focus --workspace "{p}"
                }
            }
            """);

        Assert.Empty(row.Command);
        Assert.True(row.Prompts);
        Assert.False(row.Unavailable);
        Assert.Contains("asks p", row.Badges);

        ScriptPrompt question = Assert.IsType<ScriptPrompt>(row.Runs);
        Assert.Equal(0, question.Depth);
        Assert.Equal("list.exe", question.CommandLine);
        Assert.Equal("p", question.Prompt.Name);
        Assert.Empty(question.Answers);

        // Not judged empty ahead of time: the list is not known until the program runs.
        Assert.False(row.HasActions);
    }

    [Fact]
    public void ALaterQuestionAProgramAnswersIsCarriedByTheRowThatLeadsToIt()
    {
        PaletteEntry row = Row("""
            dalil {
                action "Arrange..." {
                    param "ws" values="1 2"
                    param "p"  run="list.exe"
                    focus --workspace "{ws}"
                    layout --set "{p}"
                }
            }
            """);

        Assert.Null(row.Runs);
        IReadOnlyList<PaletteAction> firsts = row.ResolveActions();

        Assert.Equal(2, firsts.Count);

        PaletteAction two = firsts.Single(a => a.Name == "2");
        Assert.Null(two.Children);
        Assert.Empty(two.Command);
        Assert.Equal("then choose a p", two.Description);

        ScriptPrompt question = Assert.IsType<ScriptPrompt>(two.Runs);
        Assert.Equal(1, question.Depth);
        Assert.Equal("2", question.Answers["ws"]);

        // And the row the window builds from the action still carries it.
        PaletteEntry asEntry = PaletteActions.AsEntries([two]).Single();
        Assert.Same(question, asEntry.Runs);
    }

    [Fact]
    public void TheProgramsLinesBecomeRowsWithTheAnswerFilledIn()
    {
        PaletteEntry row = Row("""
            dalil {
                action "Open a project..." {
                    param "p" run="list.exe"
                    focus --workspace "{p}"
                }
            }
            """);

        IReadOnlyList<PaletteEntry> choices = PaletteEntries.ScriptChoices(
            row.Runs!, ["alpha", "  beta  ", "", "alpha"], Desktop, labels: null);

        Assert.Equal(["alpha", "beta"], choices.Select(c => c.Primary));
        Assert.Equal(["focus --workspace alpha", "focus --workspace beta"], choices.Select(c => c.Command));
        Assert.Equal("focus --workspace alpha", choices[0].Secondary);
    }

    [Fact]
    public void ATabSeparatesWhatIsShownFromWhatIsSubstituted()
    {
        PaletteEntry row = Row("""
            dalil {
                action "Open a project..." {
                    param "p" run="list.exe"
                    focus --workspace "{p}"
                }
            }
            """);

        IReadOnlyList<PaletteEntry> choices = PaletteEntries.ScriptChoices(
            row.Runs!, ["Shubbak\tW:\\Github\\Shubbak", "\tonly-a-value", "only-a-label\t"], Desktop, labels: null);

        Assert.Equal(3, choices.Count);

        Assert.Equal("Shubbak", choices[0].Primary);
        Assert.Equal("focus --workspace W:\\Github\\Shubbak", choices[0].Command);

        // One field, however the tab sits around it, is the value and is shown as such.
        Assert.Equal("only-a-value", choices[1].Primary);
        Assert.Equal("focus --workspace only-a-value", choices[1].Command);
        Assert.Equal("only-a-label", choices[2].Primary);
        Assert.Equal("focus --workspace only-a-label", choices[2].Command);
    }

    [Fact]
    public void AValueWithASpaceIsQuotedForTheWire()
    {
        PaletteEntry row = Row("""
            dalil { action "Go..." { param "p" run="list.exe"; focus --workspace "{p}" } }
            """);

        PaletteEntry choice = Assert.Single(PaletteEntries.ScriptChoices(row.Runs!, ["Second Monitor"], Desktop, labels: null));

        Assert.Equal("focus --workspace \"Second Monitor\"", choice.Command);
    }

    [Fact]
    public void AProgramThatPrintedNothingUsableYieldsOneRowSayingSo()
    {
        PaletteEntry row = Row("""
            dalil { action "Go..." { param "p" run="list.exe"; focus --workspace "{p}" } }
            """);

        PaletteEntry said = Assert.Single(PaletteEntries.ScriptChoices(row.Runs!, ["", "  ", "\t"], Desktop, labels: null));

        Assert.True(said.Unavailable);
        Assert.Contains("printed no choices", said.Secondary, StringComparison.Ordinal);
        Assert.Contains("list.exe", said.Secondary, StringComparison.Ordinal);
    }

    [Fact]
    public void AProgramsAnswerCanLeadToAnotherQuestion()
    {
        PaletteEntry row = Row("""
            dalil {
                action "Arrange..." {
                    param "p"  run="list.exe"
                    param "ws" values="1 2"
                    layout --set "{p}"
                    focus --workspace "{ws}"
                }
            }
            """);

        IReadOnlyList<PaletteEntry> choices = PaletteEntries.ScriptChoices(row.Runs!, ["monocle"], Desktop, labels: null);

        PaletteEntry monocle = Assert.Single(choices);
        Assert.Empty(monocle.Command);
        Assert.Equal("then choose a ws", monocle.Secondary);

        Assert.Equal(
            ["layout --set monocle\nfocus --workspace 1", "layout --set monocle\nfocus --workspace 2"],
            monocle.ResolveActions().Select(a => a.Command));
    }

    [Fact]
    public void TheWaitingAndFailureRowsNameTheQuestion()
    {
        PaletteEntry row = Row("""
            dalil { action "Go..." { param "project" run="list.exe --all"; focus --workspace "{project}" } }
            """);

        PaletteEntry waiting = Assert.Single(PaletteEntries.ScriptWaiting(row.Runs!));
        Assert.True(waiting.Unavailable);
        Assert.Contains("project", waiting.Primary, StringComparison.Ordinal);
        Assert.Equal("list.exe --all", waiting.Secondary);

        PaletteEntry failed = Assert.Single(PaletteEntries.ScriptFailure(row.Runs!, "exit code 1: boom"));
        Assert.True(failed.Unavailable);
        Assert.Equal("exit code 1: boom", failed.Secondary);
        Assert.Contains("list.exe --all", failed.Expands!, StringComparison.Ordinal);
    }

    [Fact]
    public void LinesAreReadAsLabelAndValue()
    {
        Assert.Equal(
            [("a", "a"), ("Show", "value"), ("b", "b")],
            PaletteEntries.ScriptLines(["a", "Show\tvalue", "", "b", "a", "\t", "  \t  "]));
    }
}

/// <summary>Running the program behind a script prompt, against real processes.</summary>
public sealed class ScriptListTests
{
    [Fact]
    public async Task EveryLineTheProgramPrintsIsRead()
    {
        ScriptList.Result result = await ScriptList.RunAsync("cmd /c \"echo alpha&& echo beta&& echo gamma\"");

        Assert.True(result.Succeeded, result.Failure);
        Assert.Equal(["alpha", "beta", "gamma"], result.Lines);
    }

    [Fact]
    public async Task AProgramThatCannotBeStartedIsAFailureNotAnException()
    {
        ScriptList.Result result = await ScriptList.RunAsync("no-such-program-shubbak-test.exe --flag");

        Assert.False(result.Succeeded);
        Assert.Contains("no-such-program-shubbak-test.exe", result.Failure!, StringComparison.Ordinal);
        Assert.Empty(result.Lines);
    }

    [Fact]
    public async Task AProgramThatExitsBadlyWithNothingToShowSaysWhatItWroteToStderr()
    {
        ScriptList.Result result = await ScriptList.RunAsync("cmd /c \"echo it went wrong 1>&2&& exit 3\"");

        Assert.False(result.Succeeded);
        Assert.Contains("exit code 3", result.Failure!, StringComparison.Ordinal);
        Assert.Contains("it went wrong", result.Failure!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProgramThatExitsBadlyButPrintedChoicesIsBelieved()
    {
        // A warning on stderr is not a reason to hide the list.
        ScriptList.Result result = await ScriptList.RunAsync("cmd /c \"echo alpha&& echo careful 1>&2&& exit 1\"");

        Assert.True(result.Succeeded, result.Failure);
        Assert.Equal(["alpha"], result.Lines);
    }

    [Fact]
    public async Task AProgramThatOutstaysItsWelcomeIsStoppedAndSaidSo()
    {
        // ping with a count of three waits about two seconds between echoes.
        ScriptList.Result result = await ScriptList.RunAsync("ping -n 3 127.0.0.1", timeout: TimeSpan.FromMilliseconds(300));

        Assert.False(result.Succeeded);
        Assert.Contains("did not finish", result.Failure!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlySoManyLinesAreRead()
    {
        ScriptList.Result result = await ScriptList.RunAsync("cmd /c \"for /L %i in (1,1,1500) do @echo line %i\"");

        Assert.True(result.Succeeded, result.Failure);
        Assert.Equal(ScriptList.MaxLines, result.Lines.Count);
        Assert.Equal("line 1", result.Lines[0]);
    }

    [Fact]
    public async Task CancellingTheWaitStopsTheProgram()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        ScriptList.Result result = await ScriptList.RunAsync("ping -n 3 127.0.0.1", token: cancel.Token);

        Assert.False(result.Succeeded);
        Assert.Contains("moved on", result.Failure!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("list.exe", "list.exe", "")]
    [InlineData("pwsh -NoProfile -File x.ps1", "pwsh", "-NoProfile -File x.ps1")]
    [InlineData("\"C:\\Program Files\\t\\list.exe\" --all", "C:\\Program Files\\t\\list.exe", "--all")]
    [InlineData("  spaced   args here ", "spaced", "  args here")]
    public void TheProgramIsSplitFromItsArgumentsAsTheBarSplitsIts(string commandLine, string file, string arguments)
    {
        (string actualFile, string actualArguments) = ScriptList.Split(commandLine);

        Assert.Equal(file, actualFile);
        Assert.Equal(arguments, actualArguments);
    }
}
