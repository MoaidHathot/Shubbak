using System.Globalization;
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

    /// <summary>
    /// Take the focused window under management, or release it.
    /// </summary>
    /// <remarks>
    /// A host action because the state machine deals in nodes and this deals in
    /// window handles: a window that is not managed has no node to name it by.
    /// </remarks>
    ToggleManaged,

    /// <summary>Run <see cref="ShellExecCommand.CommandLine"/>.</summary>
    ShellExecute,

    /// <summary>Re-read the config file.</summary>
    ReloadConfig,

    /// <summary>Force every window back to its computed rectangle.</summary>
    Redraw,

    /// <summary>Shut the window manager down cleanly.</summary>
    Exit,

    /// <summary>
    /// Bring a window the tree does not know about back into view.
    /// </summary>
    /// <remarks>
    /// A host action because it is entirely outside the state machine's world. The
    /// handle names a window that is not in the tree - unmanaged, or left cloaked by
    /// a daemon that died - so there is no node to operate on and the work is
    /// uncloaking, restoring and foregrounding, all of which are Win32.
    /// </remarks>
    RevealWindow,

    /// <summary>Announce <see cref="SignalCommand.Signal"/> to subscribed clients.</summary>
    Signal,

    /// <summary>
    /// Let go of the keyboard and the window events, keeping the tree.
    /// </summary>
    /// <remarks>
    /// A host action because the hooks are the platform layer's, not the state
    /// machine's. <see cref="WindowManager"/> has never known that a keyboard hook
    /// exists, and this is not the change that should teach it.
    /// </remarks>
    Suspend,

    /// <summary>Re-install the hooks and take the desktop back.</summary>
    Resume,

    /// <summary>Whichever of <see cref="Suspend"/> and <see cref="Resume"/> applies.</summary>
    /// <remarks>
    /// Decided by the host rather than here, because the host owns the hooks and so is
    /// the only thing that knows whether they are currently installed.
    /// </remarks>
    ToggleSuspend,

    /// <summary>
    /// Pin a context, or take its pin off. The payload is the encoded
    /// <see cref="ContextCommand"/>; see <see cref="CommandExecutor.Decode"/>.
    /// </summary>
    /// <remarks>
    /// A host action because which contexts hold is decided from facts the state
    /// machine has never held - windows it does not manage, the session, what a
    /// client asked for - and the pin is one of those facts.
    /// </remarks>
    Context,

    /// <summary>
    /// Save, restore or delete a named arrangement of the focused workspace. The
    /// payload is the encoded <see cref="ArrangementCommand"/>; see
    /// <see cref="CommandExecutor.DecodeArrangement"/>.
    /// </summary>
    /// <remarks>
    /// A host action because arrangements live in a file, and the state machine has
    /// no file. The host reads or writes it and, for a restore, hands the tree back to
    /// the state machine to rebuild.
    /// </remarks>
    Arrangement,
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
/// <para>
/// Deliberately thin. All behaviour lives in <see cref="WindowManager"/>; this only
/// translates. Keeping it that way is what stops keybindings and IPC developing
/// subtly different semantics for the same command.
/// </para>
/// <para>
/// One command at a time, and no sequence method. There used to be an
/// <c>ExecuteAll</c> that stopped at the first failure, written for the pair
/// <c>move --workspace 3; focus --workspace 3</c>; both callers had since grown
/// reasons to drive the loop themselves - the daemon to resolve the foreground
/// window before each command, the pipe to answer for each one - so nothing called
/// it, while its test went on asserting a rule production did not follow. The pair it
/// existed for is now one command with a <c>--focus</c> flag, and a sequence that only
/// works when every part of it runs is a single intention written as several.
/// </para>
/// </remarks>
public sealed class CommandExecutor
{
    private readonly WindowManager _wm;

    public CommandExecutor(WindowManager windowManager) =>
        _wm = windowManager ?? throw new ArgumentNullException(nameof(windowManager));

    /// <summary>Executes a single command.</summary>
    public CommandOutcome Execute(WmCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        return command switch
        {
            FocusDirectionCommand c => new(_wm.FocusDirection(c.Direction)),
            FocusWorkspaceCommand c => new(_wm.FocusWorkspace(c.Workspace)),
            FocusRecentWorkspaceCommand => new(_wm.FocusRecentWorkspace()),
            FocusRecentWindowCommand => new(_wm.FocusRecentWindow()),
            CycleFocusCommand c => new(_wm.CycleFocus(c.Forward)),

            // Falls through to the host when the tree has never heard of the handle.
            // That is not a failure: a window nobody manages is exactly the kind most
            // likely to have been lost, and revealing it is the whole point.
            FocusWindowCommand c => _wm.Root.FindWindow(c.Handle) is not null
                ? new(_wm.FocusWindowByHandle(c.Handle))
                : Host(HostAction.RevealWindow, c.Handle.ToString(CultureInfo.InvariantCulture)),

            MoveDirectionCommand c => new(_wm.MoveDirection(c.Direction)),
            MoveToWorkspaceCommand c => new(_wm.MoveToWorkspace(c.Workspace, c.Focus)),

            // The parser guarantees exactly one of the two is set; a record built by
            // hand with neither is a programming error and is refused rather than
            // guessed at.
            MoveWorkspaceToMonitorCommand { Monitor: { } monitor } => new(_wm.MoveWorkspaceToMonitor(monitor)),
            MoveWorkspaceToMonitorCommand { Direction: { } direction } => new(_wm.MoveWorkspaceToMonitor(direction)),
            MoveWorkspaceToMonitorCommand => Rejected(command, "move-workspace needs a direction or a monitor."),

            TagCommand c => new(_wm.Tag(c.Workspace, c.Mode)),
            ToggleStickyCommand => new(_wm.ToggleSticky()),
            ClearTagsCommand => new(_wm.ClearTags()),
            ScratchpadCommand c => new(_wm.ToggleScratchpad(c.Slot)),

            ResizeCommand c => new(_wm.Resize(c.Axis, c.Delta)),
            EqualiseCommand => new(_wm.EqualiseSiblings()),

            ToggleTilingDirectionCommand => new(_wm.ToggleTilingDirection()),
            SplitCommand c => ExecuteWithLayout(c.Layout, _wm.Split),
            SetLayoutCommand c => ExecuteWithLayout(c.Layout, _wm.SetLayout),
            CycleLayoutCommand c => new(_wm.CycleLayout(c.Forward)),

            ToggleFloatingCommand => new(_wm.ToggleFloating()),
            FloatCommand => new(_wm.SetFocusedWindowState(Tree.WindowState.Floating)),
            TileCommand => new(_wm.SetFocusedWindowState(Tree.WindowState.Tiling)),
            ToggleFullscreenCommand c => new(_wm.ToggleFullscreen(c.WholeMonitor)),
            ToggleMinimisedCommand => new(_wm.ToggleMinimised()),

            EnableBindingModeCommand c => new(_wm.SetBindingMode(c.Mode)),
            DisableBindingModeCommand => new(_wm.SetBindingMode(null)),
            TogglePauseCommand => new(_wm.SetPaused(!_wm.IsPaused)),

            // Host effects: no state change, so the result is a trivially successful
            // one carrying no events.
            CloseWindowCommand => Host(HostAction.CloseFocusedWindow),
            ShellExecCommand c => Host(HostAction.ShellExecute, c.CommandLine),
            SignalCommand c => Host(HostAction.Signal, Encode(c)),
            ReloadConfigCommand => Host(HostAction.ReloadConfig),
            RedrawCommand => Host(HostAction.Redraw),
            ExitCommand => Host(HostAction.Exit),

            // Host effects rather than state-machine ones, because what they change is
            // which hooks are installed - which the state machine has never known
            // about and should not start knowing about now.
            SuspendCommand => Host(HostAction.Suspend),
            ResumeCommand => Host(HostAction.Resume),
            ToggleSuspendCommand => Host(HostAction.ToggleSuspend),

            // A pin on a context, for the same reason: the facts that decide a context
            // are the host's.
            ContextCommand c => Host(HostAction.Context, Encode(c)),
            ArrangementCommand c => Host(HostAction.Arrangement, Encode(c)),

            // Only meaningful inside a window rule, where the rule engine consumes it
            // before execution. Reaching here means it was bound to a key by mistake.
            IgnoreCommand => Rejected(command, "'ignore' is only valid in a window rule."),
            ManageCommand => Rejected(
                command,
                "'manage' is only valid in a window rule; use toggle-managed for a key."),

            ToggleManagedCommand => Host(HostAction.ToggleManaged),

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

    /// <summary>
    /// Flattens a signal and its arguments into the single string a host action
    /// carries.
    /// </summary>
    /// <remarks>
    /// Tab-separated because <see cref="CommandOutcome.Payload"/> is one string and a
    /// signal may take arguments. A tab cannot appear in a name or an argument: the
    /// command parser splits its input on whitespace before either reaches here, so
    /// there is nothing to escape and nothing that can be mis-split on the way back.
    /// </remarks>
    private static string Encode(SignalCommand command) =>
        command.Arguments.Count == 0
            ? command.Signal
            : command.Signal + '\t' + string.Join('\t', command.Arguments);

    /// <summary>
    /// Flattens a context command into the single string a host action carries.
    /// </summary>
    /// <remarks>
    /// Tab-separated, like a signal, and for the same reason. The fields are the action,
    /// the context, the time-to-live in milliseconds or nothing, and whether the pin is
    /// leased. <see cref="Decode"/> is the inverse and lives beside this so the two
    /// cannot drift.
    /// </remarks>
    public static string Encode(ContextCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        return string.Join('\t',
            command.Action.ToString(),
            command.Context,
            command.Ttl is { } ttl ? ((long)ttl.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) : "",
            command.Lease ? "lease" : "");
    }

    /// <summary>Reads a context command back out of a host-action payload.</summary>
    public static ContextCommand Decode(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        string[] parts = payload.Split('\t');

        if (parts.Length != 4 || !Enum.TryParse(parts[0], out ContextAction action))
            throw new FormatException($"Not a context payload: '{payload}'.");

        TimeSpan? ttl = parts[2].Length > 0
            ? TimeSpan.FromMilliseconds(long.Parse(parts[2], CultureInfo.InvariantCulture))
            : null;

        return new ContextCommand(parts[1], action, ttl, parts[3].Length > 0);
    }

    /// <summary>Flattens an arrangement command into the single string a host action carries.</summary>
    /// <remarks>Tab-separated, like the others: the action, then the name.</remarks>
    public static string Encode(ArrangementCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        return command.Action.ToString() + '\t' + command.Arrangement;
    }

    /// <summary>Reads an arrangement command back out of a host-action payload.</summary>
    public static ArrangementCommand DecodeArrangement(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        string[] parts = payload.Split('\t');

        if (parts.Length != 2 || !Enum.TryParse(parts[0], out ArrangementAction action))
            throw new FormatException($"Not an arrangement payload: '{payload}'.");

        return new ArrangementCommand(parts[1], action);
    }

    private static CommandOutcome Rejected(WmCommand command, string reason) =>
        new(new WmResult(false, [new CommandRejected(command.Name, reason)]));
}
