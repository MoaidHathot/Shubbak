using Shubbak.Core.Geometry;

namespace Shubbak.Core.Tree;

/// <summary>
/// A physical display, owning an ordered list of workspaces of which exactly one
/// is active.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately <i>not</i> a <see cref="ContainerNode"/>. A monitor does not divide
/// its area among its workspaces - it shows one and hides the rest - so giving it
/// a layout would be meaningless, and letting the layout engine recurse into it
/// uniformly would be wrong. Modelling that difference in the type system prevents
/// a whole class of "why is my workspace half-width" bug.
/// </para>
/// <para>
/// <see cref="DeviceId"/> is the key, not the index. Windows renumbers displays on
/// replug, on DisplayPort wake, and on GPU driver restart; keying workspace affinity
/// on an index is why those events scramble other window managers' workspace
/// assignments. It is a session-stable key rather than a hardware one; see the
/// property for the difference.
/// </para>
/// </remarks>
public sealed class MonitorNode : Node
{
    private readonly List<WorkspaceNode> _workspaces = [];
    private WorkspaceNode? _active;

    public MonitorNode(string deviceId, Rect bounds, Rect workArea, uint dpi = 96)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);

        DeviceId = deviceId;
        Bounds = bounds;
        WorkArea = workArea;
        Dpi = dpi;
    }

    /// <summary>
    /// The GDI device name, <c>\\.\DISPLAY1</c>: what every monitor API in the program
    /// keys on, and the join key to everything the platform layer learns about the
    /// display afterwards.
    /// </summary>
    /// <remarks>
    /// This used to be described as a hardware device path, which it never was. Windows
    /// hands these names out in enumeration order and reuses them, so the same panel
    /// can be <c>DISPLAY1</c> before undocking and <c>DISPLAY2</c> after. It is stable
    /// for the life of a configuration - which is what the tree needs - and no further.
    /// The identity that survives replug and renumbering is <see cref="DevicePath"/>.
    /// </remarks>
    public string DeviceId { get; }

    /// <summary>
    /// The name the panel reports in its EDID - <c>DELL U3219Q</c> - or null when the
    /// platform layer has not supplied one. Built-in panels usually have none.
    /// </summary>
    /// <remarks>
    /// Not unique. Two of the same model side by side report the same name, so a rule
    /// that wants one of them has to say which by <see cref="DevicePath"/>.
    /// </remarks>
    public string? FriendlyName { get; set; }

    /// <summary>
    /// The connector's device interface path, stable for a given panel on a given port
    /// across replug, renumbering and reboot. Null when the platform layer has not
    /// supplied one, which a remote session's display never does.
    /// </summary>
    public string? DevicePath { get; set; }

    /// <summary>Whether the panel is built into the machine.</summary>
    /// <remarks>
    /// Answered by the connector type, not guessed from the name or the position. Null
    /// until the platform layer has said.
    /// </remarks>
    public bool? IsInternal { get; set; }

    /// <summary>
    /// The names the configuration gives this display: every declared
    /// <c>monitor "name"</c> whose conditions it satisfies.
    /// </summary>
    /// <remarks>
    /// Set by the host, which is the only party that has both the definitions and the
    /// display. Held on the node so that whatever describes a monitor - the state
    /// snapshot, the bar, the palette, the report - can say what the config calls it
    /// without asking the config. Empty for a display nothing names.
    /// </remarks>
    public IReadOnlyList<string> Names { get; set; } = [];

    /// <summary>Whether the configuration calls this display <paramref name="name"/>.</summary>
    public bool IsNamed(string name)
    {
        foreach (string candidate in Names)
            if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    /// <summary>Full monitor rectangle in virtual-desktop coordinates.</summary>
    public Rect Bounds { get; set; }

    /// <summary>
    /// Monitor rectangle minus permanently reserved space (taskbar, docked appbars
    /// including Taj). This, not <see cref="Bounds"/>, is what workspaces tile.
    /// </summary>
    public Rect WorkArea { get; set; }

    /// <summary>Effective DPI; 96 is 100% scaling.</summary>
    public uint Dpi { get; set; }

    /// <summary>Scale factor derived from <see cref="Dpi"/>, e.g. 1.5 at 150%.</summary>
    public double ScaleFactor => Dpi / 96.0;

    public bool IsPrimary { get; set; }

    public IReadOnlyList<WorkspaceNode> Workspaces => _workspaces;

    public override IReadOnlyList<Node> Children => _workspaces;

    /// <summary>
    /// The workspace currently displayed. Setting this does not itself show or hide
    /// anything - the caller applies the resulting diff.
    /// </summary>
    public WorkspaceNode? ActiveWorkspace
    {
        get => _active;
        set
        {
            if (value is not null && !_workspaces.Contains(value))
                throw new InvalidOperationException(
                    $"Workspace {value.Name} does not belong to monitor {DeviceId}.");

            if (ReferenceEquals(_active, value)) return;

            PreviousWorkspace = _active;
            _active = value;
        }
    }

    /// <summary>
    /// The workspace active before the current one, for
    /// <c>focus --recent-workspace</c> and for GlazeWM's
    /// <c>toggle_workspace_on_refocus</c>.
    /// </summary>
    public WorkspaceNode? PreviousWorkspace { get; private set; }

    public void AddWorkspace(WorkspaceNode workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        if (workspace.Parent is not null)
            throw new InvalidOperationException(
                $"Workspace {workspace.Name} is already attached to a monitor.");

        _workspaces.Add(workspace);
        workspace.Parent = this;

        _active ??= workspace;
    }

    public bool RemoveWorkspace(WorkspaceNode workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        if (!_workspaces.Remove(workspace)) return false;

        workspace.Parent = null;

        bool wasActive = ReferenceEquals(_active, workspace);
        bool wasPrevious = ReferenceEquals(PreviousWorkspace, workspace);

        if (wasPrevious) PreviousWorkspace = null;

        if (!wasActive) return true;

        // Falls back to where the user was last, not to whichever workspace happens
        // to sit first in the list. Taking index zero exposed an arbitrary workspace
        // when one was moved to another monitor - so a window the user had not asked
        // for appeared, on a workspace they had not selected.
        //
        // Assigned to the field rather than through the property: the setter records
        // the outgoing workspace as the previous one, and the outgoing workspace here
        // has just been detached from this monitor entirely.
        _active = PreviousWorkspace is { } recent && _workspaces.Contains(recent)
            ? recent
            : _workspaces.Count > 0 ? _workspaces[0] : null;

        // Whatever we came from is either gone or is now current, so there is no
        // meaningful workspace to toggle back to. Leaving a stale one made
        // toggle-workspace-on-refocus jump somewhere the user had never been.
        PreviousWorkspace = null;

        return true;
    }

    public WorkspaceNode? FindWorkspace(string name) =>
        _workspaces.FirstOrDefault(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase));

    public override string ToString() =>
        $"Monitor#{Id}[{FriendlyName ?? DeviceId}, {Bounds}, {_workspaces.Count} workspaces]";
}
