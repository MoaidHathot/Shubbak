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
        "workspace.activated,config.reloaded,wm.paused,wm.suspended," +
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

    /// <summary>
    /// Asks the window manager to describe a window.
    /// </summary>
    /// <remarks>
    /// The one request the palette makes whose answer it shows rather than acts on.
    /// The window manager already assembles this report - styles, cloak state, tags,
    /// and which rules matched - and until now only the command line could ask for it,
    /// which is the wrong place: by the time you are asking why a window is behaving
    /// oddly, you are looking at it, not at a shell.
    /// </remarks>
    /// <returns>
    /// The report, or null with the reason in <paramref name="failure"/>. The two are
    /// kept apart because the palette draws them differently: a report becomes a list
    /// of labelled facts, and a failure is one line saying why there is none.
    /// </returns>
    public async Task<WindowReport?> InspectAsync(long handle, Action<string> failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        try
        {
            await using IpcClient client = new();
            await client.ConnectAsync(TimeSpan.FromSeconds(5), _stopping.Token).ConfigureAwait(false);

            IpcResponse response = await client
                .SendAsync("inspect", handle.ToString(System.Globalization.CultureInfo.InvariantCulture), _stopping.Token)
                .ConfigureAwait(false);

            if (!response.Ok)
            {
                failure(response.Error ?? "The window manager would not describe it.");
                return null;
            }

            WindowReport? report = response.Data is { Length: > 0 } json
                ? JsonSerializer.Deserialize(json, IpcJsonContext.Default.WindowReport)
                : null;

            // Not a version mismatch: the protocol version is in the pipe name, so a
            // daemon from another release is never reached in the first place. Getting
            // here means the report itself was empty or malformed - said plainly,
            // because "Nothing to report" for a window that plainly exists sends
            // somebody looking for a bug in the wrong place.
            if (report is null) failure("The window manager sent a report that could not be read.");

            return report;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn(LogCategory.Ipc, $"could not inspect {handle:X}: {ex.Message}");
            failure($"Could not ask: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Asks the window manager for a report fit to attach to a bug tracker, and files it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>diagnose</c> method has existed on this pipe since the daemon did and
    /// the palette has never called it, so the only way to produce the one artefact
    /// designed to be handed to a maintainer was to open a terminal. That is exactly
    /// backwards: the report is wanted at the moment something has gone wrong on the
    /// desktop, which is the moment somebody is looking at their desktop.
    /// </para>
    /// <para>
    /// Written beside the logs rather than into the working directory, which for a
    /// process started from <c>startup-command</c> is wherever the window manager
    /// happened to be launched from - usually somewhere the user has never heard of.
    /// </para>
    /// </remarks>
    /// <returns>The path written, or null with the reason in <paramref name="failure"/>.</returns>
    public async Task<string?> DiagnoseAsync(Action<string> failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        try
        {
            await using IpcClient client = new();
            await client.ConnectAsync(TimeSpan.FromSeconds(5), _stopping.Token).ConfigureAwait(false);

            IpcResponse response = await client
                .SendAsync("diagnose", "dalil", _stopping.Token).ConfigureAwait(false);

            if (!response.Ok || response.Data is not { Length: > 0 } markdown)
            {
                failure(response.Error ?? "The window manager would not produce a report.");
                return null;
            }

            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Shubbak");

            Directory.CreateDirectory(folder);

            string path = Path.Combine(
                folder,
                $"diagnose-{DateTime.Now:yyyyMMdd-HHmmss}.md");

            await File.WriteAllTextAsync(path, markdown, _stopping.Token).ConfigureAwait(false);

            return path;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn(LogCategory.Ipc, $"could not write a diagnostic report: {ex.Message}");
            failure($"Could not write it: {ex.Message}");
            return null;
        }
    }

    /// <summary>Reads everything the palette can offer.</summary>
    /// <remarks>
    /// Several queries rather than one. They are asked together and only when the
    /// palette opens, so the cost is one round trip's worth of latency for a list that
    /// is current rather than remembered - and remembering it would mean an incremental
    /// update path, which is a whole class of bugs for a list that is on screen for a
    /// couple of seconds at a time.
    /// </remarks>
    /// <param name="includeUnmanaged">Whether the ordinary window list offers them.</param>
    /// <param name="macros">The user's own named sequences, which come from the config.</param>
    /// <param name="foreground">
    /// The window that had focus when the palette opened, for the row that answers
    /// "why is this one not being managed" before it is asked.
    /// </param>
    /// <param name="configProblems">
    /// How many things are wrong with the palette's own settings, so the command list
    /// can offer to show them. Zero leaves that row out.
    /// </param>
    public async Task<PaletteSources> ReadAsync(
        bool includeUnmanaged,
        IReadOnlyList<PaletteMacro>? macros = null,
        long foreground = 0,
        int configProblems = 0)
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

            IReadOnlyList<BindingInfo> bindings = await QueryAsync(
                client, "bindings", IpcJsonContext.Default.IReadOnlyListBindingInfo) ?? [];

            IReadOnlyList<MonitorInfoDto> monitors = await QueryAsync(
                client, "monitors", IpcJsonContext.Default.IReadOnlyListMonitorInfoDto) ?? [];

            // Not for a list of its own: this is how the palette learns that tiling is
            // paused, that a binding mode is swallowing keys, or that the manager has
            // let go of the keyboard altogether. All three make it look broken, and all
            // three were invisible here.
            StateSnapshot? state = await QueryAsync(
                client, "state", IpcJsonContext.Default.StateSnapshot);

            // The focused workspace is what "bring it here" means, and only the
            // workspace list knows which it is. Its monitor is what "near me" means.
            WorkspaceInfo? focused = workspaces.FirstOrDefault(w => w.Focused);
            string? here = focused?.Name;
            string? screen = focused?.Monitor;

            IReadOnlyList<string> names = [.. workspaces.Select(w => w.Name)];
            bool several = monitors.Count > 1;

            // What each workspace is called, for the pickers a prompting action opens.
            // A list reading "3", "\" and "'" is one nobody can choose from; the same
            // list reading "3 - Code" is the whole difference between the feature
            // working and it being a puzzle.
            Dictionary<string, string> labels = new(StringComparer.Ordinal);

            foreach (WorkspaceInfo workspace in workspaces)
                labels[workspace.Name] = workspace.DisplayName;

            var status = new WmStatus(
                state?.Paused ?? false,
                state?.BindingMode,
                state?.Suspended ?? false,
                Connected: true);

            var completions = new CompletionSources(
                names,
                layouts,
                [.. bindings.Where(b => b.Mode is { Length: > 0 }).Select(b => b.Mode!).Distinct()],
                [.. windows.Where(w => w.Scratchpad is { Length: > 0 }).Select(w => w.Scratchpad!).Distinct()]);

            IReadOnlyList<PaletteEntry> ownActions =
                PaletteEntries.ForMacros(macros ?? [], completions, labels);

            // The palette's own verbs and the user's own sequences sit above the window
            // manager's, because somebody who named a thing is looking for the name.
            List<PaletteEntry> everyCommand =
            [
                .. ownActions,
                .. PaletteEntries.ForBuiltins(configProblems, ownActions.Count),
                .. PaletteEntries.ForCommands(commands, status),
            ];

            return new PaletteSources(
                PaletteEntries.ForWindows(windows, includeUnmanaged, here, names, several, screen),
                everyCommand,
                PaletteEntries.ForWorkspaces(workspaces, several),
                PaletteEntries.ForLayouts(layouts, focused?.Layout),
                PaletteEntries.ForMonitors(monitors),
                PaletteEntries.ForScratchpad(windows, here, names),
                PaletteEntries.ForHelp(bindings),
                completions,
                status,

                // Deliberately built from the same unfiltered list, ignoring
                // includeUnmanaged. That setting keeps unmanaged windows out of the
                // ordinary list; honouring it here would leave the one mode whose
                // whole purpose is showing them permanently empty.
                PaletteEntries.ForSkipped(windows, here, names, several),

                here,
                names,
                [.. windows.Select(w => w.Handle)],
                PaletteEntries.ForContext(windows.FirstOrDefault(w => w.Handle == foreground)),
                labels);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn(LogCategory.Ipc, $"could not read the window list: {ex.Message}");

            // Marked offline rather than merely empty. The palette draws the two
            // differently, because a window manager that has died and one that is
            // still starting up produce an identical empty list and want opposite
            // advice.
            return PaletteSources.Offline;
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
/// <param name="Windows">Every window on the desktop.</param>
/// <param name="Commands">The user's macros, the palette's own verbs, then the manager's.</param>
/// <param name="Workspaces">Every workspace.</param>
/// <param name="Layouts">Every layout, with the one in use marked.</param>
/// <param name="Monitors">The displays, and what each is showing.</param>
/// <param name="Scratchpad">Windows put away, by slot.</param>
/// <param name="Help">The keys, the prefixes, and the user's own bindings.</param>
/// <param name="Completions">Values the command arguments can be completed from.</param>
/// <param name="Status">What the window manager is currently doing.</param>
/// <param name="Skipped">Windows Shubbak is not managing, and why not.</param>
/// <param name="FocusedWorkspace">Where "bring it here" means, for the bulk actions.</param>
/// <param name="WorkspaceNames">Everywhere a marked window could be sent.</param>
/// <param name="WindowHandles">
/// Every window that was reported, so their icons can be worked out on the background
/// thread this was read on rather than during a paint.
/// </param>
/// <param name="Context">
/// The row that answers a question before it is asked, or empty when there is nothing
/// worth saying.
/// </param>
/// <param name="WorkspaceLabels">
/// What each workspace is called, keyed by its name, for the pickers a prompting
/// action opens. A choice reading <c>\</c> is not one anybody can make.
/// </param>
public sealed record PaletteSources(
    IReadOnlyList<PaletteEntry> Windows,
    IReadOnlyList<PaletteEntry> Commands,
    IReadOnlyList<PaletteEntry> Workspaces,
    IReadOnlyList<PaletteEntry> Layouts,
    IReadOnlyList<PaletteEntry> Monitors,
    IReadOnlyList<PaletteEntry> Scratchpad,
    IReadOnlyList<PaletteEntry> Help,
    CompletionSources Completions,
    WmStatus Status,
    IReadOnlyList<PaletteEntry>? Skipped = null,
    string? FocusedWorkspace = null,
    IReadOnlyList<string>? WorkspaceNames = null,
    IReadOnlyList<long>? WindowHandles = null,
    IReadOnlyList<PaletteEntry>? Context = null,
    IReadOnlyDictionary<string, string>? WorkspaceLabels = null)
{
    /// <summary>
    /// Before anything has been read.
    /// </summary>
    /// <remarks>
    /// Help is built rather than fetched even here, so the one mode that explains the
    /// palette works before the first query lands and keeps working when the window
    /// manager is not running at all - which is exactly when somebody wants an
    /// explanation.
    /// </remarks>
    public static PaletteSources Empty { get; } = new(
        [], [], [], [], [], [], PaletteEntries.ForHelp(), CompletionSources.None, WmStatus.Unknown);

    /// <summary>After a read that could not reach the window manager.</summary>
    /// <remarks>
    /// Distinct from <see cref="Empty"/> only in its status, and that difference is the
    /// whole point: it is what lets the palette say "can't reach the window manager"
    /// rather than "the window manager may still be starting up", which was its advice
    /// for a daemon that had been dead for an hour.
    /// </remarks>
    public static PaletteSources Offline { get; } = new(
        [], [], [], [], [], [], PaletteEntries.ForHelp(), CompletionSources.None, WmStatus.Offline);

    /// <summary>The rows for one mode.</summary>
    public IReadOnlyList<PaletteEntry> For(PaletteMode mode) => mode switch
    {
        PaletteMode.Commands => Commands,
        PaletteMode.Workspaces => Workspaces,
        PaletteMode.Layouts => Layouts,
        PaletteMode.Monitors => Monitors,
        PaletteMode.Scratchpad => Scratchpad,
        PaletteMode.Help => Help,
        PaletteMode.Inspect => Skipped ?? [],
        _ => Windows,
    };
}
