using Shubbak.Core.Tree;

namespace Shubbak.Core.Wm;

/// <summary>
/// Something the window manager did.
/// </summary>
/// <remarks>
/// <para>
/// Every mutation produces events. They are the single description of "what
/// changed", consumed by three very different subscribers:
/// </para>
/// <list type="bullet">
///   <item>the <b>platform layer</b>, which turns them into Win32 calls;</item>
///   <item>the <b>IPC server</b>, which forwards them to Taj and to any CLI client
///   tailing the stream;</item>
///   <item><b>tests</b>, which assert on them instead of on Win32 side effects.</item>
/// </list>
/// <para>
/// Having one event stream rather than three ad-hoc notification paths is what
/// guarantees the bar can never disagree with the window manager about the current
/// state - the defect class behind Zebar's stale window titles
/// (docs/adr/0001-language-choice.md, S4).
/// </para>
/// <para>
/// Events describe <i>state transitions</i>, not geometry. Window rectangles are
/// computed separately by <see cref="Layouts.LayoutEngine"/>, because a single
/// event such as closing a window can change the geometry of every other window on
/// the workspace, and enumerating that in the event would duplicate the layout
/// engine badly.
/// </para>
/// </remarks>
public abstract record WmEvent
{
    /// <summary>A short, stable name used as the IPC event topic.</summary>
    public abstract string Topic { get; }
}

/// <summary>A window came under management.</summary>
public sealed record WindowManaged(WindowNode Window, WorkspaceNode Workspace) : WmEvent
{
    public override string Topic => "window.managed";
}

/// <summary>
/// A window left management, either because it closed or because a rule excluded it.
/// </summary>
/// <remarks>
/// Carries the identity by value rather than the node, because by the time
/// subscribers see this the window no longer exists and the node is detached.
/// </remarks>
public sealed record WindowUnmanaged(NodeId Id, long Handle, WindowIdentity Identity) : WmEvent
{
    public override string Topic => "window.unmanaged";
}

/// <summary>Focus moved. <paramref name="Window"/> is null when nothing is focused.</summary>
public sealed record WindowFocused(WindowNode? Window, WindowNode? Previous) : WmEvent
{
    public override string Topic => "window.focused";
}

/// <summary>A window's title changed.</summary>
/// <remarks>
/// Emitted from <c>EVENT_OBJECT_NAMECHANGE</c>, which S4 showed fires roughly twice
/// as often as focus changes - including on browser tab switches, which is exactly
/// what Zebar misses. Consumers must debounce: S4 also showed titles flapping
/// through intermediate values mid-transition.
/// </remarks>
public sealed record WindowTitleChanged(WindowNode Window, string Previous) : WmEvent
{
    public override string Topic => "window.title_changed";
}

/// <summary>A window moved between tiling, floating, fullscreen, or minimised.</summary>
public sealed record WindowStateChanged(
    WindowNode Window, WindowState Previous, WindowState Current) : WmEvent
{
    public override string Topic => "window.state_changed";
}

/// <summary>A window's workspace membership changed.</summary>
/// <param name="Window">The window.</param>
/// <param name="Tags">Workspaces it now also belongs to.</param>
/// <param name="IsSticky">Whether it follows every workspace on its monitor.</param>
public sealed record WindowTagsChanged(
    WindowNode Window, IReadOnlyList<string> Tags, bool IsSticky) : WmEvent
{
    public override string Topic => "window.tags_changed";
}

/// <summary>A window changed position in the tree, possibly across workspaces.</summary>
public sealed record WindowMoved(
    WindowNode Window, WorkspaceNode? From, WorkspaceNode To) : WmEvent
{
    public override string Topic => "window.moved";
}

/// <summary>A different workspace became active on a monitor.</summary>
public sealed record WorkspaceActivated(
    WorkspaceNode Workspace, WorkspaceNode? Deactivated, MonitorNode Monitor) : WmEvent
{
    public override string Topic => "workspace.activated";
}

/// <summary>A workspace was created, usually on demand by moving a window to it.</summary>
public sealed record WorkspaceCreated(WorkspaceNode Workspace, MonitorNode Monitor) : WmEvent
{
    public override string Topic => "workspace.created";
}

/// <summary>A transient workspace was reaped after its last window left.</summary>
public sealed record WorkspaceDestroyed(NodeId Id, string Name) : WmEvent
{
    public override string Topic => "workspace.destroyed";
}

/// <summary>A workspace moved to a different monitor.</summary>
public sealed record WorkspaceMoved(
    WorkspaceNode Workspace, MonitorNode From, MonitorNode To) : WmEvent
{
    public override string Topic => "workspace.moved";
}

/// <summary>A container's layout changed.</summary>
public sealed record LayoutChanged(ContainerNode Container, string Layout) : WmEvent
{
    public override string Topic => "layout.changed";
}

/// <summary>A container's children were given new shares of its space.</summary>
/// <remarks>
/// Distinct from <see cref="LayoutChanged"/>: the layout is the same, only the
/// division of space within it moved.
/// <para>
/// It exists because resizing used to report nothing at all. The tree was updated
/// and no event was emitted, so the daemon - which marks the layout dirty from
/// events - never re-applied it. Resizing appeared to do nothing until some
/// unrelated event forced a relayout, which for most people meant switching
/// workspace and back.
/// </para>
/// </remarks>
public sealed record ContainerResized(ContainerNode Container) : WmEvent
{
    public override string Topic => "container.resized";
}

/// <summary>A monitor was attached.</summary>
public sealed record MonitorAdded(MonitorNode Monitor) : WmEvent
{
    public override string Topic => "monitor.added";
}

/// <summary>A monitor was detached; its workspaces have already been migrated.</summary>
public sealed record MonitorRemoved(NodeId Id, string DeviceId) : WmEvent
{
    public override string Topic => "monitor.removed";
}

/// <summary>A monitor's geometry, DPI or work area changed.</summary>
public sealed record MonitorChanged(MonitorNode Monitor) : WmEvent
{
    public override string Topic => "monitor.changed";
}

/// <summary>The active binding mode changed; null means the default mode.</summary>
public sealed record BindingModeChanged(string? Mode) : WmEvent
{
    public override string Topic => "binding_mode.changed";
}

/// <summary>
/// Tiling was suspended or resumed.
/// </summary>
/// <remarks>
/// <para>
/// Pausing used to change <see cref="WindowManager.IsPaused"/> and announce nothing,
/// which left every other process unable to notice. A bar could only learn about it
/// by polling <c>query state</c>, so the indicator either lagged by a whole poll
/// interval or did not exist - and a window manager that has silently stopped
/// arranging windows is precisely the state a user most needs told about, because
/// the symptom is indistinguishable from it having crashed.
/// </para>
/// <para>
/// Inert for geometry. Resuming does mark the layout dirty, but the pause path does
/// that itself, having kept the flag set for exactly this purpose - the pass is
/// deferred, not cancelled.
/// </para>
/// </remarks>
public sealed record PauseChanged(bool Paused) : WmEvent
{
    public override string Topic => "wm.paused";
}

/// <summary>
/// The window manager has let go of the keyboard, or taken it back.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="PauseChanged"/> because the two answer different
/// questions. Paused means "windows are not being rearranged"; suspended means "the
/// keyboard hook is gone", which is what somebody about to play a game cares about and
/// what pausing deliberately does not do.
/// </para>
/// <para>
/// Announced so that a bar can say so. A window manager that has stopped responding to
/// keys looks identical to one that has crashed, and the difference matters a great
/// deal to whoever is looking at it.
/// </para>
/// </remarks>
public sealed record SuspendChanged(bool Suspended) : WmEvent
{
    public override string Topic => "wm.suspended";
}

/// <summary>
/// The configuration was re-read from disk.
/// </summary>
/// <remarks>
/// Announced so that everything reading the same file can reload together. The bar is
/// a separate process reading the same config, and without this it kept whatever it
/// started with: reloading the window manager left the bar showing settings from
/// however long ago it was launched, with nothing to say so.
/// </remarks>
public sealed record ConfigReloaded(string? Path) : WmEvent
{
    public override string Topic => "config.reloaded";
}

/// <summary>
/// A request the window manager declined, with a human-readable reason.
/// </summary>
/// <remarks>
/// Emitted rather than thrown. A keybinding that cannot be satisfied - focusing
/// left from the leftmost window, say - is a completely normal event, not an error,
/// and must not interrupt the input pipeline. Surfacing it as an event lets the CLI
/// report why a command did nothing, which is otherwise very hard to diagnose.
/// </remarks>
public sealed record CommandRejected(string Command, string Reason) : WmEvent
{
    public override string Topic => "command.rejected";
}
