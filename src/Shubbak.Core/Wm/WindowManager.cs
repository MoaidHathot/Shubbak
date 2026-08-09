using Shubbak.Core.Diagnostics;
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
    /// The layout a newly created workspace starts in.
    /// </summary>
    /// <remarks>
    /// Null means the registry default, horizontal split. The configuration key that
    /// sets this was read and then never consulted, so every workspace was horizontal
    /// whatever the file said - a setting that appeared to be accepted, validated
    /// without complaint, and did nothing.
    /// </remarks>
    public ILayout? DefaultLayout { get; init; }

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
    /// <remarks>
    /// <para>
    /// Returns a result rather than the workspace, and rejects rather than throwing,
    /// like every other operation here. It used to do neither, in violation of the
    /// contract stated at the top of this class - and it is called from config
    /// loading, so a throw left the configuration half-applied: the new settings and
    /// bindings were already in place and the windows had not been reconsidered, with
    /// the whole thing surfacing as a generic "tick failed".
    /// </para>
    /// <para>
    /// The workspace is not returned because the caller supplied it; keeping a
    /// reference is theirs to do. Returning it was what let the emitted
    /// WorkspaceCreated event go undrained, so it surfaced later attached to whatever
    /// unrelated operation next completed.
    /// </para>
    /// </remarks>
    public WmResult AddWorkspace(WorkspaceNode workspace, MonitorNode? monitor = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        MonitorNode? target =
            monitor
            ?? (workspace.PreferredMonitorIndex is { } index && index < Root.Monitors.Count
                ? Root.Monitors[index]
                : null)
            ?? Root.PrimaryMonitor;

        if (target is null)
            return Reject("add-workspace", "No monitor available to host a workspace.");

        target.AddWorkspace(workspace);

        // Applied only to a workspace still holding the registry default, so a
        // workspace that has been given a layout deliberately - by config, by command,
        // or by a restored session - keeps it.
        if (Options.DefaultLayout is { } layout &&
            ReferenceEquals(workspace.Layout, LayoutRegistry.Default))
        {
            workspace.Layout = layout;
        }

        Emit(new WorkspaceCreated(workspace, target));
        return Complete();
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

        if (workspace.IsScratchpad)
        {
            // Activating it would display every stashed window at once, which is the
            // opposite of what stashing them was for.
            Emit(new CommandRejected("focus-workspace", "The scratchpad cannot be activated."));
            return false;
        }

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
            //
            // Only a genuine re-focus bounces. A workspace can be displayed on a
            // monitor the user is not looking at, and pressing its key then means "go
            // there" - never "go somewhere else entirely". Testing the workspace alone
            // sent every such press to that monitor's previous workspace instead, so
            // the keys for whichever workspaces happened to be sitting on the other
            // monitors were the ones that misbehaved.
            bool alreadyThere = ReferenceEquals(FocusedMonitor, monitor);

            if (alreadyThere &&
                Options.ToggleWorkspaceOnRefocus &&
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

        // Tagged and sticky windows follow, before focus is decided, so that
        // FocusPolicy can consider them as candidates.
        GatherTaggedWindows(workspace);

        Emit(new WorkspaceActivated(workspace, deactivated, monitor));

        SetFocus(FocusPolicy.OnWorkspaceActivated(workspace));

        if (deactivated is not null) ReapIfTransient(deactivated);

        return true;
    }

    /// <summary>
    /// Moves tagged and sticky windows into the workspace being activated.
    /// </summary>
    /// <remarks>
    /// A window cannot occupy two places on screen, so membership of several
    /// workspaces is realised by relocation: the window moves to whichever tagged
    /// workspace was most recently activated. See <see cref="WindowNode.Tags"/>.
    /// </remarks>
    private void GatherTaggedWindows(WorkspaceNode workspace)
    {
        List<WindowNode>? incoming = null;

        foreach (WindowNode window in Root.DescendantWindows())
        {
            if (!window.HasTags) continue;
            if (ReferenceEquals(window.Workspace, workspace)) continue;
            if (!window.BelongsTo(workspace)) continue;

            (incoming ??= []).Add(window);
        }

        if (incoming is null) return;

        foreach (WindowNode window in incoming)
        {
            WorkspaceNode? from = window.Workspace;

            TreeOps.Detach(window);
            TreeOps.InsertByLayout(workspace, window, workspace.LastFocused);

            Emit(new WindowMoved(window, from, workspace));

            if (from is not null) ReapIfTransient(from);
        }
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
    /// <param name="window">The window to adopt.</param>
    /// <param name="workspace">Where to put it; the focused workspace when null.</param>
    /// <param name="state">
    /// The state the caller has already determined, when it has. Null means "decide
    /// from configuration", which is what a newly opened window wants.
    /// </param>
    /// <remarks>
    /// The state is a parameter rather than something read off the node because it was
    /// previously overwritten here. A window that had been detected as minimised, or
    /// as a floating dialog, or whose state had just been read back from the saved
    /// session, was reset to the configured default the moment it was adopted.
    /// A minimised window then held a tile it could not fill - reveal refuses to
    /// restore a minimised window, correctly - and the result was a hole in the layout
    /// with whatever lay behind showing through it.
    /// </remarks>
    public WmResult ManageWindow(
        WindowNode window, WorkspaceNode? workspace = null, WindowState? state = null)
    {
        ArgumentNullException.ThrowIfNull(window);

        WorkspaceNode? target = workspace ?? FocusedWorkspace;
        if (target is null)
            return Reject("manage", "No workspace available to host the window.");

        window.State = state ?? Options.InitialWindowState;

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

        // Forgotten here as well as checked at use. The check alone would be enough
        // to stay correct, but holding a reference to a released node keeps its whole
        // subtree alive for as long as nobody presses the key.
        if (ReferenceEquals(_lastMinimised, window)) _lastMinimised = null;

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
            return FocusFromNothing(direction);

        if (FocusNavigator.Navigate(from, direction) is { } target)
        {
            SetFocus(target);
            return Complete();
        }

        // Nothing that way within the workspace, so try the adjacent monitor. This
        // is the command layer's decision rather than the navigator's, because it
        // depends on monitor geometry and activates a workspace.
        return CrossToMonitor(from.Monitor, from.Rect, direction);
    }

    /// <summary>
    /// Moves focus in a direction when nothing is focused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Focus can legitimately be nothing. Crossing onto a monitor whose active
    /// workspace is empty leaves it that way, and so does closing the last window
    /// a workspace had. Without this the only way back is the mouse: every
    /// direction command needs a focused window to navigate from, and the daemon
    /// only pulls the system's idea of focus back in when the foreground window
    /// *changes*, which pressing a key does not do. That combination stranded a
    /// real session for fourteen seconds.
    /// </para>
    /// <para>
    /// Landing on the current workspace is tried before moving, so the first
    /// keypress puts the border back rather than sending focus somewhere the user
    /// did not ask for. Only when there is nothing here to land on does the
    /// direction get used, which is what makes an empty monitor a place you can
    /// leave as well as arrive at.
    /// </para>
    /// </remarks>
    private WmResult FocusFromNothing(Direction direction)
    {
        if (FocusedWorkspace is { } workspace &&
            FocusPolicy.OnWorkspaceActivated(workspace) is { } landing)
        {
            SetFocus(landing);
            return Complete();
        }

        if (FocusedMonitor is { } monitor)
            return CrossToMonitor(monitor, monitor.Bounds, direction);

        return Reject("focus", "No focused window.");
    }

    /// <summary>
    /// Moves focus to the monitor in a direction, if there is one.
    /// </summary>
    /// <remarks>
    /// Shared by the two ways of arriving here so that crossing from a window and
    /// crossing from an empty monitor use the same geometry. The landing window may
    /// be null when the destination workspace is empty; that is allowed, and
    /// <see cref="FocusFromNothing"/> is what makes it recoverable.
    /// </remarks>
    private WmResult CrossToMonitor(MonitorNode? monitor, Rect origin, Direction direction)
    {
        if (monitor is not null &&
            Root.MonitorInDirection(monitor, direction) is { } neighbour &&
            neighbour.ActiveWorkspace is { } workspace)
        {
            FocusedMonitor = neighbour;
            SetFocus(FocusPolicy.NearestTo(workspace, origin));
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
    /// <summary>Nudges a window that is not in the tiling flow.</summary>
    /// <remarks>
    /// The step is a proportion of the work area rather than a fixed pixel count, so
    /// the same binding travels the same visible distance on a laptop panel and on a
    /// 4K monitor.
    /// </remarks>
    private WmResult MoveFloating(WindowNode window, Direction direction)
    {
        Rect area = WorkAreaFor(window);
        if (area.IsEmpty) return Reject("move", "The window is not on a monitor.");

        Rect rect = window.FloatingRect ?? window.Rect;
        if (rect.IsEmpty) return Reject("move", "The window has no rectangle to move.");

        int dx = Math.Max(1, area.Width / 20);
        int dy = Math.Max(1, area.Height / 20);

        (int offsetX, int offsetY) = direction switch
        {
            Direction.Left => (-dx, 0),
            Direction.Right => (dx, 0),
            Direction.Up => (0, -dy),
            Direction.Down => (0, dy),
            _ => (0, 0),
        };

        if (offsetX == 0 && offsetY == 0) return Reject("move", "Unknown direction.");

        window.FloatingRect = new Rect(rect.X + offsetX, rect.Y + offsetY, rect.Width, rect.Height);
        window.Rect = window.FloatingRect.Value;

        if (window.Workspace is { } workspace)
            Emit(new WindowMoved(window, workspace, workspace));

        return Complete();
    }

    /// <summary>Resizes a window that is not in the tiling flow.</summary>
    /// <remarks>
    /// The delta is a proportion of the work area, matching what it means for a tiled
    /// window - where it is a proportion of the container - so one binding reads the
    /// same way whichever kind of window is in front.
    /// </remarks>
    private WmResult ResizeFloating(WindowNode window, Axis axis, double delta)
    {
        Rect area = WorkAreaFor(window);
        if (area.IsEmpty) return Reject("resize", "The window is not on a monitor.");

        Rect rect = window.FloatingRect ?? window.Rect;
        if (rect.IsEmpty) return Reject("resize", "The window has no rectangle to resize.");

        int minimum = Math.Max(Options.MinimumTileExtent, 1);

        int width = rect.Width;
        int height = rect.Height;

        if (axis == Axis.Horizontal)
            width = Math.Max(minimum, width + (int)Math.Round(area.Width * delta));
        else
            height = Math.Max(minimum, height + (int)Math.Round(area.Height * delta));

        if (width == rect.Width && height == rect.Height)
            return Reject("resize", "The window is already at its smallest on that axis.");

        window.FloatingRect = new Rect(rect.X, rect.Y, width, height);
        window.Rect = window.FloatingRect.Value;

        if (window.Workspace is { } workspace)
            Emit(new WindowMoved(window, workspace, workspace));

        return Complete();
    }

    private static Rect WorkAreaFor(WindowNode window) =>
        window.Workspace?.Monitor?.WorkArea ?? Rect.Empty;

    public WmResult MoveDirection(Direction direction)
    {
        if (FocusedWindow is not { } window)
            return Reject("move", "No focused window.");

        // Nothing to swap with outside the tiling flow, so the window is nudged
        // instead. The alternative - refusing - left an untiled window stuck wherever
        // it happened to be unless the mouse was used.
        if (!window.IsTiled) return MoveFloating(window, direction);

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

        // Case 3: the workspace edge. Hand the window to the adjacent monitor,
        // entering from the side it arrived at - a window pushed right appears at
        // the neighbour's left edge, keeping its position relative to the cursor's
        // travel. Appending regardless would jump it to the far side of the screen.
        if (window.Monitor is { } monitor &&
            Root.MonitorInDirection(monitor, direction) is { } neighbour &&
            neighbour.ActiveWorkspace is { } destination)
        {
            return MoveWindowToWorkspace(window, destination, direction);
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

    private WmResult MoveWindowToWorkspace(
        WindowNode window, WorkspaceNode destination, Direction? enteringFrom = null)
    {
        WorkspaceNode? source = window.Workspace;
        if (ReferenceEquals(source, destination)) return Complete();

        WindowNode? successor = ReferenceEquals(FocusedWindow, window)
            ? FocusPolicy.SuccessorFor(window)
            : null;

        TreeOps.Detach(window);

        if (enteringFrom is { } direction)
        {
            // Placed at the edge the window entered by, so pushing right lands it on
            // the neighbour's left. The same rule already governs moving into a
            // neighbouring container within a workspace.
            int index = direction.IsForward() ? 0 : destination.Count;
            destination.Insert(Math.Clamp(index, 0, destination.Count), window);
        }
        else
        {
            // Beside whatever was last focused there, so a window arrives where the
            // user was working rather than at the far edge.
            //
            // Only if it is still there. LastFocused is a plain reference and a window
            // that has since been moved away keeps its place in it, so the container it
            // now lives in belongs to a different workspace entirely - and the arriving
            // window was inserted there instead. It appeared not to move at all, or to
            // trade places with something on the other monitor.
            WindowNode? reference = destination.LastFocused;

            if (reference is not null && !ReferenceEquals(reference.Workspace, destination))
            {
                destination.LastFocused = null;
                reference = null;
            }

            ContainerNode container = reference?.ParentContainer ?? destination;
            TreeOps.InsertByLayout(container, window, reference);
        }

        Emit(new WindowMoved(window, source, destination));

        // Focus follows a directional move across a monitor boundary, and only that.
        //
        // The window is still on screen there, so leaving focus behind would mean a
        // second push in the same direction moved a different window - which is the
        // whole reason for following it.
        //
        // Moving to a *named* workspace deliberately does not, even when that
        // workspace is visible. The idiom for "send it there and follow" is two
        // commands on one key - `move --workspace 3; focus --workspace 3` - and if
        // the move had already moved focus, the focus command would be re-focusing
        // the workspace it is already on. With toggle-workspace-on-refocus that
        // bounces to the previous workspace, so the key appeared to send the window
        // to 3 and then show 2.
        bool followsWindow = enteringFrom is not null && destination.IsActive;

        if (Options.FollowWindowOnMove || followsWindow)
        {
            if (!destination.IsActive) ActivateWorkspaceCore(destination);
            SetFocus(window);
        }
        else if (ReferenceEquals(FocusedWindow, window))
        {
            destination.LastFocused = window;
            SetFocus(successor);
        }

        if (source is not null) ReapIfTransient(source);

        return Complete();
    }

    // ---- tags --------------------------------------------------------------

    /// <summary>
    /// Adds, removes or toggles the focused window's membership of a workspace.
    /// </summary>
    /// <param name="workspaceName">The workspace to tag to.</param>
    /// <param name="mode">Whether to add, remove or toggle.</param>
    /// <remarks>
    /// Distinct from <see cref="MoveToWorkspace"/>: moving relocates a window,
    /// tagging makes it a member of somewhere else <i>as well</i>. The two are bound
    /// to different keys because they express different intentions - "put this away"
    /// versus "I want this here too".
    /// </remarks>
    public WmResult Tag(string workspaceName, TagMode mode)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspaceName);

        if (FocusedWindow is not { } window)
            return Reject("tag", "No focused window.");

        bool tagged = window.Tags.Contains(workspaceName);

        bool add = mode switch
        {
            TagMode.Add => true,
            TagMode.Remove => false,
            _ => !tagged,
        };

        if (add)
        {
            // Tagging a window to the workspace it already sits in is meaningless
            // and would leave a tag that can never be satisfied by relocation.
            if (string.Equals(window.Workspace?.Name, workspaceName, StringComparison.OrdinalIgnoreCase))
                return Reject("tag", $"The window is already on workspace '{workspaceName}'.");

            // The tag set records the *complete* membership, including where the
            // window currently is. Without that the relationship is one-way: the
            // window would follow to the new workspace and then have no tag for the
            // one it came from, so it could never come back.
            if (window.Workspace is { } current) window.AddTag(current.Name);

            window.AddTag(workspaceName);
        }
        else
        {
            window.RemoveTag(workspaceName);

            // A set naming only the workspace the window sits in says nothing more
            // than the default, so it is cleared rather than left as a confusing
            // remnant that shows up in the bar.
            if (window.Tags.Count <= 1) window.ClearTags();
        }

        Emit(new WindowTagsChanged(window, [.. window.Tags], window.IsSticky));
        return Complete();
    }

    /// <summary>
    /// Toggles whether the focused window follows every workspace on its monitor.
    /// </summary>
    public WmResult ToggleSticky()
    {
        if (FocusedWindow is not { } window)
            return Reject("sticky", "No focused window.");

        window.IsSticky = !window.IsSticky;

        Emit(new WindowTagsChanged(window, [.. window.Tags], window.IsSticky));
        return Complete();
    }

    /// <summary>Removes every tag from the focused window.</summary>
    public WmResult ClearTags()
    {
        if (FocusedWindow is not { } window)
            return Reject("tag", "No focused window.");

        window.ClearTags();
        window.IsSticky = false;

        Emit(new WindowTagsChanged(window, [], false));
        return Complete();
    }

    // ---- scratchpad --------------------------------------------------------

    /// <summary>
    /// The workspace name used to hold scratchpad windows.
    /// </summary>
    /// <remarks>
    /// A reserved name rather than a separate mechanism: a scratchpad is simply a
    /// workspace that is never activated, so everything that already works for
    /// workspaces - the tree, focus, layout, reaping - works for it unchanged.
    /// </remarks>
    public const string ScratchpadWorkspace = "__scratchpad";

    /// <summary>
    /// Sends the focused window to the scratchpad, or brings a scratchpad window
    /// back to the current workspace.
    /// </summary>
    /// <param name="name">
    /// Which scratchpad slot. Named slots let several windows be stashed and
    /// summoned independently, which is the difference between a scratchpad that
    /// gets used and one that does not.
    /// </param>
    public WmResult ToggleScratchpad(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        WorkspaceNode? pad = Root.FindWorkspace(ScratchpadWorkspace);

        // Summon: if the named window is already stashed, bring it here.
        if (pad is not null)
        {
            WindowNode? stashed = pad.DescendantWindows()
                .FirstOrDefault(w => string.Equals(w.ScratchpadName, name, StringComparison.OrdinalIgnoreCase));

            if (stashed is not null)
            {
                if (FocusedWorkspace is not { } destination)
                    return Reject("scratchpad", "No focused workspace to summon into.");

                TreeOps.Detach(stashed);
                stashed.ScratchpadName = null;
                stashed.State = WindowState.Floating;

                TreeOps.InsertByLayout(destination, stashed, FocusedWindow);

                Emit(new WindowMoved(stashed, pad, destination));
                SetFocus(stashed);

                return Complete();
            }
        }

        // Stash: send the focused window away under this name.
        if (FocusedWindow is not { } window)
            return Reject("scratchpad", "No focused window to stash.");

        pad ??= CreateScratchpad();
        if (pad is null) return Reject("scratchpad", "No monitor available.");

        WorkspaceNode? source = window.Workspace;
        WindowNode? successor = FocusPolicy.SuccessorFor(window);

        TreeOps.Detach(window);
        window.ScratchpadName = name;
        pad.Add(window);

        Emit(new WindowMoved(window, source, pad));
        SetFocus(successor);

        if (source is not null) ReapIfTransient(source);

        return Complete();
    }

    private WorkspaceNode? CreateScratchpad()
    {
        MonitorNode? monitor = FocusedMonitor ?? Root.PrimaryMonitor;
        if (monitor is null) return null;

        // Not transient: reaping it the moment it empties would destroy the slot
        // names the user is about to summon by.
        var pad = new WorkspaceNode(ScratchpadWorkspace) { IsTransient = false };

        monitor.AddWorkspace(pad);
        Emit(new WorkspaceCreated(pad, monitor));

        return pad;
    }

    /// <summary>Windows currently stashed, with their slot names.</summary>
    public IEnumerable<(string Name, WindowNode Window)> ScratchpadContents()
    {
        WorkspaceNode? pad = Root.FindWorkspace(ScratchpadWorkspace);
        if (pad is null) yield break;

        foreach (WindowNode window in pad.DescendantWindows())
            if (window.ScratchpadName is { } name) yield return (name, window);
    }

    // ---- dragging ----------------------------------------------------------

    /// <summary>
    /// Places a dragged window where it was dropped.
    /// </summary>
    /// <param name="window">The window that was dragged.</param>
    /// <param name="x">Cursor x, in virtual-desktop coordinates.</param>
    /// <param name="y">Cursor y.</param>
    /// <remarks>
    /// Dropping on the middle of another window swaps them; dropping near an edge
    /// inserts beside it. A drop that resolves to nothing is rejected, and the
    /// caller puts the window back - which is the honest outcome, because the
    /// alternative is guessing.
    /// </remarks>
    public WmResult DropWindow(WindowNode window, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (!window.IsTiled)
            return Reject("drop", "Only tiling windows are placed by dragging.");

        // The drop is resolved against the workspace under the cursor, not the
        // window's own, so dragging to another monitor works.
        WorkspaceNode? destination = WorkspaceAt(x, y) ?? window.Workspace;

        if (destination is null)
            return Reject("drop", "The window is not on a workspace.");

        // Moving to a different workspace has no target to land beside if that
        // workspace is empty, so handle it as a plain move first.
        if (!ReferenceEquals(destination, window.Workspace) && destination.HasNoWindows)
            return MoveWindowToWorkspace(window, destination);

        if (DragResolver.Resolve(destination, window, x, y) is not { } drop)
            return Reject("drop", "Nothing under the cursor to drop onto.");

        if (drop.Kind == DropKind.Swap)
        {
            TreeOps.Swap(window, drop.Target);
            Emit(new WindowMoved(window, window.Workspace, window.Workspace!));

            return Complete();
        }

        return InsertBeside(window, drop);
    }

    /// <summary>
    /// Inserts a dragged window beside the window it was dropped next to.
    /// </summary>
    /// <remarks>
    /// When the target's container already runs along the requested axis, this is a
    /// reparent. When it does not - dropping to the left of a window inside a
    /// vertical stack - the target is first wrapped in a new container of the right
    /// axis, which is precisely the nesting the user asked for by dropping there.
    /// </remarks>
    private WmResult InsertBeside(WindowNode window, DropTarget drop)
    {
        ContainerNode? parent = drop.Target.ParentContainer;
        if (parent is null) return Reject("drop", "The drop target is not attached.");

        WorkspaceNode? from = window.Workspace;

        if (parent.Layout.PrimaryAxis == drop.Axis)
        {
            int index = parent.IndexOf(drop.Target);
            if (index < 0) return Reject("drop", "The drop target moved.");

            // Removing the window first would shift the target's index, so the
            // adjustment is computed against the tree as it stands.
            if (ReferenceEquals(window.ParentContainer, parent) &&
                parent.IndexOf(window) < index)
            {
                index--;
            }

            TreeOps.Reparent(window, parent, drop.Kind == DropKind.Before ? index : index + 1);
        }
        else
        {
            SplitLayout layout = drop.Axis == Axis.Horizontal
                ? SplitLayout.Horizontal
                : SplitLayout.Vertical;

            // Detached first, so wrapping cannot capture the dragged window along
            // with the target when the two are already siblings.
            ContainerNode? source = window.ParentContainer;
            source?.Remove(window);

            ContainerNode wrapper = TreeOps.Wrap(drop.Target, layout);
            wrapper.Insert(drop.Kind == DropKind.Before ? 0 : wrapper.Count, window);

            if (source is not null && !ReferenceEquals(source, wrapper)) TreeOps.Flatten(source);

            Emit(new LayoutChanged(wrapper, layout.Name));
        }

        Emit(new WindowMoved(window, from, window.Workspace!));

        return Complete();
    }

    /// <summary>
    /// Applies a size change the user made by dragging a window's border.
    /// </summary>
    /// <param name="window">The resized window.</param>
    /// <param name="newRect">Its geometry after the drag.</param>
    /// <remarks>
    /// Converts the new pixel size back into the ratio the tree stores, on whichever
    /// axis actually changed. Doing it per axis matters: dragging a corner changes
    /// both, and each may be governed by a different ancestor container.
    /// </remarks>
    public WmResult ResizeFromDrag(WindowNode window, Rect newRect)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (!window.IsTiled)
            return Reject("resize", "Only tiling windows are resized by dragging.");

        Rect current = window.Rect;
        bool changed = false;

        if (Math.Abs(newRect.Width - current.Width) > 2)
            changed |= ApplyDragRatio(window, Axis.Horizontal, newRect.Width);

        if (Math.Abs(newRect.Height - current.Height) > 2)
            changed |= ApplyDragRatio(window, Axis.Vertical, newRect.Height);

        return changed ? Complete() : Reject("resize", "The drag did not change any resizable axis.");
    }

    private static bool ApplyDragRatio(WindowNode window, Axis axis, int newExtent)
    {
        ContainerNode? container = TreeOps.NearestAncestorOnAxis(window, axis);
        if (container is null) return false;

        Node? child = TreeOps.ChildContaining(container, window);
        if (child is null) return false;

        int available = container.Rect.Extent(axis);
        if (available <= 0) return false;

        // The child may be a container holding the window, in which case the window
        // occupies only part of it and the delta has to be applied to the child's
        // share rather than derived from the window's own size.
        int childExtent = child.Rect.Extent(axis);
        int windowExtent = window.Rect.Extent(axis);

        int delta = newExtent - windowExtent;
        double ratio = (double)(childExtent + delta) / available;

        container.SetChildRatio(child, ratio);
        return true;
    }

    /// <summary>The active workspace of the monitor containing a point.</summary>
    private WorkspaceNode? WorkspaceAt(int x, int y) => Root.MonitorAt(x, y)?.ActiveWorkspace;

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

        // A window outside the tiling flow has no siblings to take space from, so it
        // is resized directly. Refusing meant an untiled window could be moved nowhere
        // and resized not at all: the keyboard stopped working on it entirely, and the
        // only way to change it was the mouse.
        if (!window.IsTiled) return ResizeFloating(window, axis, delta);

        // The window itself may not be the node that can grow: widening a window
        // inside a vertical split has to be applied at the first ancestor that
        // divides space horizontally.
        ContainerNode? container = TreeOps.NearestAncestorOnAxis(window, axis);
        if (container is null)
            return Reject("resize", $"No container splits along {axis} to resize within.");

        Node? child = TreeOps.ChildContaining(container, window);
        if (child is null) return Reject("resize", "Could not locate the resizable node.");

        container.SetChildRatio(child, child.SizeRatio + delta);

        // Emitted because nothing else records that anything happened. The daemon
        // marks the layout dirty from events, so a silent mutation left the new
        // ratios sitting in the tree, unapplied, until some unrelated event forced a
        // relayout.
        Emit(new ContainerResized(container));

        return Complete();
    }

    /// <summary>Gives every child of the focused window's container an equal share.</summary>
    public WmResult EqualiseSiblings()
    {
        if (FocusedWindow?.ParentContainer is not { } container)
            return Reject("equalise", "No focused window.");

        container.EqualiseChildren();
        Emit(new ContainerResized(container));

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

    /// <summary>Advances the focused container to the next layout in the cycle.</summary>
    public WmResult CycleLayout(bool forward)
    {
        ContainerNode? container = FocusedWindow?.ParentContainer ?? FocusedWorkspace;
        if (container is null) return Reject("layout-cycle", "No focused container.");

        ILayout next = forward
            ? LayoutRegistry.Next(container.Layout)
            : LayoutRegistry.Previous(container.Layout);

        container.Layout = next;
        Emit(new LayoutChanged(container, next.Name));

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

    /// <summary>Puts the focused window into a stated state.</summary>
    /// <remarks>
    /// Separate from the toggles so a rule can assert a fact rather than flip a
    /// switch. "This application always floats" written as a toggle stops being true
    /// the moment anything else has already floated the window.
    /// </remarks>
    public WmResult SetFocusedWindowState(WindowState state)
    {
        if (FocusedWindow is not { } window)
            return Reject(state == WindowState.Floating ? "float" : "tile", "No focused window.");

        return SetWindowState(window, state);
    }

    /// <summary>Toggles the focused window between fullscreen and tiling.</summary>
    /// <param name="wholeMonitor">
    /// Fill the monitor rather than the work area, covering the bar.
    /// </param>
    /// <remarks>
    /// Each mode toggles against itself rather than against "any fullscreen", so
    /// pressing the other key while already fullscreen switches between the two
    /// rather than dropping back to tiling. Going from one to the other is the more
    /// useful reading of that keypress: someone already fullscreen who asks for the
    /// whole monitor wants more room, not their layout back.
    /// </remarks>
    public WmResult ToggleFullscreen(bool wholeMonitor = false)
    {
        if (FocusedWindow is not { } window)
            return Reject("toggle-fullscreen", "No focused window.");

        WindowState target = wholeMonitor
            ? WindowState.MonitorFullscreen
            : WindowState.Fullscreen;

        return SetWindowState(
            window,
            window.State == target ? WindowState.Tiling : target);
    }

    /// <summary>
    /// Puts the focused window away, or brings back the one put away last.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The obvious implementation - flip the focused window's state - cannot undo
    /// itself. Minimising moves focus to a neighbour, because focus cannot stay on a
    /// window that is no longer on screen, so the second press lands on a different
    /// window and minimises that one too. Pressing a toggle twice left two windows
    /// away and none of them back.
    /// </para>
    /// <para>
    /// So the command remembers what it put away and offers it back first. Press to
    /// hide, press again to return - which is what the name says and what pressing it
    /// twice ought to do.
    /// </para>
    /// <para>
    /// The cost is that two windows cannot be minimised with two presses of this key:
    /// the second press returns the first window instead. That is the right trade.
    /// Minimising is rare in a tiling window manager and every window keeps its own
    /// minimise button and taskbar entry, whereas a toggle that cannot untoggle is
    /// wrong every time it is used.
    /// </para>
    /// <para>
    /// Only ever offers back a window it put away itself, that is still away, and
    /// that is still in the tree. Restored from the taskbar, minimised by its own
    /// button, or closed - in each case the memory is stale and the press means what
    /// it plainly says instead.
    /// </para>
    /// </remarks>
    public WmResult ToggleMinimised()
    {
        if (_lastMinimised is { State: WindowState.Minimised, Workspace: not null } remembered)
        {
            _lastMinimised = null;

            WmResult restored = SetWindowState(remembered, WindowState.Tiling);

            // Focused as well as restored: it was brought back to be used, and
            // leaving focus on whatever inherited it when the window went away makes
            // the press feel like it half worked.
            SetFocus(remembered);

            return restored;
        }

        if (FocusedWindow is not { } window)
            return Reject("toggle-minimised", "No focused window.");

        if (window.State == WindowState.Minimised)
        {
            _lastMinimised = null;
            return SetWindowState(window, WindowState.Tiling);
        }

        _lastMinimised = window;
        return SetWindowState(window, WindowState.Minimised);
    }

    /// <summary>
    /// The window <see cref="ToggleMinimised"/> last put away, if it is still away.
    /// </summary>
    private WindowNode? _lastMinimised;

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
        _engine.Arrange(Root, Options.ToArrangeOptions() with { Focused = FocusedWindow });

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
        else if (window is null)
        {
            // Worth a line, because losing focus is otherwise invisible. A command
            // that clears it still reports success, so the log showed a focus
            // keybinding working normally and then every later command refusing,
            // with nothing in between to connect the two.
            Log.Debug(LogCategory.Wm, "focus cleared");
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
