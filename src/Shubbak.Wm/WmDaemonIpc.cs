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
            "ping" => Task.FromResult(new IpcResponse(request.Id, true, "pong")),
            _ => Task.FromResult(new IpcResponse(
                request.Id, false, null, $"unknown method '{request.Method}'")),
        };
    }

    /// <summary>Parses and runs a command string, exactly as a keybinding would.</summary>
    private Task<IpcResponse> RunCommandAsync(IpcRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Payload))
            return Task.FromResult(new IpcResponse(request.Id, false, null, "no command given"));

        var span = new TextSpan(new TextPosition(1, 1, 0), request.Payload.Length);

        if (!CommandParser.TryParse(request.Payload, span, out WmCommand? command, out Diagnostic? error))
        {
            string message = error!.Hint is null
                ? error.Message
                : $"{error.Message} {error.Hint}";

            return Task.FromResult(new IpcResponse(request.Id, false, null, message));
        }

        return _daemon.InvokeAsync(() =>
        {
            CommandOutcome outcome = _daemon.RunCommand(command!);

            return outcome.Succeeded
                ? new IpcResponse(request.Id, true)
                : new IpcResponse(request.Id, false, null,
                    outcome.Result.RejectionReason ?? "command was rejected");
        });
    }

    private Task<IpcResponse> QueryAsync(IpcRequest request)
    {
        string what = request.Payload ?? "state";

        return _daemon.InvokeAsync<IpcResponse>(() =>
        {
            Core.Wm.WindowManager wm = _daemon.Manager;

            string json = what switch
            {
                "state" => JsonSerializer.Serialize(
                    StateProjection.Snapshot(wm), IpcJsonContext.Default.StateSnapshot),

                "windows" => JsonSerializer.Serialize(
                    (IReadOnlyList<WindowInfo>)[.. wm.Root.DescendantWindows()
                        .Select(w => StateProjection.Describe(w, wm.FocusedWindow))],
                    IpcJsonContext.Default.IReadOnlyListWindowInfo),

                "workspaces" => JsonSerializer.Serialize(
                    (IReadOnlyList<WorkspaceInfo>)[.. wm.Root.AllWorkspaces().Select(StateProjection.Describe)],
                    IpcJsonContext.Default.IReadOnlyListWorkspaceInfo),

                "monitors" => JsonSerializer.Serialize(
                    (IReadOnlyList<MonitorInfoDto>)[.. wm.Root.Monitors.Select(StateProjection.Describe)],
                    IpcJsonContext.Default.IReadOnlyListMonitorInfoDto),

                "focused" => wm.FocusedWindow is { } focused
                    ? JsonSerializer.Serialize(
                        StateProjection.Describe(focused, focused), IpcJsonContext.Default.WindowInfo)
                    : "null",

                "layouts" => JsonSerializer.Serialize(
                    string.Join('\n', Core.Layouts.LayoutRegistry.CanonicalNames),
                    IpcJsonContext.Default.String),

                _ => string.Empty,
            };

            return json.Length == 0
                ? new IpcResponse(request.Id, false, null,
                    $"unknown query '{what}'. Try: state, windows, workspaces, monitors, focused, layouts")
                : new IpcResponse(request.Id, true, json);
        });
    }

    /// <summary>
    /// Describes a window and explains how Shubbak sees it.
    /// </summary>
    /// <remarks>
    /// The feature neither GlazeWM nor komorebi has: it answers "why is this window
    /// not being tiled?" directly, and shows which rules matched and which did not.
    /// Diagnosing that by trial and error is otherwise a genuinely miserable
    /// experience.
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

            return new IpcResponse(request.Id, true, _daemon.Inspect(handle));
        });
    }
}
