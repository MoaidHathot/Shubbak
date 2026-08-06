using Shubbak.Core.Geometry;
using Shubbak.Core.Tree;

namespace Shubbak.Core.Layouts;

/// <summary>
/// The rectangle computed for one window during an arrange pass.
/// </summary>
/// <param name="Window">The window the rectangle belongs to.</param>
/// <param name="Rect">Its target rectangle, in virtual-desktop coordinates.</param>
/// <param name="Visible">
/// False for windows on an inactive workspace. Such windows are still arranged, so
/// that switching to the workspace shows a correct layout immediately rather than
/// a frame of garbage.
/// </param>
/// <param name="Raise">
/// True for a window that has to sit above the windows it overlaps.
/// <para>
/// Tiles do not overlap, so for most windows this is meaningless and stacking can be
/// left exactly as the user arranged it. Three arrangements do overlap by design -
/// fullscreen, maximised, and monocle, where every window is given the whole area -
/// and for those, stacking is the only thing that decides what is seen. Nothing
/// raised them, so a fullscreen window could sit behind the tile it was covering and
/// a monocle container showed whichever window happened to be on top.
/// </para>
/// </param>
public readonly record struct Placement(
    WindowNode Window, Rect Rect, bool Visible, bool Raise = false);

/// <summary>
/// Settings that apply to a whole arrange pass rather than to one container.
/// </summary>
/// <param name="OuterGap">
/// Spacing between a workspace and its monitor's work area. Applied once, by the
/// engine, before any layout runs - which is why layouts only have to think about
/// the gaps between siblings.
/// </param>
/// <param name="InnerGap">Spacing between adjacent siblings.</param>
/// <param name="MinimumTileExtent">Smallest extent a tile may be given.</param>
/// <param name="Focused">
/// The focused window, when there is one. Used only to decide which window to raise
/// inside a layout whose rectangles overlap; tiling layouts never consult it.
/// </param>
public readonly record struct ArrangeOptions(
    Gaps OuterGap = default,
    int InnerGap = 0,
    int MinimumTileExtent = 24,
    WindowNode? Focused = null)
{
    public static ArrangeOptions Default => new();

    internal LayoutOptions ToLayoutOptions() => new(InnerGap, MinimumTileExtent);
}

/// <summary>
/// Walks the tree and turns it into a flat list of window rectangles.
/// </summary>
/// <remarks>
/// <para>
/// The engine owns recursion and outer gaps; individual <see cref="ILayout"/>
/// implementations only ever divide one rectangle among direct children. That
/// division of labour is what keeps layouts composable and independently testable.
/// </para>
/// <para>
/// This class contains no Win32 and no timing. It computes <i>target</i>
/// rectangles; the animation engine owns interpolation towards them, and the
/// platform layer owns committing them in a single <c>DeferWindowPos</c>
/// transaction (docs/adr/0001-language-choice.md, constraint 3).
/// </para>
/// </remarks>
public sealed class LayoutEngine
{
    private readonly List<Placement> _placements = [];

    /// <summary>
    /// The focused window for the pass in progress.
    /// </summary>
    /// <remarks>
    /// Held for the duration of one arrange rather than threaded through every
    /// recursion, because only one thing consults it: a container whose layout
    /// overlaps its children, which has to know which of them to raise.
    /// </remarks>
    private WindowNode? _focused;

    /// <summary>Arranges every workspace on every monitor.</summary>
    public IReadOnlyList<Placement> Arrange(RootNode root, ArrangeOptions options)
    {
        ArgumentNullException.ThrowIfNull(root);

        _placements.Clear();
        _focused = options.Focused;

        foreach (MonitorNode monitor in root.Monitors)
            ArrangeMonitorInto(monitor, options);

        return _placements;
    }

    /// <summary>Arranges every workspace on one monitor.</summary>
    public IReadOnlyList<Placement> Arrange(MonitorNode monitor, ArrangeOptions options)
    {
        ArgumentNullException.ThrowIfNull(monitor);

        _placements.Clear();
        _focused = options.Focused;
        ArrangeMonitorInto(monitor, options);
        return _placements;
    }

    /// <summary>Arranges a single workspace.</summary>
    public IReadOnlyList<Placement> Arrange(WorkspaceNode workspace, ArrangeOptions options)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        _placements.Clear();
        _focused = options.Focused;

        MonitorNode? monitor = workspace.Monitor;
        Rect area = monitor?.WorkArea ?? workspace.Rect;
        ArrangeWorkspaceInto(
            workspace, area, monitor?.Bounds ?? area, visible: workspace.IsActive, options);

        return _placements;
    }

    private void ArrangeMonitorInto(MonitorNode monitor, ArrangeOptions options)
    {
        foreach (WorkspaceNode workspace in monitor.Workspaces)
        {
            bool visible = ReferenceEquals(workspace, monitor.ActiveWorkspace);
            ArrangeWorkspaceInto(workspace, monitor.WorkArea, monitor.Bounds, visible, options);
        }
    }

    private void ArrangeWorkspaceInto(
        WorkspaceNode workspace,
        Rect workArea,
        Rect monitorBounds,
        bool visible,
        ArrangeOptions options)
    {
        Rect area = workArea.Deflate(options.OuterGap);
        workspace.Rect = area;

        LayoutOptions layoutOptions = options.ToLayoutOptions();
        ArrangeContainer(workspace, area, visible, layoutOptions);

        // Floating, fullscreen, maximised and minimised windows sit outside the
        // tiling flow but still belong to the workspace, so they are emitted here
        // rather than being silently dropped by the recursion above.
        ArrangeNonTiled(workspace, workArea, monitorBounds, visible);
    }

    private void ArrangeContainer(
        ContainerNode container, Rect area, bool visible, in LayoutOptions options)
    {
        container.Rect = area;

        // Only tiled children participate; a floating window must not consume a
        // slot, or removing it from the flow would leave a hole.
        Node[] tiled = [.. container.Children.Where(IsTiled)];
        if (tiled.Length == 0) return;

        Rect[] rects = new Rect[tiled.Length];

        if (tiled.Length == container.Children.Count)
        {
            container.Layout.Arrange(container, area, in options, rects);
        }
        else
        {
            // The layout contract is "one rect per child", so a container holding a
            // mix of tiled and floating children is arranged through a temporary
            // view containing just the tiled ones. This keeps every ILayout
            // implementation free of float-awareness.
            using var view = TiledView.Create(container, tiled);
            view.Container.Layout.Arrange(view.Container, area, in options, rects);
        }

        // In a layout whose rectangles overlap, stacking is the only thing that
        // decides what the user sees, so the focused window has to be raised. Where
        // rectangles are disjoint - every tiling layout - stacking is left alone,
        // which keeps windows in whatever order the user put them.
        bool overlapping = container.Layout.Overlaps;

        for (int i = 0; i < tiled.Length; i++)
        {
            switch (tiled[i])
            {
                case WindowNode window:
                    window.Rect = rects[i];
                    _placements.Add(new Placement(
                        window,
                        rects[i],
                        visible,
                        Raise: overlapping && ReferenceEquals(window, _focused)));
                    break;

                case ContainerNode child:
                    ArrangeContainer(child, rects[i], visible, in options);
                    break;
            }
        }
    }

    /// <summary>
    /// Emits placements for the windows the tiling recursion skipped.
    /// </summary>
    /// <param name="workspace">The workspace being arranged.</param>
    /// <param name="workArea">
    /// The monitor's work area, <i>before</i> outer gaps. Fullscreen and maximised
    /// windows use this rather than the tiling area: gaps are a tiling concept, and
    /// a fullscreen window that honoured them would not be fullscreen.
    /// </param>
    /// <param name="monitorBounds">
    /// The whole monitor, including the strip the bar and taskbar reserved. Only
    /// <see cref="WindowState.MonitorFullscreen"/> uses it, and that is the single
    /// thing separating it from <see cref="WindowState.Fullscreen"/>.
    /// </param>
    /// <param name="visible">Whether this workspace is the active one.</param>
    private void ArrangeNonTiled(
        WorkspaceNode workspace, Rect workArea, Rect monitorBounds, bool visible)
    {
        foreach (WindowNode window in workspace.DescendantWindows())
        {
            switch (window.State)
            {
                case WindowState.Fullscreen:
                case WindowState.Maximised:
                    window.Rect = workArea;
                    _placements.Add(new Placement(window, workArea, visible, Raise: true));
                    break;

                case WindowState.MonitorFullscreen:
                    window.Rect = monitorBounds;
                    _placements.Add(new Placement(window, monitorBounds, visible, Raise: true));
                    break;

                case WindowState.Floating:
                {
                    // Position is the user's, not ours; the engine only decides
                    // visibility. Falling back to the last computed rect keeps a
                    // window that has just been un-tiled exactly where it was.
                    Rect floating = window.FloatingRect ?? window.Rect;
                    window.Rect = floating;
                    _placements.Add(new Placement(window, floating, visible));
                    break;
                }

                case WindowState.Minimised:
                    _placements.Add(new Placement(window, window.Rect, Visible: false));
                    break;

                case WindowState.Tiling:
                default:
                    // Already emitted by ArrangeContainer.
                    break;
            }
        }
    }

    private static bool IsTiled(Node node) => node.ParticipatesInTiling;

    /// <summary>
    /// A scratch container holding only the tiled children of a real one, so that
    /// layouts can keep their simple "one rect per child" contract.
    /// </summary>
    /// <remarks>
    /// The children are moved rather than copied, because <see cref="Node.SizeRatio"/>
    /// lives on the node and layouts read it. <see cref="Dispose"/> restores the
    /// original parentage and ratios exactly, so the tree is unchanged afterwards.
    /// </remarks>
    private readonly struct TiledView : IDisposable
    {
        private readonly ContainerNode _original;
        private readonly Node[] _tiled;
        private readonly double[] _ratios;

        public ContainerNode Container { get; }

        private TiledView(ContainerNode original, ContainerNode view, Node[] tiled, double[] ratios)
        {
            _original = original;
            _tiled = tiled;
            _ratios = ratios;
            Container = view;
        }

        public static TiledView Create(ContainerNode original, Node[] tiled)
        {
            double[] ratios = new double[tiled.Length];
            for (int i = 0; i < tiled.Length; i++) ratios[i] = tiled[i].SizeRatio;

            var view = new ContainerNode(original.Layout);

            foreach (Node node in tiled)
            {
                node.Parent = null;
                view.Add(node);
            }

            // Insert() renormalises as it goes; restore the caller's proportions so
            // the arrangement reflects the real tree, then let the view normalise
            // across the tiled subset only.
            double total = 0;
            foreach (double r in ratios) total += r;

            if (total > double.Epsilon)
                for (int i = 0; i < tiled.Length; i++)
                    tiled[i].SizeRatio = ratios[i] / total;

            return new TiledView(original, view, tiled, ratios);
        }

        public void Dispose()
        {
            foreach (Node node in _tiled) node.Parent = _original;
            for (int i = 0; i < _tiled.Length; i++) _tiled[i].SizeRatio = _ratios[i];
        }
    }
}
