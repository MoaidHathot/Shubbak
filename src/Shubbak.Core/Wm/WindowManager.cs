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

    /// <summary>
    /// Counts focus changes, so windows can be ordered by how recently they had it.
    /// </summary>
    /// <remarks>
    /// Stamped onto <see cref="WindowNode.FocusSequence"/> by <c>SetFocus</c>. A
    /// <c>long</c> at one increment per focus change will not wrap in any number of
    /// human lifetimes, so nothing needs to handle it doing so.
    /// </remarks>
    private long _focusClock;

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

        MonitorNode? target = monitor ?? HomeOf(workspace) ?? Root.PrimaryMonitor;

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

    /// <summary>
    /// Focuses the window that most recently had focus before this one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The window-level counterpart of <see cref="FocusRecentWorkspace"/>, and the
    /// behaviour Alt+Tab is usually wanted for: pressing it twice returns you to
    /// where you started, because the window being left becomes the most recent one
    /// the moment focus moves off it.
    /// </para>
    /// <para>
    /// Global rather than per-workspace. A window is most easily lost when it is not
    /// where you are looking, so restricting the search to the current workspace
    /// would exclude exactly the cases worth having this for. Focusing a window on a
    /// hidden workspace shows that workspace, which <see cref="FocusWindow"/> already
    /// handles.
    /// </para>
    /// <para>
    /// Minimised windows are skipped. They have a focus history like anything else,
    /// but "return me to what I was just using" should not mean restoring something
    /// the user deliberately put away - and a minimised window that is focused
    /// without being restored is focus on something invisible.
    /// </para>
    /// </remarks>
    public WmResult FocusRecentWindow()
    {
        WindowNode? best = null;

        foreach (WindowNode candidate in Root.DescendantWindows())
        {
            if (ReferenceEquals(candidate, FocusedWindow)) continue;
            if (candidate.State is WindowState.Minimised) continue;
            if (candidate.FocusSequence == 0) continue;

            if (best is null || candidate.FocusSequence > best.FocusSequence) best = candidate;
        }

        if (best is null)
            return Reject("focus-recent-window", "No other window has been focused yet.");

        return FocusWindow(best);
    }

    /// <summary>
    /// Focuses a window by its native handle, wherever it is.
    /// </summary>
    /// <remarks>
    /// For callers that already know which window they mean - a palette, a script,
    /// anything holding a handle rather than a direction. Returns a rejection when
    /// the handle names nothing in the tree, which lets the host decide whether to
    /// try harder: an unmanaged or orphaned window is not in the tree at all and
    /// needs reviving before it can be focused.
    /// </remarks>
    public WmResult FocusWindowByHandle(long handle)
    {
        if (Root.FindWindow(handle) is not { } window)
            return Reject("focus-window", $"No managed window with handle {handle}.");

        // A minimised window cannot usefully take focus: it is not on screen, so the
        // foreground would go to something the user cannot see. Restoring first is
        // what "focus this" has to mean for it.
        //
        // Core, because FocusWindow completes the operation and both changes must be
        // reported together.
        if (window.State is WindowState.Minimised)
            SetWindowStateCore(window, WindowState.Tiling);

        return FocusWindow(window);
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

        return MoveWorkspaceToMonitor(to);
    }

    /// <summary>
    /// Moves the focused workspace to a particular monitor, and looks at it there.
    /// </summary>
    /// <remarks>
    /// A user gesture, so the moved workspace becomes the one shown on the destination
    /// and focus goes with it - the same as moving by direction. Automatic moves, which
    /// must not steal what a monitor is showing, go through
    /// <see cref="RehomeWorkspaces"/> instead.
    /// </remarks>
    public WmResult MoveWorkspaceToMonitor(MonitorNode to)
    {
        ArgumentNullException.ThrowIfNull(to);

        if (FocusedWorkspace is not { } workspace)
            return Reject("move-workspace", "No focused workspace.");

        if (workspace.Monitor is not { } from)
            return Reject("move-workspace", "Focused workspace is not on a monitor.");

        if (!Root.Monitors.Contains(to))
            return Reject("move-workspace", $"Monitor {to.DeviceId} is not attached.");

        // Refused rather than quietly re-activated, so a key bound to "send this to the
        // left screen" pressed while already there reports what happened.
        if (ReferenceEquals(from, to))
            return Reject("move-workspace", $"Workspace '{workspace.Name}' is already on {to.DeviceId}.");

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

    /// <summary>
    /// Puts every workspace back on the monitor it belongs to, where that monitor is
    /// attached and the workspace is somewhere else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The missing half of monitor removal. Unplugging a display migrates its
    /// workspaces to a survivor, which is right; plugging it back in used to leave them
    /// there, because a workspace's preference was consulted only when it was created.
    /// Every dock and undock therefore ended with a round of moving workspaces back by
    /// hand, which is the chore a preference exists to remove.
    /// </para>
    /// <para>
    /// Automatic, so it must not change what the user is looking at more than the move
    /// itself requires. A workspace that was being shown stays shown - on its new
    /// monitor, which is the point of binding it there - and one that was not stays
    /// out of sight. A monitor that had nothing to show gets the first arrival. Focus
    /// follows only the workspace that held it.
    /// </para>
    /// <para>
    /// Nothing happens for a workspace already at home, and a call that moves nothing
    /// produces no events, so this is cheap to run on every reconciliation.
    /// </para>
    /// </remarks>
    public WmResult RehomeWorkspaces()
    {
        // Snapshotted, because moving mutates the lists being walked.
        List<(WorkspaceNode Workspace, MonitorNode From, MonitorNode To)> moves = [];

        foreach (MonitorNode from in Root.Monitors)
        {
            foreach (WorkspaceNode workspace in from.Workspaces)
            {
                // The scratchpad lives wherever it was made and is never shown, so
                // there is nothing to put right.
                if (workspace.IsScratchpad) continue;

                if (HomeOf(workspace) is not { } to) continue;
                if (ReferenceEquals(to, from)) continue;

                moves.Add((workspace, from, to));
            }
        }

        foreach ((WorkspaceNode workspace, MonitorNode from, MonitorNode to) in moves)
        {
            bool wasShown = workspace.IsActive;
            bool heldFocus = ReferenceEquals(FocusedWorkspace, workspace);
            WorkspaceNode? previouslyShownOnTo = to.ActiveWorkspace;

            from.RemoveWorkspace(workspace);
            to.AddWorkspace(workspace);

            Emit(new WorkspaceMoved(workspace, from, to));

            // AddWorkspace makes the first arrival active on an empty monitor; a
            // workspace that was on screen takes the destination's screen as well.
            if (wasShown) to.ActiveWorkspace = workspace;

            if (ReferenceEquals(to.ActiveWorkspace, workspace) && !ReferenceEquals(previouslyShownOnTo, workspace))
                Emit(new WorkspaceActivated(workspace, previouslyShownOnTo, to));

            if (wasShown && from.ActiveWorkspace is { } exposed)
                Emit(new WorkspaceActivated(exposed, workspace, from));

            if (heldFocus) FocusedMonitor = to;
        }

        return Complete();
    }

    /// <summary>
    /// The attached monitor a workspace belongs on, or null when it has no opinion or
    /// its home is not attached.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A name first, then a position. The name is whatever the configuration wrote in
    /// <c>monitor=</c>: a declared <c>monitor "name"</c>, which the host has already
    /// resolved onto <see cref="MonitorNode.Names"/>, or one of the positional
    /// spellings <see cref="MonitorReference"/> reads for itself. This class never sees
    /// the configuration; it sees what the host wrote on the nodes.
    /// </para>
    /// <para>
    /// A declared name can fit more than one display - two of the same model report the
    /// same friendly name - and the first in enumeration order is taken. Telling twins
    /// apart is what the device path matcher is for.
    /// </para>
    /// </remarks>
    public MonitorNode? HomeOf(WorkspaceNode workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        if (workspace.PreferredMonitorName is { Length: > 0 } name)
        {
            foreach (MonitorNode monitor in Root.Monitors)
                if (monitor.IsNamed(name)) return monitor;

            if (MonitorReference.Resolve(Root, name) is { } positional) return positional;
        }

        if (workspace.PreferredMonitorIndex is { } index && index >= 0 && index < Root.Monitors.Count)
            return Root.Monitors[index];

        return null;
    }

    /// <summary>
    /// Finds a monitor by any reference a command accepts: a name the configuration
    /// gives it, a position counted from zero, or a GDI device name.
    /// </summary>
    public MonitorNode? FindMonitor(string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);

        foreach (MonitorNode monitor in Root.Monitors)
            if (monitor.IsNamed(reference)) return monitor;

        return MonitorReference.Resolve(Root, reference);
    }

    /// <summary>Moves the focused workspace to the monitor a reference names.</summary>
    public WmResult MoveWorkspaceToMonitor(string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);

        if (FindMonitor(reference) is { } monitor) return MoveWorkspaceToMonitor(monitor);

        // The refusal names what would have worked, because the one thing certain about
        // a reference that resolved to nothing is that the person typing it thought it
        // would.
        List<string> known = [];

        for (int index = 0; index < Root.Monitors.Count; index++)
        {
            MonitorNode attached = Root.Monitors[index];
            string names = attached.Names.Count > 0 ? $" ({string.Join(", ", attached.Names)})" : "";
            known.Add($"{index} = {attached.DeviceId}{names}");
        }

        return Reject(
            "move-workspace",
            $"No monitor called '{reference}'. Attached: {(known.Count > 0 ? string.Join("; ", known) : "none")}.");
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
    /// <param name="name">The workspace to move it to.</param>
    /// <param name="focus">
    /// Whether the view follows the window there. The per-command form of
    /// <see cref="WmOptions.FollowWindowOnMove"/>, so that one key can mean "send it
    /// there and go with it" without needing a second command to say the second half.
    /// </param>
    public WmResult MoveToWorkspace(string name, bool focus = false)
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

        return MoveWindowToWorkspace(window, target, focus: focus);
    }

    private WmResult MoveWindowToWorkspace(
        WindowNode window, WorkspaceNode destination,
        Direction? enteringFrom = null, bool focus = false)
    {
        WorkspaceNode? source = window.Workspace;

        if (ReferenceEquals(source, destination))
        {
            // Already where it was asked to go, so the request is satisfied rather
            // than refused - and satisfied means nothing else happens either. In
            // particular the view does not move, which is the entire reason --focus
            // belongs to this command instead of being a second `focus --workspace`
            // after it: that second command could not tell "and follow it" from "I am
            // already here", and with toggle-workspace-on-refocus answered the wrong
            // one.
            if (focus) SetFocus(window);
            return Complete();
        }

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

        // Focus follows the window when it is asked to, and when a directional move
        // carries it across a monitor boundary.
        //
        // The directional case is not optional. The window is still on screen there,
        // so leaving focus behind would mean a second push in the same direction moved
        // a different window - which is the whole reason for following it.
        //
        // Moving to a *named* workspace does not follow unless told to, even when that
        // workspace is visible: "put this away" and "go there with it" are separate
        // intentions and belong on separate keys. `--focus` is how one key says the
        // second, and `follow-window-on-move` is how a config says it for every key at
        // once.
        bool followsWindow = enteringFrom is not null && destination.IsActive;

        if (focus || Options.FollowWindowOnMove || followsWindow)
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

    /// <summary>Whether a window is stashed under this slot name.</summary>
    /// <remarks>
    /// <para>
    /// Asked before a command runs, to tell a summon from a stash. The two halves of
    /// <see cref="ToggleScratchpad"/> have opposite requirements: stashing needs a
    /// focused window to send away, and summoning needs only somewhere to put one.
    /// </para>
    /// <para>
    /// Without this the daemon refused the summon. Every command that declares
    /// <c>TargetsFocusedWindow</c> is checked against the foreground window first, and
    /// stashing the last window on a workspace leaves nothing focused - so the key
    /// that put a window away was refused when pressed again to fetch it back, and the
    /// scratchpad became one-way. Reported as "it vanishes, and pressing it again does
    /// nothing".
    /// </para>
    /// </remarks>
    /// <param name="name">The slot name.</param>
    public bool IsScratchpadOccupied(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        // Case-insensitive, to agree with the lookup in ToggleScratchpad. Disagreeing
        // would refuse exactly the summons that would have succeeded.
        return ScratchpadContents()
            .Any(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));
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
    /// <para>
    /// Manual split is the one layout in which the tree <i>is</i> the layout, so a
    /// drop is read against its axis. Along it, this is a reparent. Across it -
    /// dropping to the left of a window inside a vertical stack - the target is first
    /// wrapped in a new split of the right axis, which is precisely the nesting the
    /// user asked for by dropping there.
    /// </para>
    /// <para>
    /// Every other layout places its children by their order and decides the geometry
    /// itself: the spiral, the grid and master-stack each take a flat list and put the
    /// first child where the layout says the first child goes. A drop into one of
    /// those can choose the order - leading edge before the target, trailing edge
    /// after - and nothing else, because there is no axis of the container's for the
    /// drop to agree or disagree with. Wrapping the target in a split there put a
    /// hand-made split inside an automatic layout, with its axis taken from whichever
    /// edge the cursor happened to be nearest; and when the workspace had held one
    /// window, that split became its only child and took over. A window dragged onto
    /// a <c>fibonacci-v</c> monitor made it <c>splitv</c> or <c>splith</c>, decided by
    /// the mouse, and the layout chosen for that monitor was gone.
    /// </para>
    /// </remarks>
    private WmResult InsertBeside(WindowNode window, DropTarget drop)
    {
        ContainerNode? parent = drop.Target.ParentContainer;
        if (parent is null) return Reject("drop", "The drop target is not attached.");

        WorkspaceNode? from = window.Workspace;

        if (parent.Layout is not SplitLayout split || split.Axis == drop.Axis)
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

    /// <summary>
    /// Puts the focused workspace's windows back into a recorded tree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the windows on the workspace, and only the ones that tile. An arrangement
    /// says how a workspace is divided, not which windows belong on it; pulling a
    /// window in from another workspace because it matched would take something the
    /// user put there on purpose. So each recorded window is matched against what is
    /// here - process and class, with the title and the path breaking ties, each
    /// window claimed once - and a recorded window that is not here is left out, its
    /// share going to its siblings. A container left with one child by that is
    /// replaced by the child, as the tree would do itself.
    /// </para>
    /// <para>
    /// Windows on the workspace that the arrangement does not mention stay, after the
    /// rebuilt tree, each with the share it would have had as one more child. Nothing
    /// is hidden and nothing is moved off the workspace; the worst a restore can do to
    /// a window it does not know is put it last.
    /// </para>
    /// <para>
    /// Refused, rather than done as a no-op, when nothing recorded is here: a restore
    /// that quietly rearranged nothing would leave the person pressing the key again.
    /// </para>
    /// </remarks>
    public WmResult RestoreArrangement(Arrangement arrangement)
    {
        ArgumentNullException.ThrowIfNull(arrangement);

        WorkspaceNode? workspace = FocusedWorkspace;
        if (workspace is null) return Reject("arrangement", "No focused workspace.");

        List<WindowNode> candidates = [];

        foreach (WindowNode window in workspace.DescendantWindows())
            if (window.ParticipatesInTiling) candidates.Add(window);

        if (candidates.Count == 0)
            return Reject("arrangement", $"Nothing tiles on workspace \"{workspace.Name}\", so there is nothing to arrange.");

        // Match in recorded order, best fit first, each window once. Greedy is enough:
        // the ambiguity is between windows of one program, and the title hash and the
        // path are there to settle it.
        Dictionary<ArrangementNode, WindowNode> placed = new(ReferenceEqualityComparer.Instance);
        HashSet<WindowNode> claimed = [];
        int missing = 0;

        foreach (ArrangementNode leaf in Leaves(arrangement.Children))
        {
            WindowNode? best = null;
            int bestScore = 0;

            foreach (WindowNode candidate in candidates)
            {
                if (claimed.Contains(candidate)) continue;

                int score = leaf.Score(candidate);
                if (score > bestScore) (best, bestScore) = (candidate, score);
            }

            if (best is null)
            {
                missing++;
                continue;
            }

            placed[leaf] = best;
            claimed.Add(best);
        }

        if (placed.Count == 0)
        {
            return Reject("arrangement",
                $"Nothing in \"{arrangement.Name}\" is open on workspace \"{workspace.Name}\".");
        }

        // Take the matched windows out first, letting the tree tidy what they leave,
        // then build the recorded shape from them and put it at the front.
        foreach (WindowNode window in placed.Values) TreeOps.Detach(window);

        List<(Node Node, double Ratio)> rebuilt = [];

        foreach (ArrangementNode recorded in arrangement.Children)
            if (Build(recorded, placed) is { } built) rebuilt.Add((built, recorded.Ratio));

        int kept = workspace.Children.Count;

        for (int i = 0; i < rebuilt.Count; i++) workspace.Insert(i, rebuilt[i].Node);

        if (LayoutRegistry.TryResolve(arrangement.Layout, out ILayout? layout)) workspace.Layout = layout;

        // The recorded shares, scaled to leave room for what was kept, which shares
        // the rest in the proportions it already had. Every child of the workspace
        // gets a share as if it had always been one of n + k.
        Span<double> ratios = workspace.Children.Count <= 64
            ? stackalloc double[workspace.Children.Count]
            : new double[workspace.Children.Count];

        double recordedTotal = 0;
        foreach ((_, double ratio) in rebuilt) recordedTotal += ratio;

        double keptTotal = 0;
        for (int i = rebuilt.Count; i < workspace.Children.Count; i++) keptTotal += workspace.Children[i].SizeRatio;

        double recordedShare = (double)rebuilt.Count / (rebuilt.Count + kept);

        for (int i = 0; i < rebuilt.Count; i++)
            ratios[i] = recordedTotal > 0 ? rebuilt[i].Ratio / recordedTotal * recordedShare : recordedShare / rebuilt.Count;

        for (int i = rebuilt.Count; i < workspace.Children.Count; i++)
        {
            ratios[i] = keptTotal > 0
                ? workspace.Children[i].SizeRatio / keptTotal * (1 - recordedShare)
                : (1 - recordedShare) / kept;
        }

        workspace.SetRatios(ratios);

        Emit(new LayoutChanged(workspace, workspace.Layout.Name));
        Emit(new ArrangementRestored(arrangement.Name, workspace.Name, placed.Count, missing, kept));

        return Complete();
    }

    /// <summary>
    /// Builds one recorded node from the windows matched to it, or null when none of
    /// its windows are here.
    /// </summary>
    private static Node? Build(ArrangementNode recorded, Dictionary<ArrangementNode, WindowNode> placed)
    {
        if (!recorded.IsContainer)
            return placed.TryGetValue(recorded, out WindowNode? window) ? window : null;

        List<(Node Node, double Ratio)> children = [];

        foreach (ArrangementNode child in recorded.Children ?? [])
            if (Build(child, placed) is { } built) children.Add((built, child.Ratio));

        switch (children.Count)
        {
            case 0:
                return null;

            case 1:
                // A container with one child is the child; the tree would flatten it.
                return children[0].Node;

            default:
                var container = new ContainerNode(
                    LayoutRegistry.TryResolve(recorded.Layout!, out ILayout? layout) ? layout : LayoutRegistry.Default);

                Span<double> ratios = children.Count <= 64 ? stackalloc double[children.Count] : new double[children.Count];

                for (int i = 0; i < children.Count; i++)
                {
                    container.Add(children[i].Node);
                    ratios[i] = children[i].Ratio;
                }

                container.SetRatios(ratios);
                return container;
        }
    }

    private static IEnumerable<ArrangementNode> Leaves(IReadOnlyList<ArrangementNode> nodes)
    {
        foreach (ArrangementNode node in nodes)
        {
            if (!node.IsContainer)
            {
                yield return node;
                continue;
            }

            foreach (ArrangementNode leaf in Leaves(node.Children ?? [])) yield return leaf;
        }
    }

    // ---- window state ------------------------------------------------------

    /// <summary>Sets a window's state, emitting a transition event.</summary>
    public WmResult SetWindowState(WindowNode window, WindowState state)
    {
        ArgumentNullException.ThrowIfNull(window);

        SetWindowStateCore(window, state);
        return Complete();
    }

    /// <summary>
    /// Sets a window's state without completing the operation.
    /// </summary>
    /// <remarks>
    /// For callers that continue afterwards. <see cref="Complete"/> drains the
    /// pending events, so an operation that calls the public entry point and then
    /// does more work reports only the second half - the state change would be
    /// applied to the tree and never announced to the bar or the layout pass.
    /// </remarks>
    private void SetWindowStateCore(WindowNode window, WindowState state)
    {
        WindowState previous = window.State;
        if (previous == state) return;

        // Leaving the tiling flow: remember where focus should land if this window
        // is about to become invisible.
        WindowNode? successor = state == WindowState.Minimised && ReferenceEquals(FocusedWindow, window)
            ? FocusPolicy.SuccessorFor(window)
            : null;

        if (previous == WindowState.Tiling && state != WindowState.Tiling)
            window.FloatingRect ??= window.Rect;

        // Going away from a state the window lives in: that is the state it comes back
        // to. Going from one away state to another - minimised while fullscreen,
        // fullscreen in both sizes - keeps the earlier memory, since neither of those
        // is somewhere to come back to.
        if (!WindowNode.IsAway(previous) && WindowNode.IsAway(state))
            window.StateBeforeAway = previous;

        // Any deliberate state change ends the observation. It was only ever an
        // observation about a tiled or floating window, and once the user has said
        // what this window should be, the layout follows that instead. Leaving it set
        // would hand the monitor to a window that had since been asked to be
        // something else - and the watch re-reads the rectangle within a fraction of
        // a second, so an application that really is still full-screen is noticed
        // again immediately.
        window.IsNativeFullscreen = false;

        window.State = state;
        Emit(new WindowStateChanged(window, previous, state));

        if (successor is not null) SetFocus(successor);
    }

    /// <summary>
    /// Brings a window back from an away state to the one it was in before: tiling, or
    /// floating. A window that is not away is left as it is.
    /// </summary>
    /// <remarks>
    /// The one way back from minimised and from fullscreen. Every caller used to write
    /// <see cref="WindowState.Tiling"/> here, which tiled a floating dialog for having
    /// been minimised; see <see cref="WindowNode.StateBeforeAway"/>.
    /// </remarks>
    public WmResult RestoreFromAway(WindowNode window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (WindowNode.IsAway(window.State))
            SetWindowStateCore(window, window.StateBeforeAway);

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

        return window.State == target
            ? RestoreFromAway(window)
            : SetWindowState(window, target);
    }

    /// <summary>
    /// Toggles the focused window between maximised and the state it was in.
    /// </summary>
    /// <remarks>
    /// Maximised is fullscreen with the window's own frame: it fills the work area,
    /// covers its siblings, and keeps its title bar and the bar above it, which is what
    /// somebody who presses the maximise button in a tiling window manager was asking
    /// for. Owned by the tree rather than by Windows: the window is placed at the work
    /// area like a fullscreen one, so the committer's rule that a natively maximised
    /// window is drift still holds and Win+Up still tiles the window back.
    /// </remarks>
    public WmResult ToggleMaximised()
    {
        if (FocusedWindow is not { } window)
            return Reject("toggle-maximised", "No focused window.");

        return window.State == WindowState.Maximised
            ? RestoreFromAway(window)
            : SetWindowState(window, WindowState.Maximised);
    }

    /// <summary>
    /// Focuses another monitor: whatever its active workspace was last looking at.
    /// </summary>
    /// <param name="direction">The monitor that way from the focused one, or null to use the reference.</param>
    /// <param name="reference">A name from the configuration, a position from zero, or a device name.</param>
    /// <remarks>
    /// <c>focus --direction right</c> crosses to the next monitor only when nothing
    /// within the workspace is to the right, which from the left half of a screen is
    /// two presses and from a monocle workspace is never. This is the one that says
    /// the monitor and means it.
    /// </remarks>
    public WmResult FocusMonitor(Direction? direction, string? reference)
    {
        (MonitorNode? target, WmResult? refusal) = ResolveMonitor("focus", direction, reference);
        if (target is null) return refusal!.Value;

        if (target.ActiveWorkspace is not { } workspace)
            return Reject("focus", $"{target.DeviceId} is showing no workspace.");

        FocusedMonitor = target;
        SetFocus(FocusPolicy.OnWorkspaceActivated(workspace));

        return Complete();
    }

    /// <summary>Moves the focused window to another monitor's active workspace.</summary>
    /// <param name="direction">The monitor that way from the window's, or null to use the reference.</param>
    /// <param name="reference">A name from the configuration, a position from zero, or a device name.</param>
    /// <param name="focus">Whether the view follows the window there.</param>
    public WmResult MoveToMonitor(Direction? direction, string? reference, bool focus = false)
    {
        if (FocusedWindow is not { } window)
            return Reject("move", "No focused window.");

        (MonitorNode? target, WmResult? refusal) = ResolveMonitor("move", direction, reference, window.Monitor);
        if (target is null) return refusal!.Value;

        if (target.ActiveWorkspace is not { } destination)
            return Reject("move", $"{target.DeviceId} is showing no workspace.");

        if (ReferenceEquals(window.Workspace, destination))
            return Reject("move", $"The window is already on {target.DeviceId}.");

        return MoveWindowToWorkspace(window, destination, focus: focus);
    }

    /// <summary>The monitor a direction or a reference names, or why there is none.</summary>
    private (MonitorNode? Monitor, WmResult? Refusal) ResolveMonitor(
        string verb, Direction? direction, string? reference, MonitorNode? from = null)
    {
        if (direction is { } way)
        {
            MonitorNode? origin = from ?? FocusedMonitor ?? Root.PrimaryMonitor;

            if (origin is null) return (null, Reject(verb, "No monitor is attached."));

            return Root.MonitorInDirection(origin, way) is { } neighbour
                ? (neighbour, null)
                : (null, Reject(verb, $"No monitor to the {way.ToString().ToLowerInvariant()}."));
        }

        if (reference is { Length: > 0 })
        {
            if (FindMonitor(reference) is { } named) return (named, null);

            List<string> known = [];

            for (int index = 0; index < Root.Monitors.Count; index++)
            {
                MonitorNode attached = Root.Monitors[index];
                string names = attached.Names.Count > 0 ? $" ({string.Join(", ", attached.Names)})" : "";
                known.Add($"{index} = {attached.DeviceId}{names}");
            }

            return (null, Reject(verb, $"No monitor called '{reference}'. Attached: {(known.Count > 0 ? string.Join("; ", known) : "none")}."));
        }

        return (null, Reject(verb, "No monitor was named."));
    }

    /// <summary>
    /// Exchanges the focused window with its neighbour in a direction, wherever in the
    /// tree that neighbour is.
    /// </summary>
    /// <remarks>
    /// <c>move</c> among siblings is a swap already, but a move into a neighbouring
    /// container joins it, and a move past the workspace edge changes monitor. Swap
    /// never changes the shape of the tree: two windows trade places and everything
    /// else stays where it was, which is the gesture for "these two are the wrong way
    /// round" in a layout that took a while to arrange.
    /// </remarks>
    public WmResult SwapDirection(Direction direction)
    {
        if (FocusedWindow is not { } window)
            return Reject("swap", "No focused window.");

        if (!window.IsTiled)
            return Reject("swap", "Only a tiled window has a place to swap.");

        if (FocusNavigator.Navigate(window, direction) is not { } other)
            return Reject("swap", $"Nothing to the {direction.ToString().ToLowerInvariant()} to swap with.");

        if (!other.IsTiled)
            return Reject("swap", "The window that way is not tiled.");

        TreeOps.Swap(window, other);

        Emit(new WindowMoved(window, window.Workspace, window.Workspace!));
        Emit(new WindowMoved(other, other.Workspace, other.Workspace!));

        return Complete();
    }

    /// <summary>
    /// Changes the gaps at runtime, by a signed amount or to an absolute one.
    /// </summary>
    /// <param name="inner">The change to the gap between windows, or null to leave it.</param>
    /// <param name="outer">The change to the gap around the edge, applied to all four sides, or null.</param>
    /// <param name="absolute">Whether the amounts replace the gaps rather than add to them.</param>
    /// <remarks>
    /// The gaps are the configuration's until the file is reloaded, which resets them:
    /// this is for the key that closes the gaps up for a screen-share and opens them
    /// again after, not for a second place to write the setting. Never below zero,
    /// and never so large that nothing is left to tile; the layout's own minimum
    /// extent already refuses that, but a gap of a thousand is a mistake worth
    /// catching at the command.
    /// </remarks>
    public WmResult AdjustGaps(int? inner, int? outer, bool absolute = false)
    {
        if (inner is null && outer is null)
            return Reject("gaps", "Nothing to change; give --inner or --outer.");

        // Below zero is read as zero - "close the gaps up" is the whole point of a
        // negative change - but past the ceiling is refused rather than clamped: a
        // gap of several hundred pixels is a typo, and a typo that lands on the
        // ceiling is a screen with no room to tile anything.
        const int Most = 200;

        int newInner = inner is { } i ? Math.Max(0, absolute ? i : Options.InnerGap + i) : Options.InnerGap;

        Gaps current = Options.OuterGap;
        Gaps newOuter = outer is { } o
            ? new Gaps(
                Math.Max(0, absolute ? o : current.Left + o),
                Math.Max(0, absolute ? o : current.Top + o),
                Math.Max(0, absolute ? o : current.Right + o),
                Math.Max(0, absolute ? o : current.Bottom + o))
            : current;

        if (newInner > Most || newOuter.Left > Most || newOuter.Top > Most || newOuter.Right > Most || newOuter.Bottom > Most)
            return Reject("gaps", $"A gap of more than {Most} pixels leaves nothing to tile.");

        if (newInner == Options.InnerGap && newOuter == current)
            return Reject("gaps", "The gaps are already there.");

        Options = Options with { InnerGap = newInner, OuterGap = newOuter };

        Emit(new GapsChanged(newInner, newOuter));

        return Complete();
    }

    /// <summary>
    /// Changes how many windows a master-stack layout keeps in its master area, for the
    /// focused window's container.
    /// </summary>
    /// <param name="delta">The change, or the count itself when <paramref name="absolute"/>.</param>
    /// <param name="absolute">Whether <paramref name="delta"/> is the count rather than a change.</param>
    /// <remarks>
    /// The master-stack layouts have carried a master count since they were written,
    /// with no way to set it from a key. One master is a main window and a stack; two
    /// is a pair side by side with the rest below, which is the shape for a call and its
    /// notes. Refused for a container in any other layout, since the count means
    /// nothing there and silently switching layouts would be a surprise.
    /// </remarks>
    public WmResult SetMasterCount(int delta, bool absolute = false)
    {
        if (FocusedWindow?.ParentContainer is not { } container)
            return Reject("layout", "No focused window.");

        // The nearest container in a master layout, since the focused window may sit
        // inside a split nested in one.
        ContainerNode? target = container;

        while (target is not null && target.Layout is not MasterStackLayout)
            target = target.ParentContainer;

        if (target?.Layout is not MasterStackLayout master)
            return Reject("layout", "The focused window is not in a master-stack layout; --masters applies to master-left, master-right, master-top and master-bottom.");

        int count = Math.Clamp(absolute ? delta : master.MasterCount + delta, 1, 8);

        if (count == master.MasterCount)
            return Reject("layout", $"The master count is already {count}.");

        target.Layout = master.WithMasterCount(count);

        Emit(new LayoutChanged(target, target.Layout.Name));

        return Complete();
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

            WmResult restored = RestoreFromAway(remembered);

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
            return RestoreFromAway(window);
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
        if (IsPaused == paused) return Complete();

        IsPaused = paused;
        Emit(new PauseChanged(paused));

        return Complete();
    }

    // ---- output ------------------------------------------------------------

    /// <summary>Recomputes every window's target rectangle.</summary>
    public IReadOnlyList<Placement> ComputePlacements() =>
        _engine.Arrange(Root, Options.ToArrangeOptions() with { Focused = FocusedWindow });

    // ---- internals ---------------------------------------------------------

    private void SetFocus(WindowNode? window)
    {
        if (ReferenceEquals(FocusedWindow, window))
        {
            // The same window, but perhaps not the same place: a focused window moved
            // to another monitor and followed there is still the focused window, and
            // the focused monitor has to move with it or the next monitor-relative
            // command works from the screen it left. No event; nothing changed that
            // anybody listening would call a focus change.
            if (window?.Workspace is { } here)
            {
                here.LastFocused = window;
                if (here.Monitor is { } monitor) FocusedMonitor = monitor;
            }

            return;
        }

        WindowNode? previous = FocusedWindow;
        FocusedWindow = window;

        // Stamped here rather than at each call site, because this is the one place
        // focus actually changes. An increment and a field write; the counter is
        // never read on the hot path, only when something asks for recency.
        if (window is not null) window.FocusSequence = ++_focusClock;

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
