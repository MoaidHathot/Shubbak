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
/// <see cref="DeviceId"/> is the stable key, not the index. Windows renumbers
/// displays on replug, on DisplayPort wake, and on GPU driver restart; keying
/// workspace affinity on an index is why those events scramble other window
/// managers' workspace assignments.
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
    /// Stable hardware identity (device path), surviving replug and renumbering.
    /// </summary>
    public string DeviceId { get; }

    /// <summary>Human-readable name for config and the bar.</summary>
    public string? FriendlyName { get; set; }

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

        if (ReferenceEquals(_active, workspace))
            _active = _workspaces.Count > 0 ? _workspaces[0] : null;

        if (ReferenceEquals(PreviousWorkspace, workspace))
            PreviousWorkspace = null;

        return true;
    }

    public WorkspaceNode? FindWorkspace(string name) =>
        _workspaces.FirstOrDefault(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase));

    public override string ToString() =>
        $"Monitor#{Id}[{FriendlyName ?? DeviceId}, {Bounds}, {_workspaces.Count} workspaces]";
}
