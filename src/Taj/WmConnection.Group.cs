using System.Diagnostics;
using System.Text.Json;
using Shubbak.Companion;
using Shubbak.Core.Diagnostics;
using Shubbak.Ipc;
using Taj.Core;
using Taj.Core.Sources;

namespace Taj;

public sealed partial class WmConnection
{
    /// <summary>One event stream and one snapshot read shared by every display's model.</summary>
    private sealed class Group
    {
        internal Lock Gate { get; } = new();
        internal Dictionary<long, (long ReadAt, string? Json)> Icons { get; } = [];
        internal Dictionary<long, long> Fetching { get; } = [];
        internal long IconGeneration;
        private readonly Lock _lifecycle = new();
        private readonly List<WmConnection> _views = [];
        private EventPump? _pump;
        private bool _signals, _refresh;
        private string? _pipe;

        internal void Add(WmConnection view)
        {
            lock (_lifecycle)
            {
                lock (Gate)
                {
                    if (_views.Contains(view)) throw new InvalidOperationException("The bar is already connected.");
                    if (_pipe is not null && _pipe != view.PipeName) throw new InvalidOperationException("Shared bars must use the same pipe.");
                    _pipe = view.PipeName;
                    _views.Add(view);
                }
                // Joining is a rare startup/hotplug operation. Reconnect once so the
                // new view gets a fresh snapshot after subscription, with no separate
                // read racing the event pump's snapshots.
                Restart();
            }
        }

        internal void UpdateSubscription()
        {
            lock (_lifecycle)
            {
                bool signals;
                lock (Gate) signals = _views.Exists(v => v.ListensForSignals);
                if (signals != _signals) Restart();
            }
        }

        private void Restart()
        {
            _pump?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            lock (Gate)
            {
                _signals = _views.Exists(v => v.ListensForSignals);
                _refresh = false;
                foreach (WmConnection view in _views) { view._client = null; view.IsConnected = false; }
            }
            _pump = BuildPump();
            _pump.Start();
        }

        internal void RequestRefresh() => _refresh = true; // pump thread, under Gate

        internal void ApplyIcon(long handle, string? json)
        {
            foreach (WmConnection view in _views)
                if (view._focusedHandle == handle) view._model.SetValue(FocusedWindow.IconKey, json ?? string.Empty);
        }

        private EventPump BuildPump() => new(_signals ? SubscribedWithSignals : Subscribed)
        {
            ProgramName = "taj", PipeName = _pipe!, OpensCommandsConnection = true,
            Subscribed = async (connection, token) =>
            {
                lock (Gate)
                {
                    Icons.Clear();
                    Fetching.Clear();
                    foreach (WmConnection view in _views)
                    {
                        view._client = connection.Commands;
                        view.IsConnected = true;
                        view._everConnected = true;
                        view._model.SetValue(WindowManagerStatus.ConnectionKey, WindowManagerStatus.ConnectionLabel(true, true));
                    }
                }
                await RefreshAsync(connection.Commands!, token).ConfigureAwait(false);
                if (_signals) await connection.Commands!.SendAsync("command", SignalSource.AnnounceCommand, token).ConfigureAwait(false);
            },
            Event = (connection, notification, _) =>
            {
                lock (Gate)
                    foreach (WmConnection view in _views) view.HandleEvent(connection.Commands!, notification);
                return Task.CompletedTask;
            },
            BatchCompleted = async (connection, token) =>
            {
                if (!_refresh) return;
                _refresh = false;
                await RefreshAsync(connection.Commands!, token).ConfigureAwait(false);
            },
            Disconnected = _ =>
            {
                lock (Gate)
                {
                    foreach (WmConnection view in _views)
                    {
                        view._client = null;
                        view.IsConnected = false;
                        view._model.SetValue(WindowManagerStatus.ConnectionKey, WindowManagerStatus.ConnectionLabel(false, view._everConnected));
                    }
                }
            },
            GiveUp = (ever, lost) =>
            {
                lock (Gate)
                    return _views.Count > 0 && ReconnectPolicy.ShouldGiveUp(ever, lost, Stopwatch.GetTimestamp(), _views[0].WindowManagerTimeout);
            },
            Stopped = () =>
            {
                lock (Gate)
                    foreach (WmConnection view in _views) view.WindowManagerStopped?.Invoke();
            },
        };

        private async Task RefreshAsync(IpcClient client, CancellationToken token)
        {
            IpcResponse response = await client.SendAsync("query", "bar-state", token).ConfigureAwait(false);
            if (!response.Ok || response.Data is null) return;
            StateSnapshot? state;
            try { state = JsonSerializer.Deserialize(response.Data, IpcJsonContext.Default.StateSnapshot); }
            catch (JsonException ex)
            {
                Log.Warn(LogCategory.Ipc, $"could not refresh state: {ex.Message}");
                return;
            }
            if (state is null) return;
            lock (Gate)
                foreach (WmConnection view in _views) view.ApplySnapshot(client, state);
        }

        internal ValueTask RemoveAsync(WmConnection view)
        {
            lock (_lifecycle)
            {
                bool empty;
                lock (Gate)
                {
                    if (view._disposed) return ValueTask.CompletedTask;
                    view._disposed = true;
                    view._client = null;
                    view.IsConnected = false;
                    _views.Remove(view);
                    empty = _views.Count == 0;
                }
                if (empty)
                {
                    _pump?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    _pump = null;
                }
                else UpdateSubscription();
            }
            return ValueTask.CompletedTask;
        }
    }
}
