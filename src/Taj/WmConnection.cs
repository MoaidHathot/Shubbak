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
    private readonly CancellationTokenSource _shutdown = new();
    private readonly int _monitorIndex;

    private IpcClient? _client;
    private Task? _pump;

    /// <summary>Raised when the active workspace changes, so profiles can switch.</summary>
    public event Action<string>? ActiveWorkspaceChanged;

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
    /// <param name="monitorIndex">
    /// Which monitor this bar is on. Used to show only that monitor's workspaces,
    /// which is what makes a per-monitor bar useful rather than several identical
    /// copies of one list.
    /// </param>
    public WmConnection(BarModel model, int monitorIndex = -1)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _monitorIndex = monitorIndex;
    }

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
    public async Task SendCommandAsync(string command)
    {
        if (_client is null) return;

        try
        {
            IpcResponse response = await _client.SendAsync("command", command).ConfigureAwait(false);

            if (!response.Ok)
                Log.Warn(LogCategory.Ipc, $"command '{command}' rejected: {response.Error}");
        }
        catch (IOException ex)
        {
            Log.Warn(LogCategory.Ipc, $"could not send '{command}': {ex.Message}");
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

                await RefreshAsync(client).ConfigureAwait(false);

                // A separate client for the subscription, because the command
                // channel must stay free to respond while events are streaming.
                await using var events = new IpcClient();
                await events.ConnectAsync(TimeSpan.FromSeconds(2), _shutdown.Token).ConfigureAwait(false);

                await foreach (IpcEvent notification in
                    events.SubscribeAsync(null, _shutdown.Token).ConfigureAwait(false))
                {
                    await HandleEventAsync(client, notification).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException)
            {
                Log.Warn(LogCategory.Ipc, $"disconnected: {ex.Message}");
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
                UpdateFocusedWindow(notification.Data);
                break;

            case "window.state_changed":
                // The payload is the window that changed, which is not necessarily
                // the focused one - but a state change on an unfocused window cannot
                // alter what the bar shows, and UpdateFocusedWindow only writes the
                // values the title widget reads. Cheaper than a full refresh, and
                // this fires on every fullscreen toggle.
                UpdateFocusedWindow(notification.Data);
                break;

            case "window.focused":
                UpdateFocusedWindow(notification.Data);

                // The workspace list is refreshed too, because which workspace holds
                // focus can change without any workspace being activated. Moving
                // between monitors is the ordinary case: both monitors' workspaces
                // were already displayed, so nothing is activated and the only thing
                // that changed is which one has the keyboard.
                await RefreshAsync(client).ConfigureAwait(false);
                break;

            case "workspace.activated":
            case "workspace.created":
            case "workspace.destroyed":
            case "window.managed":
            case "window.unmanaged":
            case "window.moved":
            case "window.tags_changed":
                // Workspace occupancy is derived from several event kinds, so the
                // list is re-queried rather than patched. It is a handful of entries;
                // reconstructing it is cheaper than keeping a correct incremental
                // model in step.
                await RefreshAsync(client).ConfigureAwait(false);
                break;

            case "binding_mode.changed":
                _model.SetValue("binding_mode", Unquote(notification.Data));
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
                // Best-effort on the sending side - the server does not flush its
                // outboxes on the way out - so missing this is not a failure. The
                // timeout on the reconnect loop catches it a few seconds later.
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
                break;
        }
    }

    private void UpdateFocusedWindow(string json)
    {
        if (FocusedWindow.Parse(json) is not { } values) return;

        _model.SetValue(FocusedWindow.TitleKey, values.Title);
        _model.SetValue(FocusedWindow.ProcessKey, values.Process);
        _model.SetValue(FocusedWindow.StateKey, values.State);
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

            List<WorkspaceInfo> visible = [];
            string active = string.Empty;

            foreach (WorkspaceInfo workspace in state.Workspaces)
            {
                // The scratchpad is a workspace internally so the tree works on it
                // unchanged, but it is not something the user switches to.
                if (workspace.Name.StartsWith("__", StringComparison.Ordinal)) continue;

                // The active workspace of this monitor is what selects the bar
                // profile, so it is noted before any filtering.
                if (workspace.Active && workspace.MonitorIndex == _monitorIndex && active.Length == 0)
                    active = workspace.Name;

                if (OwnMonitorOnly && _monitorIndex >= 0 && workspace.MonitorIndex != _monitorIndex)
                    continue;

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

            if (active.Length > 0) ActiveWorkspaceChanged?.Invoke(active);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Log.Warn(LogCategory.Ipc, $"could not refresh state: {ex.Message}");
        }
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
            if (_monitorIndex >= 0 && workspace.MonitorIndex != _monitorIndex) continue;

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
