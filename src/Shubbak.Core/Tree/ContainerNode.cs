using Shubbak.Core.Layouts;

namespace Shubbak.Core.Tree;

/// <summary>
/// A node that holds an ordered list of children and a <see cref="ILayout"/>
/// describing how they divide its rectangle.
/// </summary>
/// <remarks>
/// <para>
/// Layout lives on the <i>container</i>, never on the workspace. That single
/// decision is what makes the following fall out without special cases:
/// </para>
/// <list type="bullet">
///   <item>"set the layout for this workspace" is just setting it on the
///   workspace's own container, since <see cref="WorkspaceNode"/> is one;</item>
///   <item>nesting a fibonacci region inside a columns region requires no new
///   concept;</item>
///   <item>manual split (the GlazeWM/i3 default) is not a special mode - it is
///   simply a layout that never restructures the tree on insert.</item>
/// </list>
/// <para>
/// Child <see cref="Node.SizeRatio"/> values are maintained normalised: they
/// always sum to 1.0 (within floating point tolerance) whenever the container is
/// non-empty. All mutation goes through this class so that invariant cannot be
/// broken from outside.
/// </para>
/// </remarks>
public class ContainerNode : Node
{
    private readonly List<Node> _children = [];

    public ContainerNode(ILayout? layout = null) => Layout = layout ?? LayoutRegistry.Default;

    /// <summary>How this container divides its rectangle among its children.</summary>
    public ILayout Layout { get; set; }

    public override IReadOnlyList<Node> Children => _children;

    public int Count => _children.Count;

    public bool IsEmpty => _children.Count == 0;

    public int IndexOf(Node child) => _children.IndexOf(child);

    // ---- mutation ----------------------------------------------------------

    /// <summary>Appends <paramref name="child"/> as the last child.</summary>
    public void Add(Node child) => Insert(_children.Count, child);

    /// <summary>
    /// Inserts <paramref name="child"/> at <paramref name="index"/>, giving it an
    /// equal share (1/n) and shrinking existing siblings proportionally so their
    /// sizes relative to each other are preserved.
    /// </summary>
    public void Insert(int index, Node child)
    {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)index, (uint)_children.Count, nameof(index));

        if (child.Parent is not null)
            throw new InvalidOperationException(
                $"Node {child.Id} is already attached to {child.Parent.Id}. Detach it first.");

        if (child.Contains(this))
            throw new InvalidOperationException(
                $"Cannot insert node {child.Id} into its own descendant {Id}; that would create a cycle.");

        int newCount = _children.Count + 1;
        double share = 1.0 / newCount;

        // Scale existing children down to leave exactly `share` for the newcomer.
        if (_children.Count > 0)
        {
            double scale = 1.0 - share;
            foreach (Node existing in _children)
                existing.SizeRatio *= scale;
        }

        child.SizeRatio = share;
        child.Parent = this;
        _children.Insert(index, child);

        Normalise();
    }

    /// <summary>
    /// Detaches <paramref name="child"/>, redistributing its share among the
    /// remaining siblings in proportion to their current sizes.
    /// </summary>
    /// <returns>
    /// The index the child occupied, or -1 if it was not a child of this container.
    /// </returns>
    public int Remove(Node child)
    {
        ArgumentNullException.ThrowIfNull(child);

        int index = _children.IndexOf(child);
        if (index < 0) return -1;

        _children.RemoveAt(index);
        child.Parent = null;
        child.SizeRatio = 1.0;

        Normalise();
        return index;
    }

    /// <summary>
    /// Replaces <paramref name="existing"/> with <paramref name="replacement"/>,
    /// which inherits its position and size.
    /// </summary>
    /// <remarks>
    /// This is the primitive behind "wrap a window in a new container" - the
    /// operation that creates nesting. Doing it as one step rather than
    /// remove-then-insert matters, because remove/insert would renormalise twice
    /// and silently resize every sibling.
    /// </remarks>
    public void Replace(Node existing, Node replacement)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(replacement);

        int index = _children.IndexOf(existing);
        if (index < 0)
            throw new InvalidOperationException($"Node {existing.Id} is not a child of container {Id}.");

        if (replacement.Parent is not null && !ReferenceEquals(replacement.Parent, this))
            throw new InvalidOperationException(
                $"Node {replacement.Id} is already attached to {replacement.Parent.Id}.");

        if (replacement.Contains(this))
            throw new InvalidOperationException(
                $"Cannot replace with node {replacement.Id}; it is an ancestor of container {Id}.");

        replacement.SizeRatio = existing.SizeRatio;
        replacement.Parent = this;
        _children[index] = replacement;

        existing.Parent = null;
    }

    /// <summary>
    /// Moves an existing child to a new index within this container, preserving
    /// every child's size. The window "slides" past its neighbour rather than
    /// swapping geometry with it.
    /// </summary>
    public void MoveChild(Node child, int newIndex)
    {
        ArgumentNullException.ThrowIfNull(child);

        int current = _children.IndexOf(child);
        if (current < 0)
            throw new InvalidOperationException($"Node {child.Id} is not a child of container {Id}.");

        newIndex = Math.Clamp(newIndex, 0, _children.Count - 1);
        if (newIndex == current) return;

        _children.RemoveAt(current);
        _children.Insert(newIndex, child);
    }

    /// <summary>
    /// Exchanges the positions of two children, keeping each one's size attached to
    /// its <i>slot</i> rather than to the node.
    /// </summary>
    /// <remarks>
    /// Sizes stay with the slot because that is what a swap means to a user: the
    /// two tiles trade places on screen without either changing dimensions.
    /// </remarks>
    public void SwapChildren(Node a, Node b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        int ia = _children.IndexOf(a);
        int ib = _children.IndexOf(b);

        if (ia < 0 || ib < 0)
            throw new InvalidOperationException($"Both nodes must be children of container {Id}.");

        if (ia == ib) return;

        (_children[ia], _children[ib]) = (_children[ib], _children[ia]);
        (a.SizeRatio, b.SizeRatio) = (b.SizeRatio, a.SizeRatio);
    }

    /// <summary>Detaches every child and returns them in order.</summary>
    public IReadOnlyList<Node> DetachAll()
    {
        Node[] detached = [.. _children];
        foreach (Node child in detached)
        {
            child.Parent = null;
            child.SizeRatio = 1.0;
        }
        _children.Clear();
        return detached;
    }

    // ---- sizing ------------------------------------------------------------

    /// <summary>
    /// Sets a child's ratio to <paramref name="ratio"/>, taking the difference from
    /// (or giving it to) the remaining siblings in proportion to their sizes.
    /// </summary>
    /// <remarks>
    /// Adjusting only the siblings - rather than renormalising everything - is what
    /// makes interactive resize feel stable: dragging one border must not perturb
    /// tiles elsewhere in the container.
    /// </remarks>
    public void SetChildRatio(Node child, double ratio)
    {
        ArgumentNullException.ThrowIfNull(child);

        if (!_children.Contains(child))
            throw new InvalidOperationException($"Node {child.Id} is not a child of container {Id}.");

        if (_children.Count == 1)
        {
            child.SizeRatio = 1.0;
            return;
        }

        // Leave room for every sibling to keep at least its minimum.
        double minForOthers = MinSizeRatio * (_children.Count - 1);
        double target = Math.Clamp(ratio, MinSizeRatio, 1.0 - minForOthers);

        double othersTotal = 0;
        foreach (Node sibling in _children)
            if (!ReferenceEquals(sibling, child)) othersTotal += sibling.SizeRatio;

        double remaining = 1.0 - target;

        if (othersTotal <= double.Epsilon)
        {
            // Degenerate: siblings have no size to scale. Split what is left evenly.
            double even = remaining / (_children.Count - 1);
            foreach (Node sibling in _children)
                if (!ReferenceEquals(sibling, child)) sibling.SizeRatio = even;
        }
        else
        {
            double scale = remaining / othersTotal;
            foreach (Node sibling in _children)
                if (!ReferenceEquals(sibling, child)) sibling.SizeRatio *= scale;
        }

        child.SizeRatio = target;
        Normalise();
    }

    /// <summary>Gives every child an equal share.</summary>
    public void EqualiseChildren()
    {
        if (_children.Count == 0) return;

        double share = 1.0 / _children.Count;
        foreach (Node child in _children)
            child.SizeRatio = share;
    }

    /// <summary>
    /// Rescales children so their ratios sum to exactly 1.0.
    /// </summary>
    /// <remarks>
    /// Called after every mutation. Guards against accumulated floating point drift:
    /// over thousands of insert/remove cycles the sum would otherwise wander far
    /// enough to produce visible seams.
    /// </remarks>
    private void Normalise()
    {
        if (_children.Count == 0) return;

        double total = 0;
        foreach (Node child in _children) total += child.SizeRatio;

        if (total <= double.Epsilon)
        {
            EqualiseChildren();
            return;
        }

        if (Math.Abs(total - 1.0) < 1e-9) return;

        foreach (Node child in _children)
            child.SizeRatio /= total;
    }

    public override string ToString() =>
        $"Container#{Id}[{Layout.Name}, {_children.Count} children]";
}
