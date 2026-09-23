using Shubbak.Ipc;
using Taj.Core.Widgets;

namespace Taj.Core;

/// <summary>
/// What one bar reads off the window manager's state for the display it sits on.
/// </summary>
/// <param name="Workspaces">The workspace list, encoded for the <c>workspaces</c> widget.</param>
/// <param name="ActiveWorkspace">The workspace this display is showing, or empty when the snapshot names none.</param>
/// <param name="MonitorIndex">Where this display sits in the window manager's list, or -1.</param>
/// <param name="MonitorNames">What the window manager's configuration calls this display.</param>
/// <param name="Layout">The layout of the workspace this display is showing, or empty.</param>
public sealed record BarReading(
    string Workspaces,
    string ActiveWorkspace,
    int MonitorIndex,
    IReadOnlyList<string> MonitorNames,
    string Layout);

/// <summary>
/// Turns a state snapshot into what a bar on one display shows.
/// </summary>
/// <remarks>
/// <para>
/// Pure, and in <c>Taj.Core</c> rather than beside the pipe that fetches the snapshot,
/// because every decision in it has been wrong at some point and none could be tested
/// where it lived: which monitor's layout the indicator shows, which workspaces a
/// per-monitor bar lists, what order they come in, and what a JSON <c>null</c> in a
/// binding-mode payload turns into.
/// </para>
/// <para>
/// The scratchpad is a workspace internally so the tree works on it unchanged, but it
/// is not something the user switches to; any workspace whose name starts with
/// <c>__</c> is left out for that reason.
/// </para>
/// </remarks>
public static class SnapshotProjection
{
    /// <summary>The prefix that marks a workspace as the window manager's own rather than the user's.</summary>
    public const string InternalWorkspacePrefix = "__";

    /// <summary>Reads what a bar on <paramref name="deviceId"/> shows.</summary>
    /// <param name="state">The window manager's state.</param>
    /// <param name="deviceId">The GDI device name of the bar's display, <c>\\.\DISPLAY2</c>.</param>
    /// <param name="ownMonitorOnly">Whether to list only this display's workspaces.</param>
    public static BarReading Read(StateSnapshot state, string deviceId, bool ownMonitorOnly)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrEmpty(deviceId);

        // The position this display holds in the window manager's list, for bar rules
        // written as monitor=N, and the names its configuration gives it, for rules
        // written as monitor="name". Read off the snapshot every time rather than
        // remembered, because a monitor coming or going moves the one and a reload can
        // change the other.
        int monitorIndex = IndexOf(state.Monitors, deviceId);
        IReadOnlyList<string> monitorNames = monitorIndex >= 0
            ? state.Monitors[monitorIndex].Names ?? []
            : [];

        List<WorkspaceInfo> visible = [];
        string active = string.Empty;

        foreach (WorkspaceInfo workspace in state.Workspaces)
        {
            if (workspace.Name.StartsWith(InternalWorkspacePrefix, StringComparison.Ordinal)) continue;

            bool onThisMonitor = string.Equals(workspace.Monitor, deviceId, StringComparison.OrdinalIgnoreCase);

            // The active workspace of this monitor is what selects the bar profile, so
            // it is noted before any filtering.
            if (workspace.Active && onThisMonitor && active.Length == 0)
                active = workspace.Name;

            if (ownMonitorOnly && !onThisMonitor) continue;

            visible.Add(workspace);
        }

        // Declared order, not creation order and not whichever monitor a workspace
        // currently sits on. alt+1 is first because the user wrote it first, and that
        // has to hold however the workspaces move around.
        visible.Sort(static (a, b) => a.SortIndex != b.SortIndex
            ? a.SortIndex.CompareTo(b.SortIndex)
            : string.CompareOrdinal(a.Name, b.Name));

        List<WorkspacesWidget.WorkspaceEntry> entries =
        [
            .. visible.Select(w => new WorkspacesWidget.WorkspaceEntry(
                w.Name, w.DisplayName, w.Active, w.HasWindows, w.Focused)),
        ];

        return new BarReading(
            WorkspacesWidget.Encode(entries),
            active,
            monitorIndex,
            monitorNames,
            ActiveLayout(state, deviceId));
    }

    /// <summary>Where a display sits in the window manager's list, or -1.</summary>
    public static int IndexOf(IReadOnlyList<MonitorInfoDto> monitors, string deviceId)
    {
        ArgumentNullException.ThrowIfNull(monitors);

        for (int index = 0; index < monitors.Count; index++)
        {
            if (string.Equals(monitors[index].DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                return index;
        }

        return -1;
    }

    /// <summary>The layout of the workspace displayed on one monitor, or empty.</summary>
    /// <remarks>
    /// Filtered by monitor. Taking the first active workspace in the snapshot meant
    /// every bar on every monitor showed the first monitor's layout, so the indicator
    /// was wrong on all but one display and changed when the user was not looking.
    /// </remarks>
    public static string ActiveLayout(StateSnapshot state, string deviceId)
    {
        ArgumentNullException.ThrowIfNull(state);

        foreach (WorkspaceInfo workspace in state.Workspaces)
        {
            if (!workspace.Active) continue;
            if (!string.Equals(workspace.Monitor, deviceId, StringComparison.OrdinalIgnoreCase)) continue;

            return workspace.Layout;
        }

        return string.Empty;
    }

    /// <summary>
    /// Whether the displays the window manager describes differ from the last time in
    /// a way the bar windows have to answer.
    /// </summary>
    /// <remarks>
    /// Identity and rectangle only. DPI, the friendly name and the active workspace
    /// change without anything about the bar windows needing to, and this decides
    /// whether the loop is asked to look at them.
    /// </remarks>
    public static bool MonitorsDiffer(IReadOnlyList<MonitorInfoDto>? before, IReadOnlyList<MonitorInfoDto> after)
    {
        ArgumentNullException.ThrowIfNull(after);

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

    /// <summary>Reads a JSON string payload as plain text.</summary>
    /// <remarks>
    /// A JSON <c>null</c> becomes an empty string, not the four letters spelling it.
    /// Clearing the binding mode sends exactly that, so leaving the default set put
    /// the word "null" on the bar where the mode had been - and it stayed there,
    /// because an empty value is what hides the widget.
    /// </remarks>
    public static string Unquote(string? json)
    {
        if (json is null) return string.Empty;

        string trimmed = json.Trim();

        if (trimmed.Length == 0 || string.Equals(trimmed, "null", StringComparison.Ordinal))
            return string.Empty;

        return trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"'
            ? trimmed[1..^1]
            : trimmed;
    }
}
