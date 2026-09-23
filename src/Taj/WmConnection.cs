using System.Diagnostics;
using System.Text.Json;
using Shubbak.Core.Diagnostics;
using Shubbak.Ipc;
using Taj.Core;
using Taj.Core.Sources;
using Taj.Core.Widgets;

namespace Taj;

/// <summary>
/// Connects the bar to the window manager's event stream.
/// </summary>
/// <remarks>
/// <para>
/// The bar never inspects windows itself. Every value it displays comes from the
/// window manager's event stream, which is what structurally prevents the bar
/// disagreeing with the window manager.
/// </para>
/// <para>
/// This is the fix for Zebar's stale window titles. S4 measured
/// <c>EVENT_OBJECT_NAMECHANGE</c> firing on browser tab switches - twice as often as
/// focus changes - so a bar that listens only for focus misses roughly two thirds of
/// title updates. Shubbak's hook already sees those events; forwarding them costs
/// nothing.
/// </para>
/// </remarks>
public sealed class WmConnection : IAsyncDisposable
{
    private readonly BarModel _model;

    /// <summary>Whether the window manager has stopped arranging windows.</summary>
    private bool _paused;

    /// <summary>Whether it has let go of the keyboard.</summary>
    /// <remarks>
    /// Kept apart from <see cref="_paused"/> rather than collapsed into one flag,
    /// because the two arrive on different topics and either can change without the
    /// other. Collapsing them would make the second event overwrite what the first
    /// said.
    /// </remarks>
    private bool _suspended;
    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>
    /// The GDI device name of the display this bar is on, <c>\\.\DISPLAY2</c>.
    /// </summary>
    /// <remarks>
    /// The join key to the window manager's state: <c>WorkspaceInfo.Monitor</c> carries
    /// the same string. It used to be a position in the monitor list, and positions
    /// shift - unplug the first monitor and every bar after it starts filtering on the
    /// wrong display, with nothing to say so. The name is stable for as long as the
    /// display is attached, which is exactly as long as the bar exists.
    /// </remarks>
    private readonly string _deviceId;

    /// <summary>
    /// The displays as the window manager last described them, so a change can be
    /// noticed and announced.
    /// </summary>
    private IReadOnlyList<MonitorInfoDto>? _lastMonitors;

    private IpcClient? _client;
    private Task? _pump;

    /// <summary>
    /// The window in front as last reported, so an icon that arrives after focus has
    /// moved on is kept for later rather than shown now.
    /// </summary>
    private long _focusedHandle;

    /// <summary>
    /// Icons by window, as the window manager sent them, with when they were read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The window manager remembers too, so this is not about sparing it: it is about
    /// not making a round trip per focus change for a picture that was on screen a
    /// second ago. A minute, then asked again, because icons do change - a badge, a
    /// profile - and a handle is reused once its window has gone. Emptied wholesale
    /// when full, like every other cache in this program; at these sizes an eviction
    /// policy would be more code than the cache.
    /// </para>
    /// <para>
    /// Written from the pump and from the fire-and-forget fetches, hence concurrent.
    /// </para>
    /// </remarks>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, (long ReadAt, string? Json)> _icons = new();

    /// <summary>
    /// Windows whose icon is being fetched right now, so a burst of title changes in
    /// the moment after an entry expires costs one round trip rather than one each.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, byte> _fetching = new();

    private static readonly TimeSpan IconLifetime = TimeSpan.FromSeconds(60);
    private const int IconCapacity = 64;

    /// <summary>
    /// The size asked for. A hint to the window manager about which variant to prefer;
    /// the widget scales whatever comes.
    /// </summary>
    private const int IconSize = 32;

    /// <summary>
    /// Every topic <see cref="HandleEventAsync"/> has a case for, and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bar used to subscribe to <c>*</c>. That is the shortest thing to write and
    /// it made the window manager build and send a payload for every event on the
    /// desktop to a client that dropped most of them on the floor: <c>command.rejected</c>
    /// alone fires on every repeat of a held key that cannot be satisfied, and the
    /// daemon's <c>HasSubscribers</c> gate - which exists so that a payload is only
    /// serialised when somebody wants it - was answering "yes" to everything for as
    /// long as a bar was running. Anything added to the stream later for other
    /// consumers would have been paid for by the bar as well.
    /// </para>
    /// <para>
    /// A case added to the switch must be added here, or the event never arrives and
    /// nothing says so. The list sits beside the switch for that reason.
    /// </para>
    /// </remarks>
    private static readonly string Subscribed = string.Join(',',
    [
        "window.title_changed",
        "window.state_changed",
        "window.focused",
        "window.managed",
        "window.unmanaged",
        "window.moved",
        "window.tags_changed",
        "workspace.activated",
        "workspace.created",
        "workspace.destroyed",
        "workspace.moved",
        "monitor.added",
        "monitor.removed",
        "monitor.changed",
        "layout.changed",
        "binding_mode.changed",
        "wm.paused",
        "wm.suspended",
        "context.changed",
        "config.reloaded",
        IpcProtocol.ShutdownTopic,
        IpcProtocol.ResyncTopic,
    ]);

    /// <summary>
    /// Raised when the contexts the window manager holds differ from the last time
    /// this connection looked, so profiles can switch.
    /// </summary>
    /// <remarks>
    /// The whole list each time, in the window manager's order, rather than the one
    /// name that changed: a rule asks whether a context is among those held, and the
    /// list is what answers that. Raised before <see cref="ActiveWorkspaceChanged"/> on
    /// the same refresh, so the profile picked for the workspace is picked with the
    /// contexts already known.
    /// </remarks>
    public event Action<IReadOnlyList<string>>? ContextsChanged;

    /// <summary>The contexts as the window manager last listed them.</summary>
    private IReadOnlyList<string>? _lastContexts;

    /// <summary>
    /// Raised when the active workspace changes, so profiles can switch.
    /// </summary>
    /// <remarks>
    /// Carries what a bar rule can match on besides the workspace: the display's
    /// position in the window manager's monitor list, for <c>monitor=1</c>, and the
    /// names the window manager's configuration gives it, for <c>monitor="dell-left"</c>.
    /// Both are read off the snapshot each time rather than remembered, since a monitor
    /// coming or going changes the first and a reload can change the second.
    /// </remarks>
    public event Action<string, int, IReadOnlyList<string>>? ActiveWorkspaceChanged;

    /// <summary>
    /// Raised when the set of displays the window manager knows about, or where any of
    /// them is, differs from the last time this connection looked.
    /// </summary>
    /// <remarks>
    /// Raised, not acted on, like everything else here: creating and destroying bar
    /// windows is the message loop's job. Every bar's connection sees the same snapshot
    /// and so every one of them raises this for the same change; the loop's response is
    /// idempotent, so that costs a few comparisons and nothing else.
    /// </remarks>
    public event Action<IReadOnlyList<MonitorInfoDto>>? MonitorsChanged;

    /// <summary>
    /// Raised when the window manager reports that it has re-read the configuration.
    /// </summary>
    /// <remarks>
    /// The bar reads the same file, so a reload that only reached the window manager
    /// left the two disagreeing - with the bar showing whatever it was launched with
    /// and nothing to indicate it.
    /// </remarks>
    public event Action? ConfigReloaded;

    /// <param name="model">The bar model to feed.</param>
    /// <param name="deviceId">
    /// The GDI device name of the display this bar is on. Used to show only that
    /// display's workspaces, which is what makes a per-monitor bar useful rather than
    /// several identical copies of one list.
    /// </param>
    public WmConnection(BarModel model, string deviceId)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        _deviceId = deviceId;
    }

    /// <summary>The display this connection filters for.</summary>
    public string DeviceId => _deviceId;

    /// <summary>
    /// Whether to show only this monitor's workspaces.
    /// </summary>
    public bool OwnMonitorOnly { get; set; } = true;

    /// <summary>True while connected to a window manager.</summary>
    public bool IsConnected { get; private set; }

    private bool _everConnected;

    /// <summary>
    /// How long to keep waiting for a window manager that has gone, or null to wait
    /// for ever.
    /// </summary>
    public TimeSpan? WindowManagerTimeout { get; set; } = TajConfig.DefaultWindowManagerTimeout;

    /// <summary>
    /// Raised when the bar should close: the window manager said it was going, or it
    /// has been gone longer than <see cref="WindowManagerTimeout"/>.
    /// </summary>
    /// <remarks>
    /// Raised, not acted on. Closing the bar touches windows belonging to the thread
    /// running the message loop, and this is not that thread - the same reason
    /// <see cref="ConfigReloaded"/> is a signal rather than a call.
    /// </remarks>
    public event Action? WindowManagerStopped;

    /// <summary>
    /// Connects and begins consuming events, retrying until the window manager
    /// appears.
    /// </summary>
    /// <remarks>
    /// Retrying rather than failing matters because the bar is usually launched by
    /// the window manager's own startup command, and can therefore win the race.
    /// </remarks>
    public void Start() => _pump = Task.Run(PumpAsync);

    /// <summary>Sends a command, for widget clicks.</summary>
    /// <remarks>
    /// Runs on the message-loop thread while the pump runs on its own, so both may be
    /// using the connection at once. The client serialises them; this method only has
    /// to avoid reading the field twice, because the pump nulls it on every reconnect
    /// and the gap between the guard and the use was long enough to lose the race.
    /// </remarks>
    public async Task SendCommandAsync(string command)
    {
        // Read once. It was read twice - a null check and then a use - so a reconnect
        // landing between them turned a click into a NullReferenceException on a
        // fire-and-forget task, which is to say into nothing at all.
        if (_client is not { } client)
        {
            // Said out loud. A click that does nothing because the bar is not connected
            // is the same to the user as a click that does nothing because the bar is
            // broken, and only one of them is worth reporting.
            Log.Warn(LogCategory.Ipc, $"not connected; dropped '{command}'");
            return;
        }

        try
        {
            IpcResponse response = await client.SendAsync("command", command).ConfigureAwait(false);

            if (!response.Ok)
                Log.Warn(LogCategory.Ipc, $"command '{command}' rejected: {response.Error}");
        }
        catch (Exception ex)
        {
            // As broad as the pump's, and for the same reason: this is a
            // fire-and-forget task, so anything not caught here is lost entirely.
            Log.Warn(LogCategory.Ipc, $"could not send '{command}': {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task PumpAsync()
    {
        // Zero until the first successful connection, and reset by every one after,
        // so the clock only ever runs against a window manager that was really there.
        long lostAtTicks = 0;

        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                if (!IpcClient.IsServerRunning())
                {
                    if (ReconnectPolicy.ShouldGiveUp(
                            _everConnected, lostAtTicks, Stopwatch.GetTimestamp(), WindowManagerTimeout))
                    {
                        Log.Info(LogCategory.Ipc,
                            $"no window manager for {WindowManagerTimeout!.Value.TotalSeconds:F0}s; closing the bar");

                        WindowManagerStopped?.Invoke();
                        return;
                    }

                    await Task.Delay(1000, _shutdown.Token).ConfigureAwait(false);
                    continue;
                }

                await using var client = new IpcClient();
                await client.ConnectAsync(TimeSpan.FromSeconds(2), _shutdown.Token).ConfigureAwait(false);

                _client = client;
                IsConnected = true;

                // Reset on every connection, not only the first: a window manager that
                // comes back inside the window is not a window manager that has gone.
                _everConnected = true;
                lostAtTicks = 0;

                Log.Info(LogCategory.Ipc, "connected to the window manager");

                // A separate client for the subscription, because the command
                // channel must stay free to respond while events are streaming.
                //
                // Subscribed before the snapshot is read, not after. In the other order
                // there was a gap between the two in which a focus change or a
                // workspace switch was in neither, and the bar showed the wrong
                // workspace until something unrelated happened. Every event that lands
                // between the handshake and the snapshot is queued for this connection
                // and applied on top of the snapshot, which is idempotent for all of
                // them.
                await using var events = new IpcClient();
                await events.ConnectAsync(TimeSpan.FromSeconds(2), _shutdown.Token).ConfigureAwait(false);
                await events.BeginSubscriptionAsync(Subscribed, _shutdown.Token).ConfigureAwait(false);

                await RefreshAsync(client).ConfigureAwait(false);

                await foreach (IpcEvent notification in
                    events.ReadEventsAsync(_shutdown.Token).ConfigureAwait(false))
                {
                    await HandleEventAsync(client, notification).ConfigureAwait(false);
                }

                // The stream ending without an exception is the pipe closing under the
                // subscription: the window manager went without saying so, or said so
                // and the notice was lost - it does not flush its outboxes on the way
                // out. Said here because nothing else says it. A read that meets the end
                // of the pipe reports the end of the stream, not an error, so this loop
                // ended, went round and reconnected without a line in the log to explain
                // the gap. The clean case announces itself from the shutdown event.
                if (!_shutdown.IsCancellationRequested)
                    Log.Info(LogCategory.Ipc, "the window manager closed the connection");
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // Everything, not the two that were expected.
                //
                // This loop is the bar's only source of workspaces, layout and title,
                // and it was started with a bare Task.Run - nothing awaits it, so a
                // fault nobody caught was a fault nobody saw. An unexpected exception
                // left the task dead, _client null for good, and the bar drawing a
                // workspace list frozen at whatever it last read, while the clock and
                // the keyboard language carried on because they are local timers that
                // never touch this pipe. Every later click was a silent no-op, and
                // nothing was written to the log to say why.
                //
                // Measured against the alternative: a bar that reconnects a second
                // later having logged what happened is strictly better than one that
                // looks alive and is not.
                Log.Warn(LogCategory.Ipc, $"disconnected: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                _client = null;
                IsConnected = false;

                // Stamped where the connection ended rather than where it was noticed,
                // so the wait is measured from the loss itself.
                if (lostAtTicks == 0) lostAtTicks = Stopwatch.GetTimestamp();
            }

            try
            {
                await Task.Delay(1000, _shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task HandleEventAsync(IpcClient client, IpcEvent notification)
    {
        switch (notification.Topic)
        {
            case "window.title_changed":
                UpdateFocusedWindow(client, notification.Data);
                break;

            case "window.state_changed":
                // The payload is the window that changed, which is not necessarily
                // the focused one - but a state change on an unfocused window cannot
                // alter what the bar shows, and UpdateFocusedWindow only writes the
                // values the title widget reads. Cheaper than a full refresh, and
                // this fires on every fullscreen toggle.
                UpdateFocusedWindow(client, notification.Data);
                break;

            case "window.focused":
                UpdateFocusedWindow(client, notification.Data);

                // The workspace list is refreshed too, because which workspace holds
                // focus can change without any workspace being activated. Moving
                // between monitors is the ordinary case: both monitors' workspaces
                // were already displayed, so nothing is activated and the only thing
                // that changed is which one has the keyboard.
                await RefreshAsync(client).ConfigureAwait(false);
                break;

            case "window.unmanaged":
                // Its icon is forgotten along with it, so a handle Windows hands to the
                // next window does not come with the last one's picture.
                ForgetIcon(notification.Data);
                await RefreshAsync(client).ConfigureAwait(false);
                break;

            case "workspace.activated":
            case "workspace.created":
            case "workspace.destroyed":
            case "workspace.moved":
            case "window.managed":
            case "window.moved":
            case "window.tags_changed":
                // Workspace occupancy is derived from several event kinds, so the
                // list is re-queried rather than patched. It is a handful of entries;
                // reconstructing it is cheaper than keeping a correct incremental
                // model in step.
                await RefreshAsync(client).ConfigureAwait(false);
                break;

            case "monitor.added":
            case "monitor.removed":
            case "monitor.changed":
                // Which monitor a workspace is on is part of what the snapshot says,
                // and this bar shows only its own monitor's workspaces - so a monitor
                // coming or going changes the answer for every bar, not just the one
                // on the monitor concerned. Used to be dropped by the default case
                // below while the subscription was to everything.
                await RefreshAsync(client).ConfigureAwait(false);
                break;

            case "binding_mode.changed":
                _model.SetValue("binding_mode", Unquote(notification.Data));
                break;

            // Both change what Shubbak is doing without changing anything on screen,
            // which is exactly the kind of state a bar exists to make visible. A
            // suspended window manager in particular looks identical to a crashed one
            // until you press a key and nothing happens.
            case "wm.paused":
                _paused = notification.Data.Contains("\"paused\":true", StringComparison.Ordinal);
                PublishStatus();
                break;

            case "wm.suspended":
                _suspended = notification.Data.Contains("\"suspended\":true", StringComparison.Ordinal);
                PublishStatus();
                break;

            case "context.changed":
                // Re-read rather than patched from the payload, which names only the
                // context that flipped. The snapshot lists every context that holds in
                // the window manager's own order, and a list assembled here from
                // arrivals would drift from it in order and, after a missed event, in
                // content. Contexts flip seconds or minutes apart; one query each time
                // costs nothing worth saving.
                await RefreshAsync(client).ConfigureAwait(false);
                break;

            case "layout.changed":
                await RefreshAsync(client).ConfigureAwait(false);
                break;

            case "config.reloaded":
                // Raised, not acted on. Rebuilding the bar touches windows and GDI
                // objects belonging to the thread running the message loop, and this
                // is not that thread.
                ConfigReloaded?.Invoke();
                break;

            case IpcProtocol.ShutdownTopic:
                // The window manager is going. A bar launched by it should go too,
                // rather than sitting there attached to nothing.
                //
                // Best-effort on the sending side - the server gives its outboxes a
                // bounded moment on the way out, not a guarantee - so missing this is
                // not a failure. The timeout on the reconnect loop catches it a few
                // seconds later.
                Log.Info(LogCategory.Ipc, "the window manager is shutting down; closing the bar");
                WindowManagerStopped?.Invoke();
                break;

            case "wm.resync":
                // The window manager dropped a backlog it could not deliver, so what
                // the bar is showing is older than the world. Re-reading is the whole
                // point of being told.
                Log.Warn(LogCategory.Ipc, "missed events; re-reading the window manager's state");
                await RefreshAsync(client).ConfigureAwait(false);
                break;

            default:
                // Nothing should arrive here: the subscription names exactly the topics
                // above. If something does, the two lists have drifted, and a debug
                // line is the cheapest way to find out which way.
                Log.Debug(LogCategory.Ipc, $"unhandled event on a subscribed topic: {notification.Topic}");
                break;
        }
    }

    /// <summary>
    /// Publishes the window manager's own state, as one combined value and as two
    /// separate ones.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>status</c> is for a bar with room for one pill: suspended wins when both
    /// hold, because a window manager that is not arranging windows is inconvenient
    /// and one that has let go of the keyboard is why none of your keys work.
    /// </para>
    /// <para>
    /// <c>suspended</c> and <c>paused</c> are separate so a config can show two pills
    /// and give each the click that undoes it. That is the point of them: a pill
    /// saying "suspended" which you can click to resume is a way back that does not
    /// need the keyboard, which is the one thing suspending took away.
    /// </para>
    /// <para>
    /// All three are empty when there is nothing to say, and a template widget hides
    /// itself when its result is empty.
    /// </para>
    /// </remarks>
    private void PublishStatus()
    {
        _model.SetValue("suspended", WindowManagerStatus.SuspendedLabel(_suspended));
        _model.SetValue("paused", WindowManagerStatus.PausedLabel(_paused));
        _model.SetValue("status", WindowManagerStatus.Combined(_suspended, _paused));

        // Announced as well as displayed. A suspended window manager means nobody is
        // arranging windows, which in practice means a game - so the loop stops
        // polling sources nothing is going to read. Raised on every publish rather
        // than on a change, because the loop treats it as a level and not an edge.
        SuspendedChanged?.Invoke(_suspended);
    }

    /// <summary>
    /// Raised with the window manager's suspension state whenever it is republished.
    /// </summary>
    /// <remarks>
    /// A level, not an edge: the current state each time, so a listener that missed
    /// one is corrected by the next rather than left inverted.
    /// </remarks>
    public event Action<bool>? SuspendedChanged;

    private void UpdateFocusedWindow(IpcClient client, string json)
    {
        if (FocusedWindow.Parse(json) is not { } values) return;

        _model.SetValue(FocusedWindow.TitleKey, values.Title);
        _model.SetValue(FocusedWindow.ProcessKey, values.Process);
        _model.SetValue(FocusedWindow.StateKey, values.State);

        TrackFocus(client, values.Handle);
    }

    /// <summary>
    /// Notes which window is in front and sees to its icon: from memory when it was
    /// seen recently, otherwise asked for and applied when the answer comes.
    /// </summary>
    /// <remarks>
    /// The previous icon stays up until the new one arrives rather than being cleared
    /// at once. Clearing hides the widget, which moves the title beside it, and a
    /// title that jumps left and back on every focus change is worse than a picture
    /// that lags the title by the milliseconds a round trip takes.
    /// </remarks>
    private void TrackFocus(IpcClient client, long handle)
    {
        Volatile.Write(ref _focusedHandle, handle);

        if (handle == 0)
        {
            _model.SetValue(FocusedWindow.IconKey, string.Empty);
            return;
        }

        if (_icons.TryGetValue(handle, out (long ReadAt, string? Json) known) &&
            Stopwatch.GetElapsedTime(known.ReadAt) < IconLifetime)
        {
            _model.SetValue(FocusedWindow.IconKey, known.Json ?? string.Empty);
            return;
        }

        // One fetch per window at a time. A terminal retitles itself on every command,
        // and each retitle lands here; without this, the first retitle after an entry
        // expired would send a request, and every one behind it in the same instant
        // would send another - each a message into the window the user is typing in.
        if (!_fetching.TryAdd(handle, 0)) return;

        _ = FetchIconAsync(client, handle);
    }

    /// <summary>
    /// Asks the window manager for a window's icon, remembers the answer, and shows it
    /// if that window is still the one in front.
    /// </summary>
    /// <remarks>
    /// Fire-and-forget from the pump rather than awaited on it, so a window slow to
    /// answer the window manager holds up its own icon and nothing else - the events
    /// behind it keep flowing. Which is why the guard at the end exists: two fetches
    /// can be in flight and finish out of order.
    /// </remarks>
    private async Task FetchIconAsync(IpcClient client, long handle)
    {
        string? json = null;

        try
        {
            try
            {
                IpcResponse response = await client.SendAsync(
                    WindowIcon.Method, $"{handle} {IconSize}").ConfigureAwait(false);

                if (response.Ok) json = response.Data;
                else if (Log.IsEnabled(LogLevel.Debug)) Log.Debug(LogCategory.Ipc, $"no icon for window {handle}: {response.Error}");
            }
            catch (Exception ex)
            {
                // A lost connection is the pump's to notice and mend; here it is one
                // picture missing, and a debug line is all that is owed.
                Log.Debug(LogCategory.Ipc, $"could not fetch the icon of window {handle}: {ex.GetType().Name}: {ex.Message}");
            }

            if (_icons.Count >= IconCapacity) _icons.Clear();
            _icons[handle] = (Stopwatch.GetTimestamp(), json);
        }
        finally
        {
            // After the entry is stored, so nobody arriving in between finds neither
            // an answer nor a fetch in flight and starts another.
            _fetching.TryRemove(handle, out _);
        }

        if (Volatile.Read(ref _focusedHandle) == handle)
            _model.SetValue(FocusedWindow.IconKey, json ?? string.Empty);
    }

    /// <summary>Forgets the icon of a window the window manager has let go of.</summary>
    private void ForgetIcon(string json)
    {
        try
        {
            if (JsonSerializer.Deserialize(json, IpcJsonContext.Default.WindowInfo) is { } window)
                _icons.TryRemove(window.Handle, out _);
        }
        catch (JsonException)
        {
            // Nothing to forget from a payload that says nothing.
        }
    }

    private async Task RefreshAsync(IpcClient client)
    {
        try
        {
            IpcResponse response = await client.SendAsync("query", "state").ConfigureAwait(false);
            if (!response.Ok || response.Data is null) return;

            StateSnapshot? state = JsonSerializer.Deserialize(
                response.Data, IpcJsonContext.Default.StateSnapshot);

            if (state is null) return;

            // The position this display holds in the window manager's list, for bar
            // rules written as monitor=N, and the names its configuration gives it, for
            // rules written as monitor="name". Read off the snapshot every time rather
            // than remembered, because a monitor coming or going moves the one and a
            // reload can change the other.
            int monitorIndex = IndexOfThisMonitor(state);
            IReadOnlyList<string> monitorNames = monitorIndex >= 0
                ? state.Monitors[monitorIndex].Names ?? []
                : [];

            List<WorkspaceInfo> visible = [];
            string active = string.Empty;

            foreach (WorkspaceInfo workspace in state.Workspaces)
            {
                // The scratchpad is a workspace internally so the tree works on it
                // unchanged, but it is not something the user switches to.
                if (workspace.Name.StartsWith("__", StringComparison.Ordinal)) continue;

                bool onThisMonitor = string.Equals(workspace.Monitor, _deviceId, StringComparison.OrdinalIgnoreCase);

                // The active workspace of this monitor is what selects the bar
                // profile, so it is noted before any filtering.
                if (workspace.Active && onThisMonitor && active.Length == 0)
                    active = workspace.Name;

                if (OwnMonitorOnly && !onThisMonitor) continue;

                visible.Add(workspace);
            }

            // Declared order, not creation order and not whichever monitor a
            // workspace currently sits on. alt+1 is first because the user wrote it
            // first, and that has to hold however the workspaces move around.
            visible.Sort(static (a, b) => a.SortIndex != b.SortIndex
                ? a.SortIndex.CompareTo(b.SortIndex)
                : string.CompareOrdinal(a.Name, b.Name));

            List<WorkspacesWidget.WorkspaceEntry> entries =
            [
                .. visible.Select(w => new WorkspacesWidget.WorkspaceEntry(
                    w.Name, w.DisplayName, w.Active, w.HasWindows, w.Focused)),
            ];

            _model.SetValue("workspaces", WorkspacesWidget.Encode(entries));
            _model.SetValue(FocusedWindow.TitleKey, state.FocusedWindow?.Title ?? string.Empty);
            _model.SetValue(
                FocusedWindow.ProcessKey, state.FocusedWindow?.ProcessName ?? string.Empty);
            _model.SetValue(FocusedWindow.StateKey, state.FocusedWindow?.State ?? string.Empty);
            _model.SetValue("binding_mode", state.BindingMode ?? string.Empty);
            _model.SetValue("layout", FindActiveLayout(state));

            // The icon too, from the snapshot as well as from the events, or a bar
            // started with a window already in front would show its title alone until
            // focus next moved.
            TrackFocus(client, state.FocusedWindow?.Handle ?? 0);

            // From the snapshot as well as from the events, because a bar that starts
            // while Shubbak is already suspended would otherwise show nothing until
            // the state next changed - which is precisely the moment somebody is
            // looking at the bar wondering why their keys do nothing.
            _paused = state.Paused;
            _suspended = state.Suspended;
            PublishStatus();

            // Before the workspace, so that a rule asking for both is judged with both
            // known: the workspace handler picks the profile, and it reads the contexts
            // the bar was last told about.
            IReadOnlyList<string> contexts = state.Contexts ?? [];
            _model.SetValue(ActiveContexts.Key, ActiveContexts.Label(contexts));

            if (!ActiveContexts.Same(_lastContexts, contexts))
            {
                // One value per context as well as the joined list, so a widget can
                // show an icon for one of them; the model drops unchanged writes.
                foreach ((string key, string value) in ActiveContexts.Changes(_lastContexts, contexts))
                    _model.SetValue(key, value);

                _lastContexts = contexts;
                ContextsChanged?.Invoke(contexts);
            }

            if (active.Length > 0) ActiveWorkspaceChanged?.Invoke(active, monitorIndex, monitorNames);

            if (MonitorsDiffer(_lastMonitors, state.Monitors))
            {
                _lastMonitors = state.Monitors;
                MonitorsChanged?.Invoke(state.Monitors);
            }
        }
        catch (JsonException ex)
        {
            // A payload that will not parse is this one message's problem, so the
            // connection carries on.
            //
            // Nothing else is caught here on purpose. A refresh that fails because the
            // connection failed has to reach the pump, which reconnects; absorbing it
            // would leave the bar streaming events it can no longer act on, refreshing
            // nothing, and looking exactly as alive as it did before.
            Log.Warn(LogCategory.Ipc, $"could not refresh state: {ex.Message}");
        }
    }

    /// <summary>Where this display sits in the window manager's list, or -1.</summary>
    private int IndexOfThisMonitor(StateSnapshot state)
    {
        for (int index = 0; index < state.Monitors.Count; index++)
        {
            if (string.Equals(state.Monitors[index].DeviceId, _deviceId, StringComparison.OrdinalIgnoreCase))
                return index;
        }

        return -1;
    }

    /// <summary>
    /// Whether two descriptions of the displays disagree about which are attached or
    /// where any of them is.
    /// </summary>
    /// <remarks>
    /// Identity and rectangle only. DPI, the friendly name and the active workspace
    /// change without anything about the bar windows needing to, and this decides
    /// whether the loop is asked to look at them.
    /// </remarks>
    private static bool MonitorsDiffer(IReadOnlyList<MonitorInfoDto>? before, IReadOnlyList<MonitorInfoDto> after)
    {
        if (before is null || before.Count != after.Count) return true;

        for (int i = 0; i < after.Count; i++)
        {
            MonitorInfoDto a = before[i];
            MonitorInfoDto b = after[i];

            if (!string.Equals(a.DeviceId, b.DeviceId, StringComparison.OrdinalIgnoreCase)) return true;
            if (a.X != b.X || a.Y != b.Y || a.Width != b.Width || a.Height != b.Height) return true;
        }

        return false;
    }

    /// <summary>The layout of the workspace displayed on this bar's monitor.</summary>
    /// <remarks>
    /// Filtered by monitor. Taking the first active workspace in the snapshot meant
    /// every bar on every monitor showed the first monitor's layout, so the indicator
    /// was wrong on all but one display and changed when the user was not looking.
    /// </remarks>
    private string FindActiveLayout(StateSnapshot state)
    {
        foreach (WorkspaceInfo workspace in state.Workspaces)
        {
            if (!workspace.Active) continue;
            if (!string.Equals(workspace.Monitor, _deviceId, StringComparison.OrdinalIgnoreCase)) continue;

            return workspace.Layout;
        }

        return string.Empty;
    }

    /// <summary>Reads a JSON string payload as plain text.</summary>
    /// <remarks>
    /// A JSON <c>null</c> becomes an empty string, not the four letters spelling it.
    /// Clearing the binding mode sends exactly that, so leaving the default set put
    /// the word "null" on the bar where the mode had been - and it stayed there,
    /// because an empty value is what hides the widget.
    /// </remarks>
    private static string Unquote(string json)
    {
        if (json is null) return string.Empty;

        string trimmed = json.Trim();

        if (trimmed.Length == 0 || string.Equals(trimmed, "null", StringComparison.Ordinal))
            return string.Empty;

        return trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"'
            ? trimmed[1..^1]
            : trimmed;
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);

        if (_pump is not null)
        {
            try { await _pump.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        _shutdown.Dispose();
    }
}
