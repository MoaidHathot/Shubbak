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

    private IpcClient? _client;
    private Task? _pump;

    /// <summary>Raised when the active workspace changes, so profiles can switch.</summary>
    public event Action<string>? ActiveWorkspaceChanged;

    public WmConnection(BarModel model) =>
        _model = model ?? throw new ArgumentNullException(nameof(model));

    /// <summary>True while connected to a window manager.</summary>
    public bool IsConnected { get; private set; }

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
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                if (!IpcClient.IsServerRunning())
                {
                    await Task.Delay(1000, _shutdown.Token).ConfigureAwait(false);
                    continue;
                }

                await using var client = new IpcClient();
                await client.ConnectAsync(TimeSpan.FromSeconds(2), _shutdown.Token).ConfigureAwait(false);

                _client = client;
                IsConnected = true;

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
            case "window.focused":
            case "window.title_changed":
                UpdateFocusedWindow(notification.Data);
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

            default:
                break;
        }
    }

    private void UpdateFocusedWindow(string json)
    {
        if (string.IsNullOrEmpty(json) || json == "null")
        {
            _model.SetValue("window.title", string.Empty);
            _model.SetValue("window.process", string.Empty);
            return;
        }

        try
        {
            WindowInfo? window = JsonSerializer.Deserialize(json, IpcJsonContext.Default.WindowInfo);
            if (window is null) return;

            _model.SetValue("window.title", window.Title);
            _model.SetValue("window.process", window.ProcessName);
        }
        catch (JsonException ex)
        {
            Log.Warn(LogCategory.Ipc, $"malformed window payload: {ex.Message}");
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

            List<WorkspacesWidget.WorkspaceEntry> entries = [];
            string active = string.Empty;

            foreach (WorkspaceInfo workspace in state.Workspaces)
            {
                // The scratchpad is a workspace internally so the tree works on it
                // unchanged, but it is not something the user switches to.
                if (workspace.Name.StartsWith("__", StringComparison.Ordinal)) continue;

                entries.Add(new WorkspacesWidget.WorkspaceEntry(
                    workspace.Name, workspace.DisplayName, workspace.Active, workspace.HasWindows));

                if (workspace.Active && active.Length == 0) active = workspace.Name;
            }

            _model.SetValue("workspaces", WorkspacesWidget.Encode(entries));
            _model.SetValue("window.title", state.FocusedWindow?.Title ?? string.Empty);
            _model.SetValue("window.process", state.FocusedWindow?.ProcessName ?? string.Empty);
            _model.SetValue("binding_mode", state.BindingMode ?? string.Empty);
            _model.SetValue("layout", FindActiveLayout(state));

            if (active.Length > 0) ActiveWorkspaceChanged?.Invoke(active);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Log.Warn(LogCategory.Ipc, $"could not refresh state: {ex.Message}");
        }
    }

    private static string FindActiveLayout(StateSnapshot state)
    {
        foreach (WorkspaceInfo workspace in state.Workspaces)
            if (workspace.Active) return workspace.Layout;

        return string.Empty;
    }

    private static string Unquote(string json) =>
        json.Length >= 2 && json[0] == '"' && json[^1] == '"' ? json[1..^1] : json;

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
