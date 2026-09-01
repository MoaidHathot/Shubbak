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

    /// <summary>
    /// Whether this command acts on whichever window currently has focus.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stated per command rather than inferred, because the consequence of getting it
    /// wrong is acting on a window the user cannot see. Focus can be on a window
    /// Shubbak does not manage - a dialog, a tray popup, an application it passed over
    /// - and its own idea of the focused window is then whatever was focused before.
    /// Commands that ran anyway hit that earlier window: pressing the float key over
    /// an unmanaged window untiled something else entirely, and the close key would
    /// have closed it.
    /// </para>
    /// <para>
    /// False for commands that act on a workspace, a monitor, or focus itself. Those
    /// remain useful from an unmanaged window - moving focus out of one is exactly
    /// how you leave it - and refusing them would break clicking a workspace on the
    /// bar, whose own window is not managed either.
    /// </para>
    /// </remarks>
    public virtual bool TargetsFocusedWindow => false;

    /// <summary>
    /// Whether holding the key down should run this command again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Windows delivers auto-repeat as repeated key-downs with no release between, and
    /// every one of them used to be executed. For focus and resize that is exactly
    /// what the user wants and is why holding the key feels right. For the rest it is
    /// not: <c>close</c> held for a second closes everything on the workspace, and
    /// <c>shell-exec</c> held for a second starts thirty terminals.
    /// </para>
    /// <para>
    /// Written as one list rather than an override per command, unlike
    /// <see cref="TargetsFocusedWindow"/>. That property states something about each
    /// command in isolation; this one is a policy about which commands are dangerous
    /// to run in a burst, and a policy is easier to get right when it can be read in
    /// one place.
    /// </para>
    /// <para>
    /// Repeating is the default, because the two mistakes are not equal. A navigation
    /// command that stops repeating is immediately obvious and merely irritating,
    /// while a destructive one that repeats is discovered by losing something. Any
    /// binding can override this with <c>repeat=#false</c> or <c>repeat=#true</c>.
    /// </para>
    /// <para>
    /// The test a command has to pass is not "is it destructive" but <b>does repeating
    /// it act on the same thing</b>. Moving the focused window within its layout keeps
    /// the focus, so holding the key pushes one window further; moving it to another
    /// workspace does not, so holding the key sends a procession of windows after it.
    /// That distinction is what the first version of this list got wrong.
    /// </para>
    /// </remarks>
    public bool RepeatsOnHold => this is not (
        // Toggles, which flip back and forth at the hardware repeat rate.
        ToggleStickyCommand or
        ToggleTilingDirectionCommand or
        ToggleFloatingCommand or
        ToggleFullscreenCommand or
        ToggleMinimisedCommand or
        ToggleManagedCommand or
        TogglePauseCommand or
        SuspendCommand or
        ResumeCommand or
        ToggleSuspendCommand or
        CycleLayoutCommand or
        ScratchpadCommand or

        // Destructive, or expensive, or both.
        CloseWindowCommand or
        ExitCommand or
        ShellExecCommand or
        ReloadConfigCommand or
        RedrawCommand or

        // A signal asks another process to do something, most often to put a window
        // on screen. Repeating it at the hardware repeat rate asks again dozens of
        // times a second, and the client has no way to tell a held key from a user
        // who genuinely pressed it repeatedly.
        SignalCommand or

        // Commands that take the focused window off this workspace, which is the same
        // trap as close and was missed because the consequence is recoverable rather
        // than final. Once the window is gone, focus falls to whatever was next to it,
        // so the repeat acts on a different window - and the one it acts on is the
        // neighbour, which for two windows of the same application sitting side by
        // side is the one that makes it look as though the pair were being treated as
        // a unit.
        //
        // Holding the key half a second longer therefore sends two windows where one
        // was meant to go. Repeating cannot be right for any of these: there is no
        // reading of "hold to move the window to workspace three" that means "and then
        // its neighbour, and then the one after that".
        MoveToWorkspaceCommand or
        TagCommand or
        ClearTagsCommand or

        // Two monitors makes this a toggle in all but name.
        MoveWorkspaceToMonitorCommand or

        // Entering or leaving a mode repeatedly leaves which one is active a matter
        // of when the key happened to be released.
        EnableBindingModeCommand or
        DisableBindingModeCommand);
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

/// <summary><c>focus-recent-window</c></summary>
/// <remarks>
/// The window-level Alt+Tab. Pressing it twice returns you to where you started,
/// because the window being left becomes the most recent one as focus moves off it.
/// </remarks>
public sealed record FocusRecentWindowCommand : WmCommand
{
    public override string Name => "focus-recent-window";
}

/// <summary><c>focus-window 0x1D0076</c></summary>
/// <remarks>
/// <para>
/// Focus by native handle, for a caller that already knows which window it means -
/// a palette, a script, anything that has enumerated windows rather than navigated
/// to them. Every other focus command is relative to where focus already is, which
/// is no use for reaching a window you cannot see.
/// </para>
/// <para>
/// Does not target the focused window in the sense
/// <see cref="WmCommand.TargetsFocusedWindow"/> means: it names its own target, so
/// the host must not refuse it merely because something unmanaged is in front.
/// Refusing on that basis would break the one case it exists for.
/// </para>
/// </remarks>
public sealed record FocusWindowCommand(long Handle) : WmCommand
{
    public override string Name => "focus-window";
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

    public override bool TargetsFocusedWindow => true;
}

/// <summary><c>move --workspace 3</c> / <c>move --workspace 3 --focus</c></summary>
/// <param name="Workspace">Where the window is to end up.</param>
/// <param name="Focus">Whether the view follows it there.</param>
/// <remarks>
/// <para>
/// "Put this away" and "go there with it" are one keystroke apart and were once
/// expressed as two commands on one key - <c>move --workspace 3; focus --workspace
/// 3</c>. That reads well and is wrong, because the second half is indistinguishable
/// from a bare workspace switch. Press it for the workspace the window is already on
/// and the move does nothing, leaving a plain <c>focus --workspace 3</c> to be
/// answered by <c>toggle-workspace-on-refocus</c> as a re-focus: the window stayed
/// put and the screen jumped to the previous workspace, from a key whose whole
/// subject was moving a window.
/// </para>
/// <para>
/// Nothing downstream could tell the two presses apart, because the daemon runs each
/// command singly and no layer ever sees the pair. So the intention says itself here
/// instead, which is also what i3, GlazeWM and komorebi do - all three bind
/// <c>mod+shift+N</c> to one command, never to a sequence.
/// </para>
/// </remarks>
public sealed record MoveToWorkspaceCommand(string Workspace, bool Focus = false) : WmCommand
{
    public override string Name => "move-to-workspace";

    public override bool TargetsFocusedWindow => true;
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

    public override bool TargetsFocusedWindow => true;
}

/// <summary><c>sticky</c> - follow every workspace on this monitor.</summary>
public sealed record ToggleStickyCommand : WmCommand
{
    public override string Name => "sticky";

    public override bool TargetsFocusedWindow => true;
}

/// <summary><c>scratchpad --name notes</c> - stash or summon.</summary>
public sealed record ScratchpadCommand(string Slot) : WmCommand
{
    public override string Name => "scratchpad";

    public override bool TargetsFocusedWindow => true;
}

/// <summary><c>tag --clear</c></summary>
public sealed record ClearTagsCommand : WmCommand
{
    public override string Name => "tag-clear";

    public override bool TargetsFocusedWindow => true;
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

    public override bool TargetsFocusedWindow => true;
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

    public override bool TargetsFocusedWindow => true;
}

/// <summary>
/// <c>float</c> - takes the window out of the tiling flow.
/// </summary>
/// <remarks>
/// Distinct from <c>toggle-floating</c> because a rule needs to state a fact, not
/// flip a switch. A rule saying "this always floats" written as a toggle does the
/// opposite as soon as something else has already floated the window - and the
/// built-in dialog rule floats some of them before any rule runs.
/// </remarks>
public sealed record FloatCommand : WmCommand
{
    public override string Name => "float";

    public override bool TargetsFocusedWindow => true;
}

/// <summary><c>tile</c> - returns the window to the tiling flow.</summary>
public sealed record TileCommand : WmCommand
{
    public override string Name => "tile";

    public override bool TargetsFocusedWindow => true;
}

/// <summary><c>toggle-fullscreen</c>, or <c>toggle-fullscreen --monitor</c></summary>
/// <param name="WholeMonitor">
/// Whether to fill the monitor rather than the work area. The work area is what the
/// bar and taskbar have reserved, so the difference is visible exactly when
/// something is docked - which for this window manager is always, since it launches
/// its own bar.
/// </param>
public sealed record ToggleFullscreenCommand(bool WholeMonitor = false) : WmCommand
{
    public override string Name => "toggle-fullscreen";

    public override bool TargetsFocusedWindow => true;
}

/// <summary><c>toggle-minimised</c></summary>
public sealed record ToggleMinimisedCommand : WmCommand
{
    public override string Name => "toggle-minimized";

    public override bool TargetsFocusedWindow => true;
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

    public override bool TargetsFocusedWindow => true;
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

/// <summary>
/// <c>wm-suspend</c> - stop managing windows <em>and</em> let go of the keyboard.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="TogglePauseCommand"/>, and the difference is the whole
/// point. Pausing stops Shubbak rearranging the desktop but leaves the low-level
/// keyboard hook installed, so every bound chord is still swallowed and never reaches
/// the focused application. That is correct for pause - the command that resumes is a
/// keybinding, and a pause that cannot be undone from the keyboard is a trap - and it
/// is exactly wrong for the case this exists for.
/// </para>
/// <para>
/// Suspending removes the hook. The reason people reach for it is a game: an input
/// the window manager swallows is an input the game never sees, which matters far more
/// than the microsecond the hook costs. Until this existed the only way to get that
/// was to exit the window manager entirely, which un-conceals every window on every
/// workspace on the way out and takes seconds to undo.
/// </para>
/// </remarks>
public sealed record SuspendCommand : WmCommand
{
    public override string Name => "wm-suspend";
}

/// <summary><c>wm-resume</c> - undo <see cref="SuspendCommand"/>.</summary>
public sealed record ResumeCommand : WmCommand
{
    public override string Name => "wm-resume";
}

/// <summary><c>wm-toggle-suspend</c></summary>
/// <remarks>
/// The one worth binding to a key, since the key that suspends cannot be the key that
/// resumes - by the time it would be needed, the hook that would have seen it is gone.
/// Resuming is done from the resume hotkey, the CLI, or a click.
/// </remarks>
public sealed record ToggleSuspendCommand : WmCommand
{
    public override string Name => "wm-toggle-suspend";
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
/// <c>signal palette</c> - announces a named user gesture to connected clients.
/// </summary>
/// <remarks>
/// <para>
/// The extension point for user interface that is not Shubbak's. A client subscribed
/// to the <c>signal</c> topic decides what a name means; the window manager only
/// carries it, and deliberately knows nothing about what is on the other end.
/// </para>
/// <para>
/// This exists because the alternative couples the window manager to one program.
/// The obvious way to add a window palette is a <c>palette</c> command that opens
/// it - which makes the palette a feature of whichever process implements it, so
/// anyone preferring a different bar loses the palette with it. A signal keeps the
/// keybinding in the user's config, where every other key already lives, and keeps
/// the daemon ignorant of what the key is for.
/// </para>
/// <para>
/// Far weaker than <see cref="ShellExecCommand"/> and not gated the way it is. This
/// starts nothing, elevates nothing, and reaches only processes already connected to
/// a pipe scoped to the user's own account. It is a string on a topic.
/// </para>
/// </remarks>
public sealed record SignalCommand(string Signal, IReadOnlyList<string> Arguments) : WmCommand
{
    public override string Name => "signal";
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

/// <summary>
/// <c>manage</c> - takes on a window the built-in filter would have passed over.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart to <c>ignore</c>, and the reason the filter's judgements are
/// defaults rather than policy. The built-in exclusions are the ones that would
/// otherwise ruin the first five minutes - the taskbar, Start, the Win+Space
/// switcher - but they are heuristics, and some perfectly ordinary application is
/// always going to look like a palette or arrive without a title.
/// </para>
/// <para>
/// Only meaningful inside a window rule. The rules that keep the desktop itself out
/// cannot be overridden: a window manager that tiles the wallpaper is not expressing
/// a preference.
/// </para>
/// </remarks>
public sealed record ManageCommand : WmCommand
{
    public override string Name => "manage";
}

/// <summary>
/// <c>toggle-managed</c> - takes the focused window under management, or releases it.
/// </summary>
/// <remarks>
/// The runtime counterpart to the <c>manage</c> and <c>ignore</c> rules, for the
/// window in front of you that you did not think to write a rule for.
/// </remarks>
public sealed record ToggleManagedCommand : WmCommand
{
    public override string Name => "toggle-managed";
}