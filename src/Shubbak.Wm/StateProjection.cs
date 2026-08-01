using System.Text.Json;
using Shubbak.Core.Tree;
using Shubbak.Core.Wm;
using Shubbak.Ipc;

namespace Shubbak.Wm;

/// <summary>
/// Projects window manager state into the IPC data contract.
/// </summary>
/// <remarks>
/// A translation layer rather than exposing the tree directly. Clients get a flat,
/// stable shape that can survive internal refactoring - and, importantly, cannot
/// hold references to live nodes.
/// </remarks>
internal static class StateProjection
{
    public static WindowInfo Describe(WindowNode window, WindowNode? focused) => new(
        window.Id.Value,
        window.Handle,
        window.Identity.Title,
        window.Identity.ClassName,
        window.Identity.ProcessName,
        window.State.ToString().ToLowerInvariant(),
        ReferenceEquals(window, focused),
        window.Rect.X,
        window.Rect.Y,
        window.Rect.Width,
        window.Rect.Height);

    public static WorkspaceInfo Describe(WorkspaceNode workspace, int monitorIndex = -1) => new(
        workspace.Id.Value,
        workspace.Name,
        workspace.Label,
        workspace.IsActive,
        !workspace.HasNoWindows,
        workspace.Monitor?.DeviceId ?? string.Empty,
        workspace.Layout.Name,
        workspace.DescendantWindows().Count(),
        workspace.SortIndex,
        monitorIndex);

    public static MonitorInfoDto Describe(MonitorNode monitor) => new(
        monitor.Id.Value,
        monitor.DeviceId,
        monitor.IsPrimary,
        monitor.Dpi,
        monitor.Bounds.X,
        monitor.Bounds.Y,
        monitor.Bounds.Width,
        monitor.Bounds.Height,
        monitor.ActiveWorkspace?.Name);

    public static StateSnapshot Snapshot(WindowManager wm)
    {
        WindowNode? focused = wm.FocusedWindow;

        return new StateSnapshot(
            [.. wm.Root.Monitors.Select(Describe)],
            [.. DescribeWorkspaces(wm)],
            [.. wm.Root.DescendantWindows().Select(w => Describe(w, focused))],
            focused is null ? null : Describe(focused, focused),
            wm.BindingMode,
            wm.IsPaused);
    }

    /// <summary>Serialises an event's payload for publication.</summary>
    /// <summary>
    /// Describes every workspace, tagged with the index of the monitor it is on.
    /// </summary>
    /// <remarks>
    /// The index lets the bar show only its own monitor''s workspaces, which is what
    /// makes a per-monitor bar useful rather than three identical copies of the same
    /// list.
    /// </remarks>
    public static IEnumerable<WorkspaceInfo> DescribeWorkspaces(WindowManager wm)
    {
        for (int index = 0; index < wm.Root.Monitors.Count; index++)
            foreach (WorkspaceNode workspace in wm.Root.Monitors[index].Workspaces)
                yield return Describe(workspace, index);
    }

    public static string Payload(WmEvent wmEvent, WindowManager wm)
    {
        WindowNode? focused = wm.FocusedWindow;

        return wmEvent switch
        {
            WindowManaged e => Json(Describe(e.Window, focused)),
            WindowFocused e => e.Window is null ? "null" : Json(Describe(e.Window, focused)),
            WindowTitleChanged e => Json(Describe(e.Window, focused)),
            WindowStateChanged e => Json(Describe(e.Window, focused)),
            WindowMoved e => Json(Describe(e.Window, focused)),
            WorkspaceActivated e => Json(Describe(e.Workspace)),
            WorkspaceCreated e => Json(Describe(e.Workspace)),
            WorkspaceMoved e => Json(Describe(e.Workspace)),
            MonitorAdded e => Json(Describe(e.Monitor)),
            MonitorChanged e => Json(Describe(e.Monitor)),

            // Events describing something that no longer exists carry only what is
            // still meaningful.
            WindowUnmanaged e => $"{{\"id\":{e.Id.Value},\"handle\":{e.Handle}}}",
            WorkspaceDestroyed e => $"{{\"id\":{e.Id.Value},\"name\":{JsonString(e.Name)}}}",
            MonitorRemoved e => $"{{\"id\":{e.Id.Value},\"device_id\":{JsonString(e.DeviceId)}}}",
            BindingModeChanged e => e.Mode is null ? "null" : JsonString(e.Mode),
            LayoutChanged e => $"{{\"layout\":{JsonString(e.Layout)}}}",
            CommandRejected e =>
                $"{{\"command\":{JsonString(e.Command)}," +
                $"\"reason\":{JsonString(e.Reason)}}}",

            _ => "{}",
        };
    }

    /// <summary>Serialises a string through the source-generated context.</summary>
    /// <remarks>
    /// Reflection-based serialisation is forbidden by ADR 0001 constraint 6, and
    /// the AOT analyser enforces it - which is exactly what it is for.
    /// </remarks>
    private static string JsonString(string value) =>
        JsonSerializer.Serialize(value, IpcJsonContext.Default.String);

    private static string Json(WindowInfo value) =>
        JsonSerializer.Serialize(value, IpcJsonContext.Default.WindowInfo);

    private static string Json(WorkspaceInfo value) =>
        JsonSerializer.Serialize(value, IpcJsonContext.Default.WorkspaceInfo);

    private static string Json(MonitorInfoDto value) =>
        JsonSerializer.Serialize(value, IpcJsonContext.Default.MonitorInfoDto);
}
