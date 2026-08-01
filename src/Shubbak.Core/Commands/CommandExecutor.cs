using Shubbak.Core.Layouts;
using Shubbak.Core.Wm;

namespace Shubbak.Core.Commands;

/// <summary>
/// Effects a command asks of the host process, which the state machine cannot
/// perform itself.
/// </summary>
/// <remarks>
/// <see cref="WindowManager"/> has no I/O by design, so commands that need to touch
/// the outside world - closing a window, launching a program, exiting - are returned
/// as requests rather than executed. The host performs them. This keeps the state
/// machine testable and keeps every side effect visible at one boundary.
/// </remarks>
public enum HostAction
{
    None,

    /// <summary>Ask the focused window to close.</summary>
    CloseFocusedWindow,

    /// <summary>Run <see cref="ShellExecCommand.CommandLine"/>.</summary>
    ShellExecute,

    /// <summary>Re-read the config file.</summary>
    ReloadConfig,

    /// <summary>Force every window back to its computed rectangle.</summary>
    Redraw,

    /// <summary>Shut the window manager down cleanly.</summary>
    Exit,
}

/// <summary>The outcome of executing one command.</summary>
/// <param name="Result">What changed in the state machine.</param>
/// <param name="Action">An effect the host must perform, if any.</param>
/// <param name="Payload">Argument for <paramref name="Action"/>.</param>
public readonly record struct CommandOutcome(
    WmResult Result,
    HostAction Action = HostAction.None,
    string? Payload = null)
{
    public bool Succeeded => Result.Succeeded;

    public IReadOnlyList<WmEvent> Events => Result.Events;
}

/// <summary>
/// Maps <see cref="WmCommand"/> values onto <see cref="WindowManager"/> operations.
/// </summary>
/// <remarks>
/// Deliberately thin. All behaviour lives in <see cref="WindowManager"/>; this only
/// translates. Keeping it that way is what stops keybindings and IPC developing
/// subtly different semantics for the same command.
/// </remarks>
public sealed class CommandExecutor
{
    private readonly WindowManager _wm;

    public CommandExecutor(WindowManager windowManager) =>
        _wm = windowManager ?? throw new ArgumentNullException(nameof(windowManager));

    /// <summary>Executes a sequence, stopping at the first command that fails.</summary>
    /// <remarks>
    /// Sequences exist because the author's config binds pairs such as
    /// <c>['move --workspace 3', 'focus --workspace 3']</c>. Stopping on failure
    /// matters: if the move is rejected there is nothing to follow, and focusing
    /// anyway would leave the user somewhere they did not ask to be.
    /// </remarks>
    public IReadOnlyList<CommandOutcome> ExecuteAll(IEnumerable<WmCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);

        List<CommandOutcome> outcomes = [];

        foreach (WmCommand command in commands)
        {
            CommandOutcome outcome = Execute(command);
            outcomes.Add(outcome);
            if (!outcome.Succeeded) break;
        }

        return outcomes;
    }

    /// <summary>Executes a single command.</summary>
    public CommandOutcome Execute(WmCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        return command switch
        {
            FocusDirectionCommand c => new(_wm.FocusDirection(c.Direction)),
            FocusWorkspaceCommand c => new(_wm.FocusWorkspace(c.Workspace)),
            FocusRecentWorkspaceCommand => new(_wm.FocusRecentWorkspace()),
            CycleFocusCommand c => new(_wm.CycleFocus(c.Forward)),

            MoveDirectionCommand c => new(_wm.MoveDirection(c.Direction)),
            MoveToWorkspaceCommand c => new(_wm.MoveToWorkspace(c.Workspace)),
            MoveWorkspaceToMonitorCommand c => new(_wm.MoveWorkspaceToMonitor(c.Direction)),

            ResizeCommand c => new(_wm.Resize(c.Axis, c.Delta)),
            EqualiseCommand => new(_wm.EqualiseSiblings()),

            ToggleTilingDirectionCommand => new(_wm.ToggleTilingDirection()),
            SplitCommand c => ExecuteWithLayout(c.Layout, _wm.Split),
            SetLayoutCommand c => ExecuteWithLayout(c.Layout, _wm.SetLayout),
            CycleLayoutCommand c => new(_wm.CycleLayout(c.Forward)),

            ToggleFloatingCommand => new(_wm.ToggleFloating()),
            ToggleFullscreenCommand => new(_wm.ToggleFullscreen()),
            ToggleMinimisedCommand => new(_wm.ToggleMinimised()),

            EnableBindingModeCommand c => new(_wm.SetBindingMode(c.Mode)),
            DisableBindingModeCommand => new(_wm.SetBindingMode(null)),
            TogglePauseCommand => new(_wm.SetPaused(!_wm.IsPaused)),

            // Host effects: no state change, so the result is a trivially successful
            // one carrying no events.
            CloseWindowCommand => Host(HostAction.CloseFocusedWindow),
            ShellExecCommand c => Host(HostAction.ShellExecute, c.CommandLine),
            ReloadConfigCommand => Host(HostAction.ReloadConfig),
            RedrawCommand => Host(HostAction.Redraw),
            ExitCommand => Host(HostAction.Exit),

            // Only meaningful inside a window rule, where the rule engine consumes it
            // before execution. Reaching here means it was bound to a key by mistake.
            IgnoreCommand => Rejected(command, "'ignore' is only valid in a window rule."),

            _ => Rejected(command, $"Command '{command.Name}' is not implemented."),
        };
    }

    private static CommandOutcome ExecuteWithLayout(string name, Func<ILayout, WmResult> operation)
    {
        if (!LayoutRegistry.TryResolve(name, out ILayout layout))
        {
            return new CommandOutcome(new WmResult(
                false,
                [new CommandRejected("layout", $"Unknown layout '{name}'.")]));
        }

        return new CommandOutcome(operation(layout));
    }

    private static CommandOutcome Host(HostAction action, string? payload = null) =>
        new(new WmResult(true, []), action, payload);

    private static CommandOutcome Rejected(WmCommand command, string reason) =>
        new(new WmResult(false, [new CommandRejected(command.Name, reason)]));
}
