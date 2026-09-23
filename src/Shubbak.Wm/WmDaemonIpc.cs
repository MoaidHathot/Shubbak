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

    /// <summary>
    /// Icons already read, so a bar switching between the same few windows all day
    /// asks each of them once every half minute rather than once per focus change.
    /// </summary>
    private readonly WindowIconCache _icons = new();

    public WmDaemonIpc(WmDaemon daemon) => _daemon = daemon;

    public Task<IpcResponse> HandleAsync(IpcRequest request, IpcClientInfo client)
    {
        return request.Method switch
        {
            "command" => RunCommandAsync(request, client),
            "query" => QueryAsync(request),
            "inspect" => InspectAsync(request),
            WindowIcon.Method => Task.FromResult(WindowIconResponse(request)),
            "add-rule" => AddRuleAsync(request, client),
            "remove-rule" => RemoveRuleAsync(request, client),
            "diagnose" => DiagnoseAsync(request),
            "log-level" => SetLogLevelAsync(request),
            "ping" => Task.FromResult(new IpcResponse(request.Id, true, "pong")),
            _ => Task.FromResult(new IpcResponse(
                request.Id, false, null, $"unknown method '{request.Method}'")),
        };
    }

    /// <summary>
    /// Answers <c>window-icon</c>: a window's icon as pixels.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A capability of the window manager rather than of any one client, for the same
    /// reason the title comes over the pipe rather than from the bar reading windows
    /// itself: one process asks windows things, and everything else asks it. A bar, a
    /// palette, a keycast overlay or a script gets the same icon the taskbar shows
    /// without sending a message to a window that may be hung.
    /// </para>
    /// <para>
    /// On the pipe thread, deliberately, and never on the loop: nothing here touches the
    /// tree, and the one call that can wait - the message to the window - is bounded to
    /// a tenth of a second and gives up at once on a window already known to be hung.
    /// The loop, which holds the keyboard hook, must not be made to wait on another
    /// process for the sake of a picture.
    /// </para>
    /// <para>
    /// The payload is the handle as a decimal number, optionally followed by the size
    /// the client draws at, which picks the large or the small variant to try first.
    /// </para>
    /// </remarks>
    private IpcResponse WindowIconResponse(IpcRequest request)
    {
        string[] words = (request.Payload ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (words.Length == 0 || !long.TryParse(words[0], out long raw))
            return new IpcResponse(request.Id, false, null, "expected a window handle, optionally followed by a size");

        int size = words.Length > 1 && int.TryParse(words[1], out int wanted) ? wanted : 32;
        nint handle = (nint)raw;

        if (!Win32Window.Exists(handle))
            return new IpcResponse(request.Id, false, null, "no such window");

        if (_icons.Get(handle, size) is not { } pixels)
            return new IpcResponse(request.Id, false, null, "the window has no icon");

        var icon = new WindowIcon(raw, pixels.Width, pixels.Height, Convert.ToBase64String(pixels.Bgra), pixels.Source);

        return new IpcResponse(request.Id, true, JsonSerializer.Serialize(icon, IpcJsonContext.Default.WindowIcon));
    }

    /// <summary>
    /// Adds rules to the configuration file, and reloads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one method that writes to the user's file, and narrow on purpose: rules, and
    /// nothing else, appended as a block of their own. What may be written is decided
    /// in <c>ConfigEditor</c>, which refuses anything that is not a rule, anything the
    /// loader would reject or drop, and - unless the pipe may run it directly - a rule
    /// that runs <c>shell-exec</c>. The whole method is gated by
    /// <c>allow-config-edits-over-ipc</c>, which is on by default because every process
    /// that can reach the pipe can already reach the file.
    /// </para>
    /// <para>
    /// On the loop, because it rewrites the running configuration.
    /// </para>
    /// </remarks>
    private Task<IpcResponse> AddRuleAsync(IpcRequest request, IpcClientInfo client)
    {
        if (Parse(request, IpcJsonContext.Default.RuleAddition, out RuleAddition? addition, out string? problem) is false)
            return Task.FromResult(new IpcResponse(request.Id, false, null, problem));

        if (addition!.Kdl is not { Length: > 0 })
            return Task.FromResult(new IpcResponse(request.Id, false, null, "no rule given"));

        string requestedBy = Describe(client).Description;

        return _daemon.InvokeAsync(() =>
        {
            RuleChange? change = _daemon.AddRule(addition, requestedBy, out string? refusal);

            return change is null
                ? new IpcResponse(request.Id, false, null, refusal ?? "the rule was not added")
                : new IpcResponse(request.Id, true, JsonSerializer.Serialize(change, IpcJsonContext.Default.RuleChange));
        });
    }

    /// <summary>Removes one rule from the configuration file, and reloads.</summary>
    /// <remarks>
    /// Identified by name and line together, as a report gave them, and refused when the
    /// file no longer agrees with the report. See <c>ConfigEditor.PlanRemoval</c>.
    /// </remarks>
    private Task<IpcResponse> RemoveRuleAsync(IpcRequest request, IpcClientInfo client)
    {
        if (Parse(request, IpcJsonContext.Default.RuleRemoval, out RuleRemoval? removal, out string? problem) is false)
            return Task.FromResult(new IpcResponse(request.Id, false, null, problem));

        if (removal!.Name is not { Length: > 0 } || removal.Line <= 0)
            return Task.FromResult(new IpcResponse(request.Id, false, null, "a rule is named by its name and the line it begins on"));

        string requestedBy = Describe(client).Description;

        return _daemon.InvokeAsync(() =>
        {
            RuleChange? change = _daemon.RemoveRule(removal, requestedBy, out string? refusal);

            return change is null
                ? new IpcResponse(request.Id, false, null, refusal ?? "the rule was not removed")
                : new IpcResponse(request.Id, true, JsonSerializer.Serialize(change, IpcJsonContext.Default.RuleChange));
        });
    }

    /// <summary>Reads a JSON payload, or says why it could not.</summary>
    private static bool Parse<T>(
        IpcRequest request, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> shape, out T? value, out string? problem)
        where T : class
    {
        value = null;

        if (request.Payload is not { Length: > 0 } json)
        {
            problem = "no payload given";
            return false;
        }

        try
        {
            value = JsonSerializer.Deserialize(json, shape);
        }
        catch (JsonException ex)
        {
            problem = $"the payload could not be read: {ex.Message}";
            return false;
        }

        if (value is null)
        {
            problem = "the payload was empty";
            return false;
        }

        problem = null;
        return true;
    }

    /// <summary>
    /// Who sent a command, for the one command that wants to know.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Resolved on the pipe thread, before the hop to the message loop, so the loop pays
    /// nothing for it; and resolved only for a command that carries a context verb,
    /// because that is the only one that reads it - the pid lookup is a kernel call and
    /// the process name a handle open, neither of which every <c>focus</c> should pay.
    /// </para>
    /// <para>
    /// The name is best-effort. A process that has exited between sending and being
    /// asked about is described by its pid alone, which is still an answer.
    /// </para>
    /// </remarks>
    private static CommandOrigin Describe(IpcClientInfo client)
    {
        uint pid = Win32Foreground.ProcessIdOfPipeClient(client.PipeHandle);

        if (pid == 0) return new CommandOrigin(client.ConnectionId, "a pipe client");

        string? path = Win32Window.GetProcessPath(pid);
        string name = path is null ? $"pid {pid}" : $"{Path.GetFileName(path)} (pid {pid})";

        return new CommandOrigin(client.ConnectionId, name);
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
    /// The sequence stops at the first failure. A caller on the pipe is not watching
    /// anything and is owed an answer, and half-applying a sequence it cannot see is
    /// worse than refusing the rest of it.
    /// </para>
    /// <para>
    /// A keybinding bound to a list does not stop, because <c>WmDaemon.Execute</c>
    /// runs each command whatever the last one returned. That is a difference rather
    /// than a decision - it arrived with the per-command foreground resolution and
    /// nothing chose it - and it stands because <c>WmResult.Succeeded</c> is a poor
    /// thing to abort on: focusing left from the leftmost window is a routine refusal,
    /// not an error, so stopping on it would silently truncate every sequence that
    /// begins with a directional command at a screen edge. Anything that genuinely
    /// needs both halves to run should be one command, not two.
    /// </para>
    /// </remarks>
    private Task<IpcResponse> RunCommandAsync(IpcRequest request, IpcClientInfo client)
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

            CommandOrigin? origin = only is ContextCommand ? Describe(client) : null;

            return _daemon.InvokeAsync(() => RunAll(only!, null, request.Id, origin));
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

        CommandOrigin? sequenceOrigin = commands.Exists(c => c is ContextCommand) ? Describe(client) : null;

        return _daemon.InvokeAsync(() => RunAll(null, commands, request.Id, sequenceOrigin));
    }

    /// <summary>Runs one command, or a sequence, on the message loop.</summary>
    private IpcResponse RunAll(WmCommand? single, List<WmCommand>? sequence, int id, CommandOrigin? origin)
    {
        if (single is not null) return Report(_daemon.RunCommand(single, origin), id);

        foreach (WmCommand command in sequence!)
        {
            IpcResponse response = Report(_daemon.RunCommand(command, origin), id);
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
        if (what is "all-windows" or "every-window")
        {
            // every-window is the wider list: the windows the filter turned down for
            // their shape as well as for their kind, which are the ones a manage rule
            // exists for and which the ordinary list never shows.
            List<WindowCatalogue.Discovered> discovered = WindowCatalogue.Discover(everything: what is "every-window");

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
                    StateProjection.Snapshot(wm, _daemon.IsSuspended, _daemon.Session, _daemon.ActiveContexts),
                    IpcJsonContext.Default.StateSnapshot),

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

                "contexts" => JsonSerializer.Serialize(
                    _daemon.ReportContexts(), IpcJsonContext.Default.IReadOnlyListContextReport),

                "arrangements" => JsonSerializer.Serialize(
                    _daemon.DescribeArrangements(), IpcJsonContext.Default.IReadOnlyListArrangementInfo),

                "rules" => JsonSerializer.Serialize(
                    _daemon.DescribeRules(), IpcJsonContext.Default.IReadOnlyListRuleInfo),

                // Plain text rather than JSON: the same rendering shubbak diagnose puts
                // in its report, for reading the tree as it is rather than reconstructing
                // it from the windows list.
                "tree" => Core.Diagnostics.TreeRenderer.Render(wm.Root, wm.FocusedWindow),

                _ => string.Empty,
            };

            return json.Length == 0
                ? new IpcResponse(request.Id, false, null,
                    $"unknown query '{what}'. Try: state, tree, windows, all-windows, every-window, workspaces, " +
                    "monitors, focused, layouts, commands, bindings, contexts, arrangements, rules")
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
