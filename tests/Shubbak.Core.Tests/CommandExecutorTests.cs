using Shubbak.Core.Commands;
using Shubbak.Core.Geometry;
using Shubbak.Core.Layouts;
using Shubbak.Core.Tree;
using Shubbak.Core.Wm;

namespace Shubbak.Core.Tests;

/// <summary>
/// Tests for <see cref="CommandExecutor"/>.
/// </summary>
/// <remarks>
/// The executor is deliberately thin, so these check translation rather than
/// behaviour - behaviour is covered by <see cref="WindowManagerTests"/>. The valuable
/// cases are the ones that prove a keybinding, the CLI and IPC cannot drift apart.
/// Sequencing is not among them: every caller drives its own loop, so a test here
/// would be pinning a rule nothing runs.
/// </remarks>
public sealed class CommandExecutorTests
{
    private static (WindowManager Wm, CommandExecutor Executor) Create(
        WmOptions? options = null, params string[] workspaces)
    {
        WindowManager wm = WmFixture.Create(options, workspaceNames: workspaces);
        return (wm, new CommandExecutor(wm));
    }

    [Fact]
    public void FocusDirectionIsTranslated()
    {
        (WindowManager wm, CommandExecutor executor) = Create(workspaces: "1");
        WindowNode a = wm.Open("a");
        WindowNode b = wm.Open("b");
        wm.Arrange();
        wm.FocusWindow(a);

        CommandOutcome outcome = executor.Execute(new FocusDirectionCommand(Direction.Right));

        Assert.True(outcome.Succeeded);
        Assert.Same(b, wm.FocusedWindow);
    }

    [Fact]
    public void MoveWithFocusMatchesTheAuthorsKeybinding()
    {
        // The config binds alt+shift+3 to a single command:
        //   move --workspace 3 --focus
        // The window moves and the view goes with it.
        (WindowManager wm, CommandExecutor executor) = Create(workspaces: ["1", "3"]);
        WindowNode a = wm.Open("a");

        CommandOutcome outcome = executor.Execute(new MoveToWorkspaceCommand("3", Focus: true));

        Assert.True(outcome.Succeeded);
        Assert.Equal("3", wm.FocusedWorkspace!.Name);
        Assert.Same(a, wm.FocusedWindow);
    }

    [Fact]
    public void MovingWithNothingFocusedIsRefused()
    {
        // The refusal half of what used to be a sequencing test. Its other half
        // asserted that a following command was skipped, which no caller has done
        // since the daemon started running commands singly - a passing test for a
        // rule production did not follow, which is worse than no test at all.
        (WindowManager wm, CommandExecutor executor) = Create(workspaces: ["1", "2"]);

        CommandOutcome outcome = executor.Execute(new MoveToWorkspaceCommand("2"));

        Assert.False(outcome.Succeeded);
        Assert.Equal("1", wm.FocusedWorkspace!.Name);
    }

    [Fact]
    public void ResizeConvertsAPercentageIntoARatioDelta()
    {
        (WindowManager wm, CommandExecutor executor) = Create(workspaces: "1");
        WindowNode a = wm.Open("a");
        wm.Open("b");
        wm.Arrange();
        wm.FocusWindow(a);

        // GlazeWM's `resize --width +2%`.
        executor.Execute(new ResizeCommand(Axis.Horizontal, 0.02));

        Assert.Equal(0.52, a.SizeRatio, 1e-6);
    }

    [Fact]
    public void BindingModeCommandsRoundTrip()
    {
        (WindowManager wm, CommandExecutor executor) = Create(workspaces: "1");

        executor.Execute(new EnableBindingModeCommand("resize"));
        Assert.Equal("resize", wm.BindingMode);

        executor.Execute(new DisableBindingModeCommand());
        Assert.Null(wm.BindingMode);
    }

    [Fact]
    public void TogglePauseFlipsTheFlag()
    {
        (WindowManager wm, CommandExecutor executor) = Create(workspaces: "1");

        executor.Execute(new TogglePauseCommand());
        Assert.True(wm.IsPaused);

        executor.Execute(new TogglePauseCommand());
        Assert.False(wm.IsPaused);
    }

    public static TheoryData<WmCommand, HostAction> SideEffectingCommands => new()
    {
        { new CloseWindowCommand(), HostAction.CloseFocusedWindow },
        { new ReloadConfigCommand(), HostAction.ReloadConfig },
        { new RedrawCommand(), HostAction.Redraw },
        { new ExitCommand(), HostAction.Exit },
    };

    [Theory]
    [MemberData(nameof(SideEffectingCommands))]
    public void SideEffectingCommandsAreReturnedForTheHostRatherThanExecuted(
        WmCommand command, HostAction expected)
    {
        // The state machine has no I/O, so every side effect surfaces at one
        // boundary instead of being scattered through the command layer.
        (_, CommandExecutor executor) = Create(workspaces: "1");

        CommandOutcome outcome = executor.Execute(command);

        Assert.True(outcome.Succeeded);
        Assert.Equal(expected, outcome.Action);
    }

    [Fact]
    public void ShellExecCarriesItsCommandLineToTheHost()
    {
        (_, CommandExecutor executor) = Create(workspaces: "1");

        CommandOutcome outcome = executor.Execute(
            new ShellExecCommand("pwsh -WindowStyle Hidden -Command Restart-Taj"));

        Assert.Equal(HostAction.ShellExecute, outcome.Action);
        Assert.Equal("pwsh -WindowStyle Hidden -Command Restart-Taj", outcome.Payload);
    }

    [Fact]
    public void CloseDoesNotRemoveTheWindowFromTheTree()
    {
        // The window leaves only when the OS reports it gone. Removing it eagerly
        // would lose windows that refuse to close, such as one showing an
        // unsaved-changes prompt.
        (WindowManager wm, CommandExecutor executor) = Create(workspaces: "1");
        WindowNode a = wm.Open("a");

        executor.Execute(new CloseWindowCommand());

        Assert.Same(a, wm.FocusedWindow);
        Assert.Contains(a, wm.FocusedWorkspace!.DescendantWindows());
    }

    [Fact]
    public void UnknownLayoutNamesAreRejectedWithAReason()
    {
        (_, CommandExecutor executor) = Create(workspaces: "1");

        CommandOutcome outcome = executor.Execute(new SetLayoutCommand("hexagonal"));

        Assert.False(outcome.Succeeded);
        Assert.Contains(
            "hexagonal",
            outcome.Events.OfType<CommandRejected>().Single().Reason,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EveryRegisteredLayoutCanBeSetByName()
    {
        // Guards the registry against a layout that exists but is unreachable from
        // config or IPC - which would make it effectively invisible to users.
        (WindowManager wm, CommandExecutor executor) = Create(workspaces: "1");
        wm.Open("a");
        wm.Open("b");

        foreach (string name in Shubbak.Core.Layouts.LayoutRegistry.CanonicalNames)
        {
            CommandOutcome outcome = executor.Execute(new SetLayoutCommand(name));
            Assert.True(outcome.Succeeded, $"layout '{name}' could not be set");
        }
    }

    [Fact]
    public void LayoutAliasesResolve()
    {
        (WindowManager wm, CommandExecutor executor) = Create(workspaces: "1");
        wm.Open("a");
        wm.Open("b");

        Assert.True(executor.Execute(new SetLayoutCommand("vertical")).Succeeded);
        Assert.Same(SplitLayout.Vertical, wm.FocusedWorkspace!.Layout);

        Assert.True(executor.Execute(new SetLayoutCommand("row")).Succeeded);
        Assert.Same(SplitLayout.Horizontal, wm.FocusedWorkspace!.Layout);
    }

    [Fact]
    public void IgnoreIsRejectedOutsideAWindowRule()
    {
        // Only meaningful in a rule; binding it to a key is a config mistake worth
        // surfacing rather than silently doing nothing.
        (_, CommandExecutor executor) = Create(workspaces: "1");

        CommandOutcome outcome = executor.Execute(new IgnoreCommand());

        Assert.False(outcome.Succeeded);
        Assert.NotNull(outcome.Events.OfType<CommandRejected>().Single().Reason);
    }

    [Fact]
    public void RejectedCommandsStillReportEventsSoTheCliCanExplainItself()
    {
        (WindowManager wm, CommandExecutor executor) = Create(workspaces: "1");
        wm.Open("a");
        wm.Arrange();

        CommandOutcome outcome = executor.Execute(new FocusDirectionCommand(Direction.Right));

        Assert.False(outcome.Succeeded);
        CommandRejected rejection = outcome.Events.OfType<CommandRejected>().Single();
        Assert.Equal("focus", rejection.Command);
        Assert.False(string.IsNullOrWhiteSpace(rejection.Reason));
    }
}
