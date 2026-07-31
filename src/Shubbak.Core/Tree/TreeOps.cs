using Shubbak.Core.Geometry;
using Shubbak.Core.Layouts;

namespace Shubbak.Core.Tree;

/// <summary>
/// Structural operations on the window tree.
/// </summary>
/// <remarks>
/// <para>
/// These are the primitives that commands are built from. Keeping them here rather
/// than on <see cref="ContainerNode"/> is deliberate: <see cref="ContainerNode"/>
/// owns only its own children and its size invariant, whereas everything below
/// spans several nodes and must keep the <i>tree</i> well-formed.
/// </para>
/// <para>
/// The recurring theme is <see cref="Flatten(ContainerNode)"/>. Every operation
/// that removes a node can leave behind a container that is empty, or that holds a
/// single child and therefore adds nothing but an extra level of nesting. Left
/// alone these accumulate, and the tree slowly fills with invisible structure that
/// makes focus movement and resizing behave unpredictably - a failure mode that is
/// very hard to diagnose from the outside because nothing looks wrong on screen.
/// </para>
/// </remarks>
public static class TreeOps
{
    /// <summary>
    /// Inserts <paramref name="node"/> into <paramref name="container"/> at the
    /// position its layout chooses, relative to <paramref name="reference"/>.
    /// </summary>
    public static void InsertByLayout(ContainerNode container, Node node, Node? reference = null)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(node);

        int index = container.Layout.ResolveInsertIndex(container, reference);
        container.Insert(Math.Clamp(index, 0, container.Count), node);
    }

    /// <summary>
    /// Detaches <paramref name="node"/> and tidies up any structure its removal
    /// made redundant.
    /// </summary>
    /// <returns>
    /// The container the node was removed from, or <see langword="null"/> if it was
    /// not attached to one.
    /// </returns>
    public static ContainerNode? Detach(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);

        ContainerNode? parent = node.ParentContainer;
        if (parent is null) return null;

        parent.Remove(node);
        Flatten(parent);
        return parent;
    }

    /// <summary>
    /// Removes redundant nesting at <paramref name="container"/> and upwards.
    /// </summary>
    /// <remarks>
    /// <para>Two cases are collapsed:</para>
    /// <list type="number">
    ///   <item>An <b>empty</b> non-workspace container is removed entirely.</item>
    ///   <item>A container with exactly <b>one</b> child is replaced by that child,
    ///   which inherits its size. A one-child split is by definition invisible on
    ///   screen, so keeping it would mean the tree no longer matches what the user
    ///   sees.</item>
    /// </list>
    /// <para>
    /// Workspaces are never removed by flattening: an empty workspace is a normal,
    /// meaningful state, and a workspace with one child must keep its identity so
    /// its keybinding continues to work. A workspace with a single container child
    /// does absorb that child's layout, which is what makes
    /// <c>toggle-tiling-direction</c> on a bare workspace behave sensibly.
    /// </para>
    /// </remarks>
    public static void Flatten(ContainerNode? container)
    {
        while (container is not null)
        {
            ContainerNode? parent = container.ParentContainer;

            if (container is WorkspaceNode workspace)
            {
                AbsorbSoleContainerChild(workspace);
                return;
            }

            if (container.Count == 0)
            {
                if (parent is null) return;
                parent.Remove(container);
                container = parent;
                continue;
            }

            if (container.Count == 1)
            {
                Node sole = container.Children[0];

                if (parent is null)
                {
                    // A detached single-child container is nobody's problem yet.
                    return;
                }

                container.Remove(sole);
                parent.Replace(container, sole);
                container = parent;
                continue;
            }

            return;
        }
    }

    /// <summary>
    /// Collapses <c>workspace -> [container] -> children</c> into
    /// <c>workspace -> children</c>, adopting the container's layout.
    /// </summary>
    private static void AbsorbSoleContainerChild(WorkspaceNode workspace)
    {
        while (workspace.Count == 1 &&
               workspace.Children[0] is ContainerNode sole and not WorkspaceNode)
        {
            IReadOnlyList<Node> grandchildren = sole.DetachAll();
            workspace.Remove(sole);
            workspace.Layout = sole.Layout;

            foreach (Node child in grandchildren)
                workspace.Add(child);
        }
    }

    /// <summary>
    /// Wraps <paramref name="node"/> in a new container using
    /// <paramref name="layout"/>, in place.
    /// </summary>
    /// <returns>The new container, now holding <paramref name="node"/>.</returns>
    /// <remarks>
    /// This is how nesting is created - the operation behind <c>split</c>. The new
    /// container inherits the node's slot and size, so nothing visibly moves until
    /// a second window is added to it.
    /// </remarks>
    public static ContainerNode Wrap(Node node, ILayout layout)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(layout);

        ContainerNode? parent = node.ParentContainer
            ?? throw new InvalidOperationException(
                $"Node {node.Id} has no container parent and cannot be wrapped.");

        var wrapper = new ContainerNode(layout);

        parent.Replace(node, wrapper);
        wrapper.Add(node);
        node.SizeRatio = 1.0;

        return wrapper;
    }

    /// <summary>
    /// Moves <paramref name="node"/> into <paramref name="destination"/> at
    /// <paramref name="index"/>, tidying the tree it left behind.
    /// </summary>
    public static void Reparent(Node node, ContainerNode destination, int index)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(destination);

        if (node.Contains(destination))
            throw new InvalidOperationException(
                $"Cannot move node {node.Id} into its own descendant {destination.Id}.");

        ContainerNode? source = node.ParentContainer;

        if (ReferenceEquals(source, destination))
        {
            destination.MoveChild(node, index);
            return;
        }

        source?.Remove(node);
        destination.Insert(Math.Clamp(index, 0, destination.Count), node);

        // Only after the insert: flattening the source first could delete a
        // container that the destination index was computed against.
        if (source is not null) Flatten(source);
    }

    /// <summary>
    /// Exchanges the positions of two nodes, which need not share a parent.
    /// </summary>
    public static void Swap(Node a, Node b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (ReferenceEquals(a, b)) return;

        if (a.Contains(b) || b.Contains(a))
            throw new InvalidOperationException(
                $"Cannot swap nodes {a.Id} and {b.Id}; one contains the other.");

        ContainerNode? parentA = a.ParentContainer
            ?? throw new InvalidOperationException($"Node {a.Id} is not attached.");
        ContainerNode? parentB = b.ParentContainer
            ?? throw new InvalidOperationException($"Node {b.Id} is not attached.");

        if (ReferenceEquals(parentA, parentB))
        {
            parentA.SwapChildren(a, b);
            return;
        }

        int indexA = parentA.IndexOf(a);
        int indexB = parentB.IndexOf(b);
        double ratioA = a.SizeRatio;
        double ratioB = b.SizeRatio;

        // Detach both before reinserting either, so the two indices stay valid.
        parentA.Remove(a);
        parentB.Remove(b);

        parentA.Insert(Math.Clamp(indexA, 0, parentA.Count), b);
        parentB.Insert(Math.Clamp(indexB, 0, parentB.Count), a);

        // Sizes belong to the slot, not the node: a swap trades places on screen
        // without either window changing dimensions.
        b.SizeRatio = ratioA;
        a.SizeRatio = ratioB;
    }

    /// <summary>
    /// Toggles a container between horizontal and vertical splitting.
    /// </summary>
    /// <returns>The layout now in effect.</returns>
    public static ILayout ToggleSplitDirection(ContainerNode container)
    {
        ArgumentNullException.ThrowIfNull(container);

        container.Layout = container.Layout switch
        {
            SplitLayout split => split.Transposed,

            // A layout with no axis (monocle, tabbed in P2) has no direction to
            // toggle; adopt the default rather than silently doing nothing.
            _ => LayoutRegistry.Default,
        };

        return container.Layout;
    }

    /// <summary>
    /// The nearest ancestor container (including <paramref name="node"/> itself)
    /// whose layout runs along <paramref name="axis"/>.
    /// </summary>
    /// <remarks>
    /// This is how a resize request finds the container that can actually satisfy
    /// it: widening a window inside a vertical split has to be applied further up,
    /// at the first ancestor that divides space horizontally.
    /// </remarks>
    public static ContainerNode? NearestAncestorOnAxis(Node node, Axis axis)
    {
        ArgumentNullException.ThrowIfNull(node);

        for (Node? n = node; n is not null; n = n.Parent)
            if (n is ContainerNode container && container.Layout.PrimaryAxis == axis && container.Count > 1)
                return container;

        return null;
    }

    /// <summary>
    /// The child of <paramref name="ancestor"/> that contains
    /// <paramref name="descendant"/>.
    /// </summary>
    public static Node? ChildContaining(ContainerNode ancestor, Node descendant)
    {
        ArgumentNullException.ThrowIfNull(ancestor);
        ArgumentNullException.ThrowIfNull(descendant);

        for (Node? n = descendant; n is not null; n = n.Parent)
            if (ReferenceEquals(n.Parent, ancestor)) return n;

        return null;
    }
}
