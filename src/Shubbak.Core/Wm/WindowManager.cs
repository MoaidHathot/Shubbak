using Shubbak.Core.Geometry;
using Shubbak.Core.Layouts;
using Shubbak.Core.Tree;

namespace Shubbak.Core.Wm;

/// <summary>
/// Tuning for window manager behaviour, mirroring the config's <c>general</c> and
/// <c>gaps</c> sections.
/// </summary>
public sealed record WmOptions
{
    /// <summary>Spacing between the workspace and the monitor work area.</summary>
    public Gaps OuterGap { get; init; }

    /// <summary>Spacing between adjacent siblings.</summary>
    public int InnerGap { get; init; }

    /// <summary>Smallest extent a tile may be given, in pixels.</summary>
    public int MinimumTileExtent { get; init; } = 24;

    /// <summary>State new windows are created in.</summary>
    public WindowState InitialWindowState { get; init; } = WindowState.Tiling;

    /// <summary>
    /// Whether focusing the already-active workspace switches back to the previous
    /// one. GlazeWM calls this <c>toggle_workspace_on_refocus</c>.
    /// </summary>
    public bool ToggleWorkspaceOnRefocus { get; init; }

    /// <summary>
    /// Whether moving a window to another workspace also moves focus there.
    /// </summary>
    /// <remarks>
    /// False by default, matching i3 and GlazeWM: "put this away" and "go there" are
    /// separate intentions, and the author's config expresses the combined one by
    /// binding two commands to a single key.
    /// </remarks>
    public bool FollowWindowOnMove { get; init; }

    public static WmOptions Default => new();

    internal ArrangeOptions ToArrangeOptions() =>
        new(OuterGap, InnerGap, MinimumTileExtent);
}

/// <summary>
/// The window manager state machine.
/// </summary>
/// <remarks>
/// <para>
/// Owns the tree, focus, and the active binding mode, and exposes every operation
/// the command layer needs. Contains no Win32, no timers and no I/O: it is a pure
/// state machine over <see cref="RootNode"/> that reports what changed through
/// <see cref="WmEvent"/>. That is what lets the whole behavioural surface -
/// including awkward cases like closing the last window on a monitor being removed
/// - be tested deterministically and in milliseconds.
/// </para>
/// <para>
/// Operations return <see cref="WmResult"/> rather than throwing. A keybinding that
/// cannot be satisfied is normal, not exceptional, and must never break the input
/// pipeline.
/// </para>
/// </remarks>
public sealed class WindowManager
{
    private readonly List<WmEvent> _pending = [];
    private readonly LayoutEngine _engine = new();

    public WindowManager(WmOptions? options = null)
    {
        Options = options ?? WmOptions.Default;
        Root = new RootNode();
    }

    public WmOptions Options { get; set; }

    public RootNode Root { get; }

    /// <summary>The window with input focus, if any.</summary>
    public WindowNode? FocusedWindow { get; private set; }

    /// <summary>
    /// The workspace commands act on: the focused window's, or the active workspace
    /// of the focused monitor when nothing is focused.
    /// </summary>
    public WorkspaceNode? FocusedWorkspace =>
        FocusedWindow?.Workspace ?? FocusedMonitor?.ActiveWorkspace;

    /// <summary>
    /// The monitor commands act on.
    /// </summary>
    /// <remarks>
    /// Tracked explicitly rather than derived from focus, so that focusing an empty
    /// workspace on another monitor still moves the point of action. Without this,
    /// switching to an empty workspace would leave subsequent commands operating on
    /// the monitor the user just left.
    /// </remarks>
    public MonitorNode? FocusedMonitor { get; private set; }

    /// <summary>The active binding mode, or null for the default set.</summary>
    public string? BindingMode { get; private set; }

    /// <summary>
    /// When true, keybindings other than the one that resumes are ignored, and
    /// window events are tracked but not acted on.
    /// </summary>
    public bool IsPaused { get; private set; }

    // ---- monitors ----------------------------------------------------------

    /// <summary>Attaches a monitor.</summary>
    public WmResult AddMonitor(MonitorNode monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);

        Root.AddMonitor(monitor);
        Emit(new MonitorAdded(monitor));

        FocusedMonitor ??= monitor;

        if (monitor.ActiveWorkspace is { } active)
            Emit(new WorkspaceActivated(active, null, monitor));

        return Complete();
    }

    /// <summary>
    /// Detaches a monitor, migrating its workspaces to another one.
    /// </summary>
    /// <remarks>
    /// Migration rather than destruction is essential. Displays disappear for
    /// mundane reasons - undocking, DisplayPort sleep, a driver restart - and
    /// discarding the workspaces would close nothing but would strand every window
    /// on them off-screen with no way to reach them.
    /// </remarks>
    public WmResult RemoveMonitor(MonitorNode monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);

        if (!Root.Monitors.Contains(monitor))
            return Reject("remove-monitor", $"Monitor {monitor.DeviceId} is not attached.");

        MonitorNode? destination = Root.Monitors.FirstOrDefault(m => !ReferenceEquals(m, monitor));

        if (destination is null)
        {
            // Removing the only monitor. Keep the tree intact; the platform layer
            // is expected to re-add a monitor before anything can be displayed.
            Root.RemoveMonitor(monitor);
            if (ReferenceEquals(FocusedMonitor, monitor)) FocusedMonitor = null;
            Emit(new MonitorRemoved(monitor.Id, monitor.DeviceId));
            return Complete();
        }

        foreach (WorkspaceNode workspace in monitor.Workspaces.ToArray())
        {
            monitor.RemoveWorkspace(workspace);
            destination.AddWorkspace(workspace);
            Emit(new WorkspaceMoved(workspace, monitor, destination));
        }

        Root.RemoveMonitor(monitor);
        Emit(new MonitorRemoved(monitor.Id, monitor.DeviceId));

        if (ReferenceEquals(FocusedMonitor, monitor))
        {
            FocusedMonitor = destination;
            SetFocus(FocusPolicy.OnWorkspaceActivated(destination.ActiveWorkspace!));
        }

        return Complete();
    }

    /// <summary>Records a change to a monitor's geometry, work area or DPI.</summary>
    public WmResult UpdateMonitor(MonitorNode monitor, Rect bounds, Rect workArea, uint dpi)
    {
        ArgumentNullException.ThrowIfNull(monitor);

        bool changed = monitor.Bounds != bounds || monitor.WorkArea != workArea || monitor.Dpi != dpi;

        monitor.Bounds = bounds;
        monitor.WorkArea = workArea;
        monitor.Dpi = dpi;

        if (changed) Emit(new MonitorChanged(monitor));
        return Complete();
    }

    // ---- workspaces --------------------------------------------------------

    /// <summary>
    /// Registers a workspace declared in config, on its preferred monitor.
    /// </summary>
    public WorkspaceNode AddWorkspace(WorkspaceNode workspace, MonitorNode? monitor = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        MonitorNode target =
            monitor
            ?? (workspace.PreferredMonitorIndex is { } index && index < Root.Monitors.Count
                ? Root.Monitors[index]
                : null)
            ?? Root.PrimaryMonitor
            ?? throw new InvalidOperationException("No monitor available to host a workspace.");

        target.AddWorkspace(workspace);
        Emit(new WorkspaceCreated(workspace, target));
        return workspace;
    }

    /// <summary>Activates a workspace by name, creating it on demand.</summary>
    public WmResult FocusWorkspace(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        WorkspaceNode? workspace = Root.FindWorkspace(name);

        if (workspace is null)
        {
            if (FocusedMonitor is null)
                return Reject("focus-workspace", "No monitor is available.");

            workspace = new WorkspaceNode(name) { IsTransient = true };
            FocusedMonitor.AddWorkspace(workspace);
            Emit(new WorkspaceCreated(workspace, FocusedMonitor));
        }

        return ActivateWorkspaceCore(workspace) ? Complete() : Failed();
    }

    /// <summary>Activates an existing workspace.</summary>
    public WmResult ActivateWorkspace(WorkspaceNode workspace) =>
        ActivateWorkspaceCore(workspace) ? Complete() : Failed();

    /// <summary>
    /// Activates a workspace, emitting events but leaving them buffered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Split out from <see cref="ActivateWorkspace"/> because several operations
    /// activate a workspace <i>and then keep going</i> - focusing a window on a
    /// hidden workspace, or moving a window with follow-on-move enabled. Calling the
    /// public method from inside those would drain the buffer early, and the events
    /// emitted before the call would be reported while the ones after were silently
    /// dropped.
    /// </para>
    /// <para>
    /// That failure mode is particularly nasty because the tree would still be
    /// correct: only the bar and IPC clients would drift out of sync, and only in
    /// composite operations. Hence the rule: an operation that continues after
    /// activating a workspace must call this, not the public wrapper.
    /// </para>
    /// </remarks>
    private bool ActivateWorkspaceCore(WorkspaceNode workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        MonitorNode? monitor = workspace.Monitor;
        if (monitor is null)
        {
            Emit(new CommandRejected("focus-workspace", $"Workspace {workspace.Name} is not on a monitor."));
            return false;
        }

        if (ReferenceEquals(monitor.ActiveWorkspace, workspace))
        {
            // Re-focusing the active workspace: either bounce to the previous one or
            // just move the point of action to this monitor.
            if (Options.ToggleWorkspaceOnRefocus &&
                monitor.PreviousWorkspace is { } previous &&
                !ReferenceEquals(previous, workspace))
            {
                return ActivateWorkspaceCore(previous);
            }

            FocusedMonitor = monitor;
            SetFocus(FocusPolicy.OnWorkspaceActivated(workspace));
            return true;
        }

        WorkspaceNode? deactivated = monitor.ActiveWorkspace;

        // Remember where focus was, so returning here is lossless.
        if (deactivated is not null && FocusedWindow?.Workspace == deactivated)
            deactivated.LastFocused = FocusedWindow;

        monitor.ActiveWorkspace = workspace;
        FocusedMonitor = monitor;

        Emit(new WorkspaceActivated(workspace, deactivated, monitor));

        SetFocus(FocusPolicy.OnWorkspaceActivated(workspace));

        if (deactivated is not null) ReapIfTransient(deactivated);

        return true;
    }

    /// <summary>Activates the workspace that previously had focus on this monitor.</summary>
    public WmResult FocusRecentWorkspace()
    {
        MonitorNode? monitor = FocusedMonitor;
        if (monitor?.PreviousWorkspace is not { } previous)
            return Reject("focus-recent-workspace", "No previous workspace on this monitor.");

        return ActivateWorkspace(previous);
    }

    /// <summary>Moves the focused workspace to the monitor in a given direction.</summary>
    public WmResult MoveWorkspaceToMonitor(Direction direction)
    {
        if (FocusedWorkspace is not { } workspace)
            return Reject("move-workspace", "No focused workspace.");

        if (workspace.Monitor is not { } from)
            return Reject("move-workspace", "Focused workspace is not on a monitor.");

        if (Root.MonitorInDirection(from, direction) is not { } to)
            return Reject("move-workspace", $"No monitor to the {direction.ToString().ToLowerInvariant()}.");

        from.RemoveWorkspace(workspace);
        to.AddWorkspace(workspace);
        to.ActiveWorkspace = workspace;

        Emit(new WorkspaceMoved(workspace, from, to));
        Emit(new WorkspaceActivated(workspace, null, to));

        FocusedMonitor = to;

        // The source monitor now shows whatever it fell back to.
        if (from.ActiveWorkspace is { } exposed)
            Emit(new WorkspaceActivated(exposed, null, from));

        return Complete();
    }

    // ---- window lifecycle --------------------------------------------------

    /// <summary>
    /// Brings a window under management, inserting it beside the focused window.
    /// </summary>
    public WmResult ManageWindow(WindowNode window, WorkspaceNode? workspace = null)
    {
        ArgumentNullException.ThrowIfNull(window);

        WorkspaceNode? target = workspace ?? FocusedWorkspace;
        if (target is null)
            return Reject("manage", "No workspace available to host the window.");

        window.State = Options.InitialWindowState;

        // Insert beside the focused window when it is on this workspace, so a new
        // window appears where the user is looking rather than at the far edge.
        WindowNode? reference = FocusedWindow?.Workspace == target ? FocusedWindow : null;
        ContainerNode container = reference?.ParentContainer ?? target;

        TreeOps.InsertByLayout(container, window, reference);

        Emit(new WindowManaged(window, target));
        SetFocus(window);

        return Complete();
    }

    /// <summary>Removes a window from management, moving focus to a neighbour.</summary>
    public WmResult UnmanageWindow(WindowNode window)
    {
        ArgumentNullException.ThrowIfNull(window);

        WorkspaceNode? workspace = window.Workspace;

        // Computed before detaching: the answer depends on the window's position
        // among its siblings.
        WindowNode? successor = ReferenceEquals(FocusedWindow, window)
            ? FocusPolicy.SuccessorFor(window)
            : null;

        TreeOps.Detach(window);

        foreach (WorkspaceNode candidate in Root.AllWorkspaces())
            if (ReferenceEquals(candidate.LastFocused, window)) candidate.LastFocused = null;

        Emit(new WindowUnmanaged(window.Id, window.Handle, window.Identity));

        if (ReferenceEquals(FocusedWindow, window)) SetFocus(successor);

        if (workspace is not null) ReapIfTransient(workspace);

        return Complete();
    }

    /// <summary>Records a title change.</summary>
    public WmResult UpdateTitle(WindowNode window, string title)
    {
        ArgumentNullException.ThrowIfNull(window);

        string previous = window.Identity.Title;
        if (string.Equals(previous, title, StringComparison.Ordinal)) return Complete();

        window.Identity = window.Identity.WithTitle(title);
        Emit(new WindowTitleChanged(window, previous));

        return Complete();
    }

    // ---- focus -------------------------------------------------------------

    /// <summary>Focuses a specific window.</summary>
    public WmResult FocusWindow(WindowNode? window)
    {
        if (window is not null && window.Workspace is { } workspace && !workspace.IsActive)
        {
            // Focusing a window on a hidden workspace implies showing it, otherwise
            // focus would sit on something invisible. Core variant, because this
            // operation continues afterwards.
            ActivateWorkspaceCore(workspace);
        }

        SetFocus(window);
        return Complete();
    }

    /// <summary>Moves focus in a direction, crossing to another monitor if needed.</summary>
    public WmResult FocusDirection(Direction direction)
    {
        if (FocusedWindow is not { } from)
            return Reject("focus", "No focused window.");

        if (FocusNavigator.Navigate(from, direction) is { } target)
        {
            SetFocus(target);
            return Complete();
        }

        // Nothing that way within the workspace, so try the adjacent monitor. This
        // is the command layer's decision rather than the navigator's, because it
        // depends on monitor geometry and activates a workspace.
        if (from.Monitor is { } monitor &&
            Root.MonitorInDirection(monitor, direction) is { } neighbour &&
            neighbour.ActiveWorkspace is { } workspace)
        {
            FocusedMonitor = neighbour;
            SetFocus(FocusPolicy.NearestTo(workspace, from.Rect));
            return Complete();
        }

        return Reject("focus", $"Nothing to the {direction.ToString().ToLowerInvariant()}.");
    }

    /// <summary>Cycles focus through the workspace's windows in tree order.</summary>
    public WmResult CycleFocus(bool forward)
    {
        if (FocusedWorkspace is not { } workspace)
            return Reject("focus-cycle", "No focused workspace.");

        WindowNode? next = FocusNavigator.Cycle(workspace, FocusedWindow, forward);
        if (next is null) return Reject("focus-cycle", "Workspace has no windows.");

        SetFocus(next);
        return Complete();
    }

    // ---- moving ------------------------------------------------------------

    /// <summary>Moves the focused window in a direction.</summary>
    public WmResult MoveDirection(Direction direction)
    {
        if (FocusedWindow is not { } window)
            return Reject("move", "No focused window.");

        if (!window.IsTiled)
            return Reject("move", "Only tiling windows can be moved directionally.");

        ContainerNode? parent = window.ParentContainer;
        if (parent is null) return Reject("move", "Focused window is not attached.");

        // Case 1: a sibling in that direction inside the current container. Swap
        // with it, which is what "move right" means among peers.
        if (parent.Layout.Navigate(parent, window, direction) is { } sibling)
        {
            if (sibling is ContainerNode targetContainer)
            {
                // Moving into a neighbouring container descends into it, so the
                // window joins that container rather than displacing it wholesale.
                int index = direction.IsForward() ? 0 : targetContainer.Count;
                parent.Remove(window);
                targetContainer.Insert(Math.Clamp(index, 0, targetContainer.Count), window);
                TreeOps.Flatten(parent);
            }
            else
            {
                parent.SwapChildren(window, sibling);
            }

            Emit(new WindowMoved(window, window.Workspace, window.Workspace!));
            return Complete();
        }

        // Case 2: nothing that way here. Escape to an ancestor that can satisfy it.
        Node current = parent;
        while (current.ParentContainer is { } ancestor)
        {
            if (ancestor.Layout.Navigate(ancestor, current, direction) is not null ||
                ancestor.Layout.PrimaryAxis == direction.Axis())
            {
                int anchor = ancestor.IndexOf(current);
                int index = direction.IsForward() ? anchor + 1 : anchor;

                WorkspaceNode? before = window.Workspace;
                ContainerNode source = window.ParentContainer!;
                source.Remove(window);
                ancestor.Insert(Math.Clamp(index, 0, ancestor.Count), window);
                TreeOps.Flatten(source);

                Emit(new WindowMoved(window, before, window.Workspace!));
                return Complete();
            }

            current = ancestor;
        }

        // Case 3: the workspace edge. Hand the window to the adjacent monitor.
        if (window.Monitor is { } monitor &&
            Root.MonitorInDirection(monitor, direction) is { } neighbour &&
            neighbour.ActiveWorkspace is { } destination)
        {
            return MoveWindowToWorkspace(window, destination);
        }

        return Reject("move", $"Nothing to the {direction.ToString().ToLowerInvariant()}.");
    }

    /// <summary>Moves the focused window to a named workspace, creating it if needed.</summary>
    public WmResult MoveToWorkspace(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (FocusedWindow is not { } window)
            return Reject("move-to-workspace", "No focused window.");

        WorkspaceNode? target = Root.FindWorkspace(name);

        if (target is null)
        {
            MonitorNode? monitor = FocusedMonitor ?? Root.PrimaryMonitor;
            if (monitor is null) return Reject("move-to-workspace", "No monitor available.");

            target = new WorkspaceNode(name) { IsTransient = true };
            monitor.AddWorkspace(target);
            Emit(new WorkspaceCreated(target, monitor));
        }

        return MoveWindowToWorkspace(window, target);
    }

    private WmResult MoveWindowToWorkspace(WindowNode window, WorkspaceNode destination)
    {
        WorkspaceNode? source = window.Workspace;
        if (ReferenceEquals(source, destination)) return Complete();

        WindowNode? successor = ReferenceEquals(FocusedWindow, window)
            ? FocusPolicy.SuccessorFor(window)
            : null;

        TreeOps.Detach(window);

        WindowNode? reference = destination.LastFocused;
        ContainerNode container = reference?.ParentContainer ?? destination;
        TreeOps.InsertByLayout(container, window, reference);

        Emit(new WindowMoved(window, source, destination));

        if (Options.FollowWindowOnMove)
        {
            ActivateWorkspaceCore(destination);
            SetFocus(window);
        }
        else if (ReferenceEquals(FocusedWindow, window))
        {
            // The window left the visible workspace, so focus must not follow it
            // into hiding.
            destination.LastFocused = window;
            SetFocus(successor);
        }

        if (source is not null) ReapIfTransient(source);

        return Complete();
    }

    // ---- sizing ------------------------------------------------------------

    /// <summary>
    /// Resizes the focused window along an axis by a fraction of its container.
    /// </summary>
    /// <param name="axis">Axis to resize along.</param>
    /// <param name="delta">
    /// Signed fraction, e.g. <c>0.02</c> for GlazeWM's <c>resize --width +2%</c>.
    /// </param>
    public WmResult Resize(Axis axis, double delta)
    {
        if (FocusedWindow is not { } window)
            return Reject("resize", "No focused window.");

        if (!window.IsTiled)
            return Reject("resize", "Only tiling windows can be resized.");

        // The window itself may not be the node that can grow: widening a window
        // inside a vertical split has to be applied at the first ancestor that
        // divides space horizontally.
        ContainerNode? container = TreeOps.NearestAncestorOnAxis(window, axis);
        if (container is null)
            return Reject("resize", $"No container splits along {axis} to resize within.");

        Node? child = TreeOps.ChildContaining(container, window);
        if (child is null) return Reject("resize", "Could not locate the resizable node.");

        container.SetChildRatio(child, child.SizeRatio + delta);
        return Complete();
    }

    /// <summary>Gives every child of the focused window's container an equal share.</summary>
    public WmResult EqualiseSiblings()
    {
        if (FocusedWindow?.ParentContainer is not { } container)
            return Reject("equalise", "No focused window.");

        container.EqualiseChildren();
        return Complete();
    }

    // ---- structure ---------------------------------------------------------

    /// <summary>Flips the focused window's container between horizontal and vertical.</summary>
    public WmResult ToggleTilingDirection()
    {
        ContainerNode? container = FocusedWindow?.ParentContainer ?? FocusedWorkspace;
        if (container is null) return Reject("toggle-tiling-direction", "No focused container.");

        ILayout layout = TreeOps.ToggleSplitDirection(container);
        Emit(new LayoutChanged(container, layout.Name));

        return Complete();
    }

    /// <summary>Wraps the focused window in a new container with the given layout.</summary>
    public WmResult Split(ILayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        if (FocusedWindow is not { } window)
            return Reject("split", "No focused window.");

        if (window.ParentContainer is null)
            return Reject("split", "Focused window is not attached.");

        ContainerNode wrapper = TreeOps.Wrap(window, layout);
        Emit(new LayoutChanged(wrapper, layout.Name));

        return Complete();
    }

    /// <summary>Sets the layout of the focused window's container.</summary>
    public WmResult SetLayout(ILayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        ContainerNode? container = FocusedWindow?.ParentContainer ?? FocusedWorkspace;
        if (container is null) return Reject("set-layout", "No focused container.");

        container.Layout = layout;
        Emit(new LayoutChanged(container, layout.Name));

        return Complete();
    }

    // ---- window state ------------------------------------------------------

    /// <summary>Sets a window's state, emitting a transition event.</summary>
    public WmResult SetWindowState(WindowNode window, WindowState state)
    {
        ArgumentNullException.ThrowIfNull(window);

        WindowState previous = window.State;
        if (previous == state) return Complete();

        // Leaving the tiling flow: remember where focus should land if this window
        // is about to become invisible.
        WindowNode? successor = state == WindowState.Minimised && ReferenceEquals(FocusedWindow, window)
            ? FocusPolicy.SuccessorFor(window)
            : null;

        if (previous == WindowState.Tiling && state != WindowState.Tiling)
            window.FloatingRect ??= window.Rect;

        window.State = state;
        Emit(new WindowStateChanged(window, previous, state));

        if (successor is not null) SetFocus(successor);

        return Complete();
    }

    /// <summary>Toggles the focused window between tiling and floating.</summary>
    public WmResult ToggleFloating()
    {
        if (FocusedWindow is not { } window)
            return Reject("toggle-floating", "No focused window.");

        return SetWindowState(
            window,
            window.State == WindowState.Floating ? WindowState.Tiling : WindowState.Floating);
    }

    /// <summary>Toggles the focused window between fullscreen and tiling.</summary>
    public WmResult ToggleFullscreen()
    {
        if (FocusedWindow is not { } window)
            return Reject("toggle-fullscreen", "No focused window.");

        return SetWindowState(
            window,
            window.State == WindowState.Fullscreen ? WindowState.Tiling : WindowState.Fullscreen);
    }

    /// <summary>Toggles the focused window's minimised state.</summary>
    public WmResult ToggleMinimised()
    {
        if (FocusedWindow is not { } window)
            return Reject("toggle-minimised", "No focused window.");

        return SetWindowState(
            window,
            window.State == WindowState.Minimised ? WindowState.Tiling : WindowState.Minimised);
    }

    // ---- modes -------------------------------------------------------------

    /// <summary>Enters a named binding mode, or returns to the default set when null.</summary>
    public WmResult SetBindingMode(string? mode)
    {
        if (string.Equals(BindingMode, mode, StringComparison.Ordinal)) return Complete();

        BindingMode = mode;
        Emit(new BindingModeChanged(mode));
        return Complete();
    }

    /// <summary>Suspends or resumes window management.</summary>
    public WmResult SetPaused(bool paused)
    {
        IsPaused = paused;
        return Complete();
    }

    // ---- output ------------------------------------------------------------

    /// <summary>Recomputes every window's target rectangle.</summary>
    public IReadOnlyList<Placement> ComputePlacements() =>
        _engine.Arrange(Root, Options.ToArrangeOptions());

    // ---- internals ---------------------------------------------------------

    private void SetFocus(WindowNode? window)
    {
        if (ReferenceEquals(FocusedWindow, window)) return;

        WindowNode? previous = FocusedWindow;
        FocusedWindow = window;

        if (window?.Workspace is { } workspace)
        {
            workspace.LastFocused = window;
            if (workspace.Monitor is { } monitor) FocusedMonitor = monitor;
        }

        Emit(new WindowFocused(window, previous));
    }

    /// <summary>
    /// Destroys a workspace that exists only because a window was put on it, once
    /// that window has gone.
    /// </summary>
    /// <remarks>
    /// Workspaces declared in config are never reaped: an empty declared workspace
    /// must survive so its keybinding keeps working.
    /// </remarks>
    private void ReapIfTransient(WorkspaceNode workspace)
    {
        if (!workspace.ShouldReap) return;

        MonitorNode? monitor = workspace.Monitor;
        if (monitor is null) return;

        NodeId id = workspace.Id;
        string name = workspace.Name;

        monitor.RemoveWorkspace(workspace);
        Emit(new WorkspaceDestroyed(id, name));
    }

    private void Emit(WmEvent wmEvent) => _pending.Add(wmEvent);

    private WmResult Reject(string command, string reason)
    {
        Emit(new CommandRejected(command, reason));
        return new WmResult(false, Drain());
    }

    /// <summary>
    /// Completes an operation whose failure was already reported by a
    /// <c>...Core</c> helper.
    /// </summary>
    private WmResult Failed() => new(false, Drain());

    private WmResult Complete() => new(true, Drain());

    private WmEvent[] Drain()
    {
        if (_pending.Count == 0) return [];

        WmEvent[] events = [.. _pending];
        _pending.Clear();
        return events;
    }
}

/// <summary>
/// The outcome of an operation: whether it did anything, and what changed.
/// </summary>
/// <param name="Succeeded">
/// False when the request could not be satisfied. Not an error - focusing left from
/// the leftmost window is entirely normal - so callers usually ignore this and just
/// forward <paramref name="Events"/>.
/// </param>
/// <param name="Events">What changed, in order.</param>
public readonly record struct WmResult(bool Succeeded, IReadOnlyList<WmEvent> Events)
{
    /// <summary>The reason a failed operation gave, if it gave one.</summary>
    public string? RejectionReason =>
        Events.OfType<CommandRejected>().FirstOrDefault()?.Reason;
}
