using System.Text.Json;
using Dalil.Core;
using Shubbak.Core.Diagnostics;
using Shubbak.Ipc;

namespace Dalil;

/// <summary>What the window manager has told us to do.</summary>
/// <param name="Signal">The signal name.</param>
/// <param name="Arguments">Anything that followed it.</param>
public readonly record struct SignalRaised(string Signal, IReadOnlyList<string> Arguments);

/// <summary>
/// Dalil's end of the pipe.
/// </summary>
/// <remarks>
/// <para>
/// Subscribes to <c>signal</c>, and to the window events that mean the list is stale.
/// It does not subscribe to everything: a title changing on a video that is playing
/// is several events a second, and rebuilding an unopened palette for each of them
/// would make an idle machine busy.
/// </para>
/// <para>
/// Everything here runs on a background thread. The window is owned by the message
/// loop, so anything reaching it is posted rather than called.
/// </para>
/// </remarks>
public sealed class WmConnection : IAsyncDisposable
{
    /// <summary>
    /// The topics worth reloading the window list for.
    /// </summary>
    /// <remarks>
    /// Deliberately excludes <c>window.title_changed</c>. It is by far the most
    /// frequent event on a desktop - a playing video produces one every few frames -
    /// and a title that has changed while the palette is closed is read fresh the
    /// next time it opens.
    /// </remarks>
    private const string Topics =
        IpcProtocol.SignalTopic +
        ",window.managed,window.unmanaged,window.focused,window.state_changed," +
        "workspace.activated,config.reloaded," +
        IpcProtocol.ShutdownTopic + "," + IpcProtocol.ResyncTopic;

    private readonly CancellationTokenSource _stopping = new();
    private Task? _pump;

    /// <summary>Raised on a background thread when the window manager signals.</summary>
    public event Action<SignalRaised>? Signalled;

    /// <summary>Raised on a background thread when the window list may have changed.</summary>
    public event Action? Stale;

    /// <summary>Raised on a background thread when the window manager is going away.</summary>
    public event Action? ShuttingDown;

    /// <summary>Raised on a background thread when the configuration was reloaded.</summary>
    public event Action? Reloaded;

    public void Start() => _pump = Task.Run(() => PumpAsync(_stopping.Token));

    /// <summary>Asks the window manager to run a command.</summary>
    public async Task SendAsync(string command)
    {
        try
        {
            await using IpcClient client = new();
            await client.ConnectAsync(TimeSpan.FromSeconds(5), _stopping.Token).ConfigureAwait(false);

            IpcResponse response = await client
                .SendAsync("command", command, _stopping.Token).ConfigureAwait(false);

            if (!response.Ok)
                Log.Warn(LogCategory.Ipc, $"'{command}' was refused: {response.Error}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn(LogCategory.Ipc, $"could not send '{command}': {ex.Message}");
        }
    }

    /// <summary>Reads everything the palette can offer.</summary>
    /// <remarks>
    /// Four queries rather than one. They are asked together and only when the palette
    /// opens, so the cost is one round trip's worth of latency for a list that is
    /// current rather than remembered - and remembering it would mean an incremental
    /// update path, which is a whole class of bugs for a list that is on screen for a
    /// couple of seconds at a time.
    /// </remarks>
    public async Task<PaletteSources> ReadAsync(bool includeUnmanaged)
    {
        try
        {
            await using IpcClient client = new();
            await client.ConnectAsync(TimeSpan.FromSeconds(5), _stopping.Token).ConfigureAwait(false);

            IReadOnlyList<WindowCandidate> windows = await QueryAsync(
                client, "all-windows", IpcJsonContext.Default.IReadOnlyListWindowCandidate) ?? [];

            IReadOnlyList<CommandInfo> commands = await QueryAsync(
                client, "commands", IpcJsonContext.Default.IReadOnlyListCommandInfo) ?? [];

            IReadOnlyList<WorkspaceInfo> workspaces = await QueryAsync(
                client, "workspaces", IpcJsonContext.Default.IReadOnlyListWorkspaceInfo) ?? [];

            IReadOnlyList<string> layouts = await QueryAsync(
                client, "layouts", IpcJsonContext.Default.IReadOnlyListString) ?? [];

            return new PaletteSources(
                PaletteEntries.ForWindows(windows, includeUnmanaged),
                PaletteEntries.ForCommands(commands),
                PaletteEntries.ForWorkspaces(workspaces),
                PaletteEntries.ForLayouts(layouts));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn(LogCategory.Ipc, $"could not read the window list: {ex.Message}");
            return PaletteSources.Empty;
        }
    }

    private async Task<T?> QueryAsync<T>(
        IpcClient client, string what, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> shape)
    {
        IpcResponse response = await client.SendAsync("query", what, _stopping.Token).ConfigureAwait(false);

        if (!response.Ok || response.Data is null)
        {
            Log.Warn(LogCategory.Ipc, $"query {what} failed: {response.Error}");
            return default;
        }

        return JsonSerializer.Deserialize(response.Data, shape);
    }

    /// <summary>
    /// Reads the event stream, reconnecting for as long as the process runs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A palette that stops working because the window manager was restarted is a
    /// palette that has to be restarted too, and nobody will know to.
    /// </para>
    /// <para>
    /// Two failures that look alike and are not. Failing to connect is transient - the
    /// daemon is starting, or restarting - and is worth retrying quietly and often. A
    /// refused <em>subscription</em> is not: the server rejects topics it does not
    /// know, so this means a version mismatch, it will not fix itself, and retrying
    /// every second produces a line a second in the log and nothing else. So the
    /// reason is said out loud once and the retry backs off.
    /// </para>
    /// </remarks>
    private async Task PumpAsync(CancellationToken token)
    {
        TimeSpan wait = TimeSpan.FromSeconds(1);
        string? lastComplaint = null;

        while (!token.IsCancellationRequested)
        {
            try
            {
                await using IpcClient client = new();
                await client.ConnectAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);

                IAsyncEnumerator<IpcEvent> events =
                    client.SubscribeAsync(Topics, token).GetAsyncEnumerator(token);

                // Announced only once the subscription has been accepted. Saying
                // "connected" before asking would report success for a connection
                // that is about to be refused, which is exactly what it did.
                bool announced = false;

                try
                {
                    while (await events.MoveNextAsync().ConfigureAwait(false))
                    {
                        if (!announced)
                        {
                            Log.Info(LogCategory.Ipc, "connected; listening for signals");
                            announced = true;
                            wait = TimeSpan.FromSeconds(1);
                            lastComplaint = null;
                        }

                        Dispatch(events.Current);
                    }
                }
                finally
                {
                    await events.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (InvalidOperationException ex)
            {
                // The subscription was refused. Almost always a daemon older than
                // this build, which does not publish the topics being asked for.
                if (lastComplaint != ex.Message)
                {
                    Log.Warn(LogCategory.Ipc,
                        $"the window manager refused the subscription: {ex.Message}. " +
                        "This usually means shubbak-wm is older than dalil; restart it.");

                    lastComplaint = ex.Message;
                }

                wait = TimeSpan.FromSeconds(30);
            }
            catch (Exception ex)
            {
                Log.Debug(LogCategory.Ipc, $"connection lost: {ex.Message}");
                wait = TimeSpan.FromSeconds(1);
            }

            try
            {
                await Task.Delay(wait, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Dispatch(IpcEvent raised)
    {
        switch (raised.Topic)
        {
            case IpcProtocol.SignalTopic:
                if (Parse(raised.Data) is { } signal) Signalled?.Invoke(signal);
                break;

            case IpcProtocol.ShutdownTopic:
                ShuttingDown?.Invoke();
                break;

            case "config.reloaded":
                Reloaded?.Invoke();
                break;

            default:
                Stale?.Invoke();
                break;
        }
    }

    /// <summary>Reads a signal payload without a reflection-based deserialiser.</summary>
    /// <remarks>
    /// Hand-parsed because the payload is two fields and adding a DTO to the protocol
    /// for it would make every client that does not care about signals carry it.
    /// </remarks>
    private static SignalRaised? Parse(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("name", out JsonElement name)) return null;

            List<string> arguments = [];

            if (document.RootElement.TryGetProperty("arguments", out JsonElement list))
                foreach (JsonElement argument in list.EnumerateArray())
                    if (argument.GetString() is { } value)
                        arguments.Add(value);

            return new SignalRaised(name.GetString() ?? string.Empty, arguments);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);

        if (_pump is { } pump)
        {
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: cancellation is how the pump is asked to stop.
            }
        }

        _stopping.Dispose();
    }
}

/// <summary>Everything the palette can offer, by mode.</summary>
public sealed record PaletteSources(
    IReadOnlyList<PaletteEntry> Windows,
    IReadOnlyList<PaletteEntry> Commands,
    IReadOnlyList<PaletteEntry> Workspaces,
    IReadOnlyList<PaletteEntry> Layouts)
{
    public static PaletteSources Empty { get; } = new([], [], [], []);

    /// <summary>The rows for one mode.</summary>
    public IReadOnlyList<PaletteEntry> For(PaletteMode mode) => mode switch
    {
        PaletteMode.Commands => Commands,
        PaletteMode.Workspaces => Workspaces,
        PaletteMode.Layouts => Layouts,
        _ => Windows,
    };
}
