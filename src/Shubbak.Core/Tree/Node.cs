using Shubbak.Core.Geometry;

namespace Shubbak.Core.Tree;

/// <summary>
/// Base class for every node in the window tree.
/// </summary>
/// <remarks>
/// <para>
/// The tree is the single source of truth for window arrangement. Layouts read
/// it and compute rectangles; they never own geometry themselves. This is what
/// lets the whole layout system be tested without Win32 (invariant 5 of
/// docs/adr/0001-language-choice.md).
/// </para>
/// <para>
/// The shape is i3/sway's, not Hyprland's: containers nest arbitrarily, and a
/// container's <see cref="ContainerNode.Layout"/> applies only to its own direct
/// children. That is what makes "fibonacci in this region, columns in that one"
/// fall out for free rather than needing a special case.
/// </para>
/// </remarks>
public abstract class Node
{
    private double _sizeRatio = 1.0;

    /// <summary>
    /// Stable identity, unique within a process. Used by IPC, the bar, and
    /// per-(workspace, window) state tables. Never reused.
    /// </summary>
    public NodeId Id { get; } = NodeId.Next();

    /// <summary>
    /// The node this one hangs from, or <see langword="null"/> for the root and for
    /// detached nodes.
    /// </summary>
    /// <remarks>
    /// Typed as <see cref="Node"/> rather than <see cref="ContainerNode"/> so that
    /// ancestry is uniform all the way up: Root -> Monitor -> Workspace -> Container
    /// -> Window. A monitor is deliberately not a container (it shows one workspace
    /// rather than tiling them), and root is not either; if <c>Parent</c> were typed
    /// as <see cref="ContainerNode"/>, both would need their own parallel
    /// parent/ancestor plumbing. Use <see cref="ParentContainer"/> where the tiling
    /// parent specifically is required.
    /// </remarks>
    public Node? Parent { get; internal set; }

    /// <summary>
    /// The parent when it is a tiling container, otherwise <see langword="null"/>.
    /// Null for a workspace (whose parent is a monitor) and for a monitor.
    /// </summary>
    public ContainerNode? ParentContainer => Parent as ContainerNode;

    /// <summary>
    /// This node's share of its parent's main axis, as a fraction in (0, 1].
    /// </summary>
    /// <remarks>
    /// Siblings' ratios are kept normalised to sum to 1.0 by
    /// <see cref="ContainerNode"/>. Storing a fraction rather than absolute pixels
    /// is what makes a layout survive a resolution change or a move to a different
    /// monitor without any rescaling pass.
    /// </remarks>
    public double SizeRatio
    {
        get => _sizeRatio;
        internal set
        {
            if (!double.IsFinite(value))
                throw new ArgumentOutOfRangeException(nameof(value), value, "Size ratio must be finite.");

            _sizeRatio = Math.Clamp(value, MinSizeRatio, 1.0);
        }
    }

    /// <summary>
    /// Floor for <see cref="SizeRatio"/>. Prevents a container from collapsing a
    /// child to zero width, which would make it unfocusable and unrecoverable by
    /// mouse.
    /// </summary>
    public const double MinSizeRatio = 0.01;

    /// <summary>
    /// The most recently computed rectangle for this node.
    /// </summary>
    /// <remarks>
    /// Cached output of the layout engine, not input. The animation engine reads
    /// this as the *target*; it owns interpolation towards it (ADR 0001, S2).
    /// </remarks>
    public Rect Rect { get; internal set; }

    /// <summary>Direct children, empty for leaves.</summary>
    public virtual IReadOnlyList<Node> Children => [];

    /// <summary>True when this node can hold children.</summary>
    public bool IsContainer => this is ContainerNode;

    /// <summary>Walks up to the nearest ancestor of type <typeparamref name="T"/>.</summary>
    public T? Ancestor<T>() where T : Node
    {
        for (Node? n = Parent; n is not null; n = n.Parent)
            if (n is T match) return match;
        return null;
    }

    /// <summary>The workspace this node lives in, if any.</summary>
    public WorkspaceNode? Workspace => this as WorkspaceNode ?? Ancestor<WorkspaceNode>();

    /// <summary>The monitor this node lives on, if any.</summary>
    public MonitorNode? Monitor => this as MonitorNode ?? Ancestor<MonitorNode>();

    /// <summary>Index within <see cref="ParentContainer"/>, or -1 when detached.</summary>
    public int IndexInParent => ParentContainer?.IndexOf(this) ?? -1;

    /// <summary>The sibling before this one, or null.</summary>
    public Node? PreviousSibling
    {
        get
        {
            ContainerNode? parent = ParentContainer;
            if (parent is null) return null;
            int i = parent.IndexOf(this);
            return i > 0 ? parent.Children[i - 1] : null;
        }
    }

    /// <summary>The sibling after this one, or null.</summary>
    public Node? NextSibling
    {
        get
        {
            ContainerNode? parent = ParentContainer;
            if (parent is null) return null;
            int i = parent.IndexOf(this);
            return i >= 0 && i < parent.Children.Count - 1 ? parent.Children[i + 1] : null;
        }
    }

    /// <summary>
    /// This node and all descendants, depth-first, parents before children.
    /// </summary>
    public IEnumerable<Node> SelfAndDescendants()
    {
        yield return this;
        foreach (Node child in Children)
            foreach (Node d in child.SelfAndDescendants())
                yield return d;
    }

    /// <summary>All descendant windows, in left-to-right tree order.</summary>
    public IEnumerable<WindowNode> DescendantWindows() =>
        SelfAndDescendants().OfType<WindowNode>();

    /// <summary>
    /// True when this node participates in tiling: either it is a tiled window, or
    /// it is a container with at least one tiled window somewhere inside it.
    /// </summary>
    /// <remarks>
    /// The single predicate used by layout, navigation and arrangement to decide
    /// whether a node occupies a slot. Floating, fullscreen and minimised windows
    /// are outside the tiling flow, so a container holding only those must not
    /// consume space or absorb focus - it would appear as an unreachable hole.
    /// </remarks>
    public bool ParticipatesInTiling => this switch
    {
        WindowNode window => window.IsTiled,
        ContainerNode container => container.DescendantWindows().Any(w => w.IsTiled),
        _ => false,
    };

    /// <summary>This node and all ancestors, innermost first.</summary>
    public IEnumerable<Node> SelfAndAncestors()
    {
        for (Node? n = this; n is not null; n = n.Parent)
            yield return n;
    }

    /// <summary>True when <paramref name="other"/> is this node or a descendant of it.</summary>
    public bool Contains(Node other)
    {
        for (Node? n = other; n is not null; n = n.Parent)
            if (ReferenceEquals(n, this)) return true;
        return false;
    }
}
