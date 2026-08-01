using Shubbak.Core.Geometry;

namespace Shubbak.Core.Commands;

/// <summary>
/// A window manager command, parsed from a keybinding, the CLI, or IPC.
/// </summary>
/// <remarks>
/// <para>
/// Commands are inert data. Parsing, dispatch and execution are separate stages, so
/// that a keybinding, a <c>shubbak</c> invocation and an IPC request all converge on
/// the same values and therefore cannot drift apart in behaviour - a real problem in
/// window managers that grow a second, subtly different command path for their CLI.
/// </para>
/// <para>
/// Being data also means a command can be validated at config load time. A typo in
/// a keybinding is reported with a line and column when the config is read, rather
/// than silently doing nothing months later when the key is finally pressed.
/// </para>
/// </remarks>
public abstract record WmCommand
{
    /// <summary>The canonical name, as it appears in config and IPC.</summary>
    public abstract string Name { get; }
}

// ---- focus -----------------------------------------------------------------

/// <summary><c>focus --direction left</c></summary>
public sealed record FocusDirectionCommand(Direction Direction) : WmCommand
{
    public override string Name => "focus";
}

/// <summary><c>focus --workspace 3</c></summary>
public sealed record FocusWorkspaceCommand(string Workspace) : WmCommand
{
    public override string Name => "focus-workspace";
}

/// <summary><c>focus --recent-workspace</c></summary>
public sealed record FocusRecentWorkspaceCommand : WmCommand
{
    public override string Name => "focus-recent-workspace";
}

/// <summary><c>focus --next</c> / <c>focus --prev</c></summary>
public sealed record CycleFocusCommand(bool Forward) : WmCommand
{
    public override string Name => "focus-cycle";
}

// ---- movement --------------------------------------------------------------

/// <summary><c>move --direction right</c></summary>
public sealed record MoveDirectionCommand(Direction Direction) : WmCommand
{
    public override string Name => "move";
}

/// <summary><c>move --workspace 3</c></summary>
public sealed record MoveToWorkspaceCommand(string Workspace) : WmCommand
{
    public override string Name => "move-to-workspace";
}

/// <summary><c>move-workspace --direction left</c></summary>
public sealed record MoveWorkspaceToMonitorCommand(Direction Direction) : WmCommand
{
    public override string Name => "move-workspace";
}

// ---- tags ------------------------------------------------------------------

/// <summary><c>tag --add 3</c> / <c>tag --remove 3</c> / <c>tag --toggle 3</c></summary>
public sealed record TagCommand(string Workspace, Wm.TagMode Mode) : WmCommand
{
    public override string Name => "tag";
}

/// <summary><c>sticky</c> - follow every workspace on this monitor.</summary>
public sealed record ToggleStickyCommand : WmCommand
{
    public override string Name => "sticky";
}

/// <summary><c>scratchpad --name notes</c> - stash or summon.</summary>
public sealed record ScratchpadCommand(string Slot) : WmCommand
{
    public override string Name => "scratchpad";
}

/// <summary><c>tag --clear</c></summary>
public sealed record ClearTagsCommand : WmCommand
{
    public override string Name => "tag-clear";
}

// ---- sizing ----------------------------------------------------------------

/// <summary>
/// <c>resize --width +2%</c>
/// </summary>
/// <param name="Axis">Axis to resize along.</param>
/// <param name="Delta">
/// Signed fraction of the container, e.g. <c>0.02</c> for <c>+2%</c>.
/// </param>
public sealed record ResizeCommand(Axis Axis, double Delta) : WmCommand
{
    public override string Name => "resize";
}

/// <summary>Gives every sibling of the focused window an equal share.</summary>
public sealed record EqualiseCommand : WmCommand
{
    public override string Name => "equalise";
}

// ---- structure -------------------------------------------------------------

/// <summary><c>toggle-tiling-direction</c></summary>
public sealed record ToggleTilingDirectionCommand : WmCommand
{
    public override string Name => "toggle-tiling-direction";
}

/// <summary><c>split --vertical</c></summary>
public sealed record SplitCommand(string Layout) : WmCommand
{
    public override string Name => "split";
}

/// <summary><c>layout --set splitv</c></summary>
public sealed record SetLayoutCommand(string Layout) : WmCommand
{
    public override string Name => "layout";
}

/// <summary><c>layout --cycle</c> / <c>layout --cycle-back</c></summary>
public sealed record CycleLayoutCommand(bool Forward) : WmCommand
{
    public override string Name => "layout-cycle";
}

// ---- window state ----------------------------------------------------------

/// <summary><c>toggle-floating</c></summary>
public sealed record ToggleFloatingCommand : WmCommand
{
    public override string Name => "toggle-floating";
}

/// <summary><c>toggle-fullscreen</c></summary>
public sealed record ToggleFullscreenCommand : WmCommand
{
    public override string Name => "toggle-fullscreen";
}

/// <summary><c>toggle-minimised</c></summary>
public sealed record ToggleMinimisedCommand : WmCommand
{
    public override string Name => "toggle-minimized";
}

/// <summary>
/// <c>close</c> - asks the focused window to close.
/// </summary>
/// <remarks>
/// Produces no tree change on its own. The platform layer sends the close request;
/// the window leaves the tree later, when the operating system reports it gone. Any
/// other ordering would remove windows that refuse to close, such as one showing an
/// unsaved-changes prompt.
/// </remarks>
public sealed record CloseWindowCommand : WmCommand
{
    public override string Name => "close";
}

// ---- modes and lifecycle ---------------------------------------------------

/// <summary><c>wm-enable-binding-mode --name resize</c></summary>
public sealed record EnableBindingModeCommand(string Mode) : WmCommand
{
    public override string Name => "wm-enable-binding-mode";
}

/// <summary><c>wm-disable-binding-mode</c></summary>
public sealed record DisableBindingModeCommand : WmCommand
{
    public override string Name => "wm-disable-binding-mode";
}

/// <summary><c>wm-toggle-pause</c></summary>
public sealed record TogglePauseCommand : WmCommand
{
    public override string Name => "wm-toggle-pause";
}

/// <summary><c>wm-reload-config</c></summary>
public sealed record ReloadConfigCommand : WmCommand
{
    public override string Name => "wm-reload-config";
}

/// <summary><c>wm-redraw</c></summary>
public sealed record RedrawCommand : WmCommand
{
    public override string Name => "wm-redraw";
}

/// <summary><c>wm-exit</c></summary>
public sealed record ExitCommand : WmCommand
{
    public override string Name => "wm-exit";
}

/// <summary>
/// <c>shell-exec pwsh ...</c> - runs an external program.
/// </summary>
/// <remarks>
/// Handled by the host process, not by the state machine, which has no I/O.
/// </remarks>
public sealed record ShellExecCommand(string CommandLine) : WmCommand
{
    public override string Name => "shell-exec";
}

/// <summary>
/// <c>ignore</c> - excludes a window from management.
/// </summary>
/// <remarks>
/// Only meaningful inside a window rule, where it is the most common action by far.
/// </remarks>
public sealed record IgnoreCommand : WmCommand
{
    public override string Name => "ignore";
}
