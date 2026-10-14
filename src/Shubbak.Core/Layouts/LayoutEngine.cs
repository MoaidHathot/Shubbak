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
        ArrangeContainer(workspace, area, monitorBounds, visible, layoutOptions);

        // Floating, fullscreen, maximised and minimised windows sit outside the
        // tiling flow but still belong to the workspace, so they are emitted here
        // rather than being silently dropped by the recursion above.
        ArrangeNonTiled(workspace, workArea, monitorBounds, visible);
    }

    private void ArrangeContainer(
        ContainerNode container,
        Rect area,
        Rect monitorBounds,
        bool visible,
        in LayoutOptions options)
    {
        container.Rect = area;

        Node[] tiled = ArrangeTiledChildren(container, area, in options, out Rect[] rects);
        if (tiled.Length == 0) return;

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
                {
                    // A window the application has taken full-screen keeps its slot -
                    // the rectangle above was computed for it and its siblings were
                    // divided around it - and is simply given the monitor instead. So
                    // nothing else moves while it is full-screen, and when it stops
                    // being full-screen it returns to a tile that was never
                    // reallocated. See WindowNode.IsNativeFullscreen.
                    bool native = window.IsNativeFullscreen;
                    Rect rect = native ? monitorBounds : rects[i];

                    window.Rect = rect;
                    _placements.Add(new Placement(
                        window,
                        rect,
                        visible,
                        Raise: native || (overlapping && ReferenceEquals(window, _focused))));
                    break;
                }

                case ContainerNode child:
                    ArrangeContainer(child, rects[i], monitorBounds, visible, in options);
                    break;
            }
        }
    }

    /// <summary>
    /// Divides <paramref name="area"/> among the tiled children of one container, as
    /// an arrange pass would, without writing to any node.
    /// </summary>
    /// <returns>
    /// The tiled children in order; <paramref name="rects"/> receives one rectangle
    /// per entry.
    /// </returns>
    /// <remarks>
    /// Only tiled children participate; a floating window must not consume a slot,
    /// or removing it from the flow would leave a hole. The layout contract is "one
    /// rect per child", so a container holding a mix of tiled and floating children
    /// is arranged through a temporary view containing just the tiled ones, which
    /// keeps every <see cref="ILayout"/> implementation free of float-awareness.
    /// </remarks>
    internal static Node[] ArrangeTiledChildren(
        ContainerNode container, Rect area, in LayoutOptions options, out Rect[] rects)
    {
        Node[] tiled = [.. container.Children.Where(IsTiled)];
        rects = new Rect[tiled.Length];

        if (tiled.Length == 0) return tiled;

        if (tiled.Length == container.Children.Count)
        {
            container.Layout.Arrange(container, area, in options, rects);
        }
        else
        {
            using var view = TiledView.Create(container, tiled);
            view.Container.Layout.Arrange(view.Container, area, in options, rects);
        }

        return tiled;
    }

    /// <summary>
    /// The share that gives <paramref name="child"/> an extent of
    /// <paramref name="extent"/> pixels along <paramref name="axis"/>, once
    /// <paramref name="container"/> is arranged again into its current rectangle.
    /// </summary>
    /// <returns>
    /// The ratio to pass to <see cref="ContainerNode.SetChildRatio"/>, or
    /// <see langword="null"/> when the child's share has no say over that axis.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Found by measuring rather than by formula. A split gives a child
    /// <c>ratio × width</c>, and dragging a border used to assume every layout did,
    /// but the spiral gives it <c>own ÷ (own + average of the rest)</c> and the
    /// master layouts something else again - so a drag of twenty pixels on the first
    /// of three spiral windows moved its edge by three hundred, and each layout's
    /// inverse would have to be written and kept in step with its arrange. Arranging
    /// the container at a candidate share and reading the child's extent back asks
    /// the layout itself, with its gaps, its clamps and its rounding included, and
    /// holds for a layout that does not exist yet.
    /// </para>
    /// <para>
    /// A bisection, because every layout gives a child more room for more share and
    /// never less, which is the one property the search needs. Pixels are integers,
    /// so the answer is the smallest share that reaches the requested extent; a
    /// request beyond what any share can give is clamped to the end it is nearer,
    /// which is what a border dragged past its limit should do.
    /// </para>
    /// <para>
    /// Null is an answer, not a failure. In a spiral of two the first split is
    /// left/right, so both windows have the whole height and no ratio changes that;
    /// a height asked of such a window would otherwise be read as "the whole
    /// container and seven pixels more", a share of more than one, clamped to the
    /// most a child may have - and that one number sizes a spiral window in both
    /// directions, so the window took almost the whole screen from a drag that had
    /// shrunk it. The caller leaves that axis alone instead.
    /// </para>
    /// <para>
    /// Measuring means arranging, and arranging reads the tree's ratios, so the
    /// container's ratios are set to each candidate and restored at the end. The
    /// node rectangles are not touched: the pass that follows writes them.
    /// </para>
    /// </remarks>
    public static double? RatioForExtent(
        ContainerNode container, Node child, Axis axis, int extent, in LayoutOptions options)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(child);

        if (container.Count < 2 || !ReferenceEquals(child.Parent, container)) return null;
        if (container.Rect.Extent(axis) <= 0) return null;

        IReadOnlyList<Node> children = container.Children;
        double[] saved = new double[children.Count];
        for (int i = 0; i < saved.Length; i++) saved[i] = children[i].SizeRatio;

        try
        {
            // The same bounds SetChildRatio clamps to, so every share measured here is
            // one that can actually be set.
            double low = Node.MinSizeRatio;
            double high = 1.0 - (Node.MinSizeRatio * (children.Count - 1));

            int atLow = Measure(container, child, axis, low, saved, in options);
            int atHigh = Measure(container, child, axis, high, saved, in options);

            if (atLow == atHigh) return null;
            if (extent <= atLow) return low;
            if (extent >= atHigh) return high;

            // Fifty halvings take the interval well below any difference a ratio can
            // make to a pixel; the loop ends sooner once it has.
            for (int i = 0; i < 50 && high - low > 1e-12; i++)
            {
                double mid = (low + high) / 2;
                if (Measure(container, child, axis, mid, saved, in options) < extent) low = mid;
                else high = mid;
            }

            return high;
        }
        finally
        {
            container.SetRatios(saved);
        }
    }

    /// <summary>
    /// The extent <paramref name="child"/> would have along <paramref name="axis"/>
    /// with a share of <paramref name="ratio"/>, everything else as in
    /// <paramref name="saved"/>.
    /// </summary>
    private static int Measure(
        ContainerNode container, Node child, Axis axis, double ratio,
        double[] saved, in LayoutOptions options)
    {
        // From the saved ratios each time rather than from the last candidate, so the
        // siblings' proportions are those of the tree and not of a previous guess.
        container.SetRatios(saved);
        container.SetChildRatio(child, ratio);

        Node[] tiled = ArrangeTiledChildren(container, container.Rect, in options, out Rect[] rects);
        int index = Array.IndexOf(tiled, child);

        return index < 0 ? 0 : rects[index].Extent(axis);
    }

    /// <summary>
    /// A floating rectangle brought back onto its workspace's display when none of it
    /// is there any more.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A floating window's position is the user's, and is left alone while any of it
    /// is on the display its workspace lives on - a dialog straddling two screens is a
    /// choice. It stops being a choice when the display it was on is unplugged, or
    /// rearranged in Settings, or its workspace is moved to a smaller one: the saved
    /// rectangle then describes a place that no longer exists, and the window was
    /// placed there anyway, off every screen, with the tree saying it was visible.
    /// </para>
    /// <para>
    /// Only a rectangle wholly outside the work area is moved, and it is moved rather
    /// than recentred - slid in by the least that puts it on screen, then shrunk if it
    /// is larger than the area - so a window that was at the top left of the old
    /// display is at the top left of the new one, which is where its owner will look.
    /// </para>
    /// </remarks>
    public static Rect ReHomed(Rect floating, Rect workArea)
    {
        if (floating.IsEmpty || workArea.IsEmpty) return floating;
        if (floating.IntersectsWith(workArea)) return floating;

        int width = Math.Min(floating.Width, workArea.Width);
        int height = Math.Min(floating.Height, workArea.Height);

        int x = Math.Clamp(floating.X, workArea.X, workArea.Right - width);
        int y = Math.Clamp(floating.Y, workArea.Y, workArea.Bottom - height);

        return new Rect(x, y, width, height);
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
                    //
                    // Unless the application has taken it full-screen, which it is as
                    // free to do from a floating window as from a tiled one. Its
                    // remembered position is left untouched, so it comes back to
                    // exactly where the user had put it.
                    Rect floating = window.IsNativeFullscreen
                        ? monitorBounds
                        : ReHomed(window.FloatingRect ?? window.Rect, workArea);

                    if (!window.IsNativeFullscreen && window.FloatingRect is { } remembered && remembered != floating)
                        window.FloatingRect = floating;

                    window.Rect = floating;
                    _placements.Add(new Placement(
                        window, floating, visible, Raise: window.IsNativeFullscreen));
                    break;
                }

                case WindowState.Minimised:
                    // Deliberately no placement. A minimised window is already off
                    // screen, and the only thing a placement would do is conceal it -
                    // which on Windows means cloaking it, which is the mechanism
                    // virtual desktops use. The shell then reads the window as living
                    // on another desktop, and clicking its taskbar button stops
                    // restoring it: the button is there, the click does nothing, and
                    // the only way back is Task View.
                    //
                    // Reported exactly that way, from a window minimised by accident.
                    // Nothing is lost by leaving it alone - Windows is already showing
                    // it in the taskbar and not on the desktop, which is the whole of
                    // what minimised means.
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
