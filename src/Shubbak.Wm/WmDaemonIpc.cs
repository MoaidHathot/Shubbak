using System.Text.Json;
using Shubbak.Config;
using Shubbak.Core.Commands;
using Shubbak.Core.Tree;
using Shubbak.Ipc;
using Shubbak.Native;

namespace Shubbak.Wm;

/// <summary>
/// The IPC methods the daemon exposes.
/// </summary>
/// <remarks>
/// Every method routes through the same <see cref="CommandExecutor"/> that
/// keybindings use, so the CLI and a keypress cannot diverge in behaviour - a real
/// problem in window managers that grow a second command path for their CLI.
/// </remarks>
internal sealed partial class WmDaemonIpc
{
    private readonly WmDaemon _daemon;

    public WmDaemonIpc(WmDaemon daemon) => _daemon = daemon;

    public Task<IpcResponse> HandleAsync(IpcRequest request)
    {
        return request.Method switch
        {
            "command" => RunCommandAsync(request),
            "query" => QueryAsync(request),
            "inspect" => InspectAsync(request),
            "diagnose" => DiagnoseAsync(request),
            "log-level" => SetLogLevelAsync(request),
            "ping" => Task.FromResult(new IpcResponse(request.Id, true, "pong")),
            _ => Task.FromResult(new IpcResponse(
                request.Id, false, null, $"unknown method '{request.Method}'")),
        };
    }

    /// <summary>
    /// Builds a self-contained diagnostic report.
    /// </summary>
    /// <remarks>
    /// Assembled inside the daemon rather than the CLI because only the daemon can
    /// see the live tree and the log ring buffer. The result is a single file that
    /// can be attached to a bug report as-is.
    /// </remarks>
    private Task<IpcResponse> DiagnoseAsync(IpcRequest request)
    {
        return _daemon.InvokeAsync(() =>
            new IpcResponse(request.Id, true, _daemon.BuildDiagnosticReport(request.Payload ?? "manual")));
    }

    /// <summary>
    /// Changes the log level on a running window manager.
    /// </summary>
    /// <remarks>
    /// Being able to raise the level without restarting is what makes an
    /// intermittent problem catchable: restarting to enable tracing usually loses
    /// the state that was about to trigger it.
    /// </remarks>
    private static Task<IpcResponse> SetLogLevelAsync(IpcRequest request)
    {
        if (!Core.Diagnostics.Log.TryParseLevel(request.Payload, out Core.Diagnostics.LogLevel level))
        {
            return Task.FromResult(new IpcResponse(
                request.Id, false, null,
                $"unknown log level '{request.Payload}'. Use trace, debug, info, warn, error or none."));
        }

        Core.Diagnostics.LogLevel previous = Core.Diagnostics.Log.Level;
        Core.Diagnostics.Log.Level = level;

        Core.Diagnostics.Log.Info(
            Core.Diagnostics.LogCategory.Wm, $"log level changed from {previous} to {level}");

        return Task.FromResult(new IpcResponse(request.Id, true, level.ToString()));
    }

    /// <summary>Parses and runs a command string, exactly as a keybinding would.</summary>
    /// <remarks>
    /// Accepts several commands separated by newlines, so a client can express "focus
    /// that window, then un-minimise it" without two round trips and without the
    /// window able to change underneath it in between.
    /// <para>
    /// The sequence stops at the first failure, which a keybinding bound to a list of
    /// commands deliberately does not - see <c>WmDaemon.Execute</c>. A key is pressed
    /// by somebody watching the screen, so a command that achieves nothing is visibly
    /// nothing and the rest of the list is still what they asked for. A caller on the
    /// pipe is not watching anything and is owed an answer, and half-applying a
    /// sequence it cannot see is worse than refusing the rest of it.
    /// </para>
    /// </remarks>
    private Task<IpcResponse> RunCommandAsync(IpcRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Payload))
            return Task.FromResult(new IpcResponse(request.Id, false, null, "no command given"));

        // One command is the overwhelmingly common case and must not pay for the rare
        // one: a vectorised scan of a short string, and then no list, no split and no
        // array.
        //
        // Newlines rather than semicolons as the separator, because shell-exec takes
        // the rest of its line verbatim - a semicolon inside a quoted command line
        // would be split in a way no amount of escaping makes obvious.
        if (!request.Payload.AsSpan().Contains('\n'))
        {
            if (!TryAccept(request.Payload, out WmCommand? only, out string? refusal))
                return Task.FromResult(new IpcResponse(request.Id, false, null, refusal));

            return _daemon.InvokeAsync(() => RunAll(only!, null, request.Id));
        }

        List<WmCommand> commands = [];

        foreach (string line in request.Payload.Split('\n'))
        {
            if (line.AsSpan().Trim().Length == 0) continue;

            if (!TryAccept(line.Trim(), out WmCommand? parsed, out string? failure))
                return Task.FromResult(new IpcResponse(request.Id, false, null, failure));

            commands.Add(parsed!);
        }

        if (commands.Count == 0)
            return Task.FromResult(new IpcResponse(request.Id, false, null, "no command given"));

        return _daemon.InvokeAsync(() => RunAll(null, commands, request.Id));
    }

    /// <summary>Runs one command, or a sequence, on the message loop.</summary>
    private IpcResponse RunAll(WmCommand? single, List<WmCommand>? sequence, int id)
    {
        if (single is not null) return Report(_daemon.RunCommand(single), id);

        foreach (WmCommand command in sequence!)
        {
            IpcResponse response = Report(_daemon.RunCommand(command), id);
            if (!response.Ok) return response;
        }

        return new IpcResponse(id, true);
    }

    private static IpcResponse Report(CommandOutcome outcome, int id) =>
        outcome.Succeeded
            ? new IpcResponse(id, true)
            : new IpcResponse(id, false, null,
                outcome.Result.RejectionReason ?? "command was rejected");

    /// <summary>
    /// Parses one command and decides whether the pipe may run it.
    /// </summary>
    /// <remarks>
    /// A window manager is not an execution service.
    /// <para>
    /// shell-exec exists so a keybinding or a startup command can launch a terminal,
    /// which is a decision the user made in their config. Nothing about that requires
    /// it to be reachable at runtime by any process that can open the pipe - and the
    /// pipe is scoped to the account, not to the integrity level, so an ordinary
    /// process can reach the pipe of an elevated daemon and have it start something
    /// elevated. Shubbak tells users to run elevated to manage elevated windows, which
    /// makes that a realistic path rather than a theoretical one.
    /// </para>
    /// <para>
    /// Off by default, and a config key rather than a rebuild for anyone who wants to
    /// drive Shubbak as a launcher deliberately.
    /// </para>
    /// <para>
    /// <c>signal</c> is deliberately not gated the same way. It starts nothing and
    /// reaches only clients already connected to the same per-user pipe, so the worst
    /// a caller can do is ask a bar to open a window the user could have opened with a
    /// keystroke.
    /// </para>
    /// </remarks>
    private bool TryAccept(string text, out WmCommand? command, out string? refusal)
    {
        var span = new TextSpan(new TextPosition(1, 1, 0), text.Length);

        if (!CommandParser.TryParse(text, span, out command, out Diagnostic? error))
        {
            refusal = error!.Hint is null ? error.Message : $"{error.Message} {error.Hint}";
            return false;
        }

        if (command is ShellExecCommand && !_daemon.AllowShellExecOverIpc)
        {
            refusal =
                "shell-exec is not accepted over the pipe. It stays available to " +
                "keybindings, rules and startup commands. Set " +
                "general { allow-shell-exec-over-ipc #true } to permit it here.";
            return false;
        }

        refusal = null;
        return true;
    }

    private Task<IpcResponse> QueryAsync(IpcRequest request)
    {
        string what = request.Payload ?? "state";

        // Answered before the message loop is involved at all.
        //
        // Enumerating the desktop is several hundred windows of Win32 reads, and none
        // of it touches the tree. Marshalling it onto the tick would put that work in
        // front of the layout pass for no reason, and the tick is the one thread that
        // must not wait for anything. Only the join needs to be there.
        if (what is "all-windows")
        {
            List<WindowCatalogue.Discovered> discovered = WindowCatalogue.Discover();

            return _daemon.InvokeAsync(() => new IpcResponse(request.Id, true,
                JsonSerializer.Serialize(
                    WindowCatalogue.Join(discovered, _daemon.Manager, _daemon.Windows),
                    IpcJsonContext.Default.IReadOnlyListWindowCandidate)));
        }

        // Neither of these reads mutable state either: the catalogue is immutable and
        // the binding list belongs to the loaded configuration.
        if (what is "commands")
        {
            return Task.FromResult(new IpcResponse(request.Id, true,
                JsonSerializer.Serialize(Describe(), IpcJsonContext.Default.IReadOnlyListCommandInfo)));
        }

        return _daemon.InvokeAsync<IpcResponse>(() =>
        {
            Core.Wm.WindowManager wm = _daemon.Manager;

            string json = what switch
            {
                "state" => JsonSerializer.Serialize(
                    StateProjection.Snapshot(wm, _daemon.IsSuspended), IpcJsonContext.Default.StateSnapshot),

                "windows" => JsonSerializer.Serialize(
                    (IReadOnlyList<WindowInfo>)[.. wm.Root.DescendantWindows()
                        .Select(w => StateProjection.Describe(w, wm.FocusedWindow))],
                    IpcJsonContext.Default.IReadOnlyListWindowInfo),

                "workspaces" => JsonSerializer.Serialize(
                    (IReadOnlyList<WorkspaceInfo>)[.. StateProjection.DescribeWorkspaces(wm)],
                    IpcJsonContext.Default.IReadOnlyListWorkspaceInfo),

                "monitors" => JsonSerializer.Serialize(
                    (IReadOnlyList<MonitorInfoDto>)[.. wm.Root.Monitors.Select(StateProjection.Describe)],
                    IpcJsonContext.Default.IReadOnlyListMonitorInfoDto),

                "focused" => wm.FocusedWindow is { } focused
                    ? JsonSerializer.Serialize(
                        StateProjection.Describe(focused, focused), IpcJsonContext.Default.WindowInfo)
                    : "null",

                "layouts" => JsonSerializer.Serialize(
                    (IReadOnlyList<string>)[.. Core.Layouts.LayoutRegistry.CanonicalNames],
                    IpcJsonContext.Default.IReadOnlyListString),

                "bindings" => JsonSerializer.Serialize(
                    _daemon.DescribeBindings(), IpcJsonContext.Default.IReadOnlyListBindingInfo),

                _ => string.Empty,
            };

            return json.Length == 0
                ? new IpcResponse(request.Id, false, null,
                    $"unknown query '{what}'. Try: state, windows, all-windows, workspaces, " +
                    "monitors, focused, layouts, commands, bindings")
                : new IpcResponse(request.Id, true, json);
        });
    }

    /// <summary>The command set, so a client need not hard-code it.</summary>
    private static IReadOnlyList<CommandInfo> Describe() =>
    [
        .. CommandCatalogue.Commands.Select(c => new CommandInfo(
            c.Verb,
            c.Summary,
            [.. c.Arguments.Select(Spell)],
            c.Aliases)),
    ];

    /// <summary>Spells an argument kind the way a person would write it.</summary>
    /// <remarks>
    /// The enum name lower-cased reads as <c>windowhandle</c>, which a client shows
    /// verbatim beside the verb. Kebab-case is what the rest of the configuration
    /// language uses and what anybody would type.
    /// </remarks>
    private static string Spell(CommandArgument argument)
    {
        string name = argument.ToString();
        var spelled = new System.Text.StringBuilder(name.Length + 4);

        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i])) spelled.Append('-');
            spelled.Append(char.ToLowerInvariant(name[i]));
        }

        return spelled.ToString();
    }

    /// <summary>
    /// Describes a window and explains how Shubbak sees it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The feature neither GlazeWM nor komorebi has: it answers "why is this window
    /// not being tiled?" directly, and shows which rules matched and which did not.
    /// Diagnosing that by trial and error is otherwise a genuinely miserable
    /// experience.
    /// </para>
    /// <para>
    /// Answers with a <see cref="WindowReport"/> rather than the text of one. Both
    /// clients that ask want different shapes - printed columns for the command line,
    /// rows for the palette - and sending the columns meant the palette recovered the
    /// fields by splitting on the padding.
    /// </para>
    /// </remarks>
    private Task<IpcResponse> InspectAsync(IpcRequest request)
    {
        if (!long.TryParse(request.Payload, out long raw))
            return Task.FromResult(new IpcResponse(request.Id, false, null, "expected a window handle"));

        nint handle = (nint)raw;

        return _daemon.InvokeAsync(() =>
        {
            if (!Win32Window.Exists(handle))
                return new IpcResponse(request.Id, false, null, "no such window");

            return new IpcResponse(request.Id, true, JsonSerializer.Serialize(
                _daemon.Inspect(handle), IpcJsonContext.Default.WindowReport));
        });
    }
}
