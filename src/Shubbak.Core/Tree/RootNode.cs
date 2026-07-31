using Shubbak.Core.Geometry;

namespace Shubbak.Core.Tree;

/// <summary>
/// The root of the window tree: the virtual desktop, owning every monitor.
/// </summary>
/// <remarks>
/// Like <see cref="MonitorNode"/> this is not a <see cref="ContainerNode"/> -
/// monitors are positioned by the operating system, not tiled by us.
/// </remarks>
public sealed class RootNode : Node
{
    private readonly List<MonitorNode> _monitors = [];

    public IReadOnlyList<MonitorNode> Monitors => _monitors;

    public override IReadOnlyList<Node> Children => _monitors;

    /// <summary>The union of every monitor's bounds.</summary>
    public Rect VirtualDesktop
    {
        get
        {
            Rect result = Rect.Empty;
            foreach (MonitorNode m in _monitors) result = result.Union(m.Bounds);
            return result;
        }
    }

    public MonitorNode? PrimaryMonitor =>
        _monitors.FirstOrDefault(m => m.IsPrimary) ?? _monitors.FirstOrDefault();

    public void AddMonitor(MonitorNode monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);

        if (monitor.Parent is not null)
            throw new InvalidOperationException($"Monitor {monitor.DeviceId} is already attached.");

        _monitors.Add(monitor);
        monitor.Parent = this;
    }

    public bool RemoveMonitor(MonitorNode monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);

        if (!_monitors.Remove(monitor)) return false;

        monitor.Parent = null;
        return true;
    }

    public MonitorNode? FindMonitor(string deviceId) =>
        _monitors.FirstOrDefault(m => string.Equals(m.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The monitor whose bounds contain the point, or the nearest one by centre
    /// distance if the point lies outside every monitor.
    /// </summary>
    /// <remarks>
    /// Falling back to nearest rather than returning null matters: a window
    /// restored from a session with different displays attached can easily sit in
    /// dead space between or beyond monitors, and it still has to land somewhere.
    /// </remarks>
    public MonitorNode? MonitorAt(int x, int y)
    {
        foreach (MonitorNode m in _monitors)
            if (m.Bounds.Contains(x, y)) return m;

        MonitorNode? nearest = null;
        long best = long.MaxValue;

        foreach (MonitorNode m in _monitors)
        {
            long dx = m.Bounds.CenterX - x;
            long dy = m.Bounds.CenterY - y;
            long distanceSquared = (dx * dx) + (dy * dy);
            if (distanceSquared < best)
            {
                best = distanceSquared;
                nearest = m;
            }
        }

        return nearest;
    }

    /// <summary>The monitor with the largest overlap with <paramref name="rect"/>.</summary>
    public MonitorNode? MonitorFor(Rect rect)
    {
        MonitorNode? best = null;
        long bestArea = 0;

        foreach (MonitorNode m in _monitors)
        {
            long area = m.Bounds.Intersect(rect).Area;
            if (area > bestArea)
            {
                bestArea = area;
                best = m;
            }
        }

        return best ?? MonitorAt(rect.CenterX, rect.CenterY);
    }

    /// <summary>
    /// The nearest monitor in <paramref name="direction"/> from
    /// <paramref name="origin"/>, or null when there is none.
    /// </summary>
    /// <remarks>
    /// Candidates must lie strictly beyond the origin's corresponding edge, so a
    /// monitor that merely overlaps is not considered "to the left of" it. Among
    /// candidates the closest along the travel axis wins, with cross-axis centre
    /// distance breaking ties - which is what makes stacked displays behave
    /// predictably.
    /// </remarks>
    public MonitorNode? MonitorInDirection(MonitorNode origin, Direction direction)
    {
        ArgumentNullException.ThrowIfNull(origin);

        Rect from = origin.Bounds;
        MonitorNode? best = null;
        (long primary, long secondary) bestScore = (long.MaxValue, long.MaxValue);

        foreach (MonitorNode candidate in _monitors)
        {
            if (ReferenceEquals(candidate, origin)) continue;

            Rect to = candidate.Bounds;

            bool qualifies = direction switch
            {
                Direction.Left => to.CenterX < from.Left,
                Direction.Right => to.CenterX >= from.Right,
                Direction.Up => to.CenterY < from.Top,
                _ => to.CenterY >= from.Bottom,
            };

            if (!qualifies) continue;

            long along = direction.Axis() == Axis.Horizontal
                ? Math.Abs(to.CenterX - from.CenterX)
                : Math.Abs(to.CenterY - from.CenterY);

            long across = direction.Axis() == Axis.Horizontal
                ? Math.Abs(to.CenterY - from.CenterY)
                : Math.Abs(to.CenterX - from.CenterX);

            if (along < bestScore.primary || (along == bestScore.primary && across < bestScore.secondary))
            {
                bestScore = (along, across);
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>Every workspace across every monitor.</summary>
    public IEnumerable<WorkspaceNode> AllWorkspaces() => _monitors.SelectMany(m => m.Workspaces);

    /// <summary>Finds a workspace by name across all monitors.</summary>
    public WorkspaceNode? FindWorkspace(string name) =>
        AllWorkspaces().FirstOrDefault(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Finds the node for a native window handle.</summary>
    public WindowNode? FindWindow(long handle) =>
        DescendantWindows().FirstOrDefault(w => w.Handle == handle);

    public override string ToString() => $"Root#{Id}[{_monitors.Count} monitors]";
}
