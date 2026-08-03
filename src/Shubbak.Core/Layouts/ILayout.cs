using Shubbak.Core.Geometry;
using Shubbak.Core.Tree;

namespace Shubbak.Core.Layouts;

/// <summary>
/// Tuning passed down to every layout during an arrange pass.
/// </summary>
/// <param name="InnerGap">Spacing between adjacent siblings, in pixels.</param>
/// <param name="MinimumTileExtent">
/// Smallest extent, along either axis, that a tile may be given. Prevents a
/// container with many children in a narrow area from producing zero- or
/// negative-sized windows, which Win32 handles poorly and which would make a
/// window impossible to grab with the mouse.
/// </param>
public readonly record struct LayoutOptions(int InnerGap = 0, int MinimumTileExtent = 24)
{
    public static LayoutOptions Default => new();
}

/// <summary>
/// Computes rectangles for the direct children of one container.
/// </summary>
/// <remarks>
/// <para>
/// Implementations arrange <b>direct children only</b>. Recursion into child
/// containers is the engine's job (<see cref="LayoutEngine"/>). That split is what
/// keeps each layout small enough to reason about and test in isolation, and it is
/// what makes layouts freely composable: a fibonacci container nested inside a
/// columns container needs no cooperation between the two implementations.
/// </para>
/// <para>
/// Implementations must be <b>stateless and thread-safe</b>. All state belongs to
/// the tree - a layout that remembered anything between passes would break when the
/// same layout instance is shared by many containers, which is the normal case.
/// </para>
/// </remarks>
public interface ILayout
{
    /// <summary>Stable identifier used in config and IPC, e.g. <c>"splith"</c>.</summary>
    string Name { get; }

    /// <summary>
    /// The axis along which children are distributed, where that is meaningful.
    /// Used for direction-aware operations such as focus and move; layouts with no
    /// dominant axis (monocle, tabbed) may return <see langword="null"/>.
    /// </summary>
    Axis? PrimaryAxis { get; }

    /// <summary>
    /// Whether the rectangles this layout produces overlap one another.
    /// </summary>
    /// <remarks>
    /// False for every tiling layout, which is what makes stacking order irrelevant
    /// to them. A layout that answers true is saying that stacking is the only thing
    /// deciding what the user sees, so the engine must raise the focused window -
    /// monocle gives every child the whole area, and without a raise it showed
    /// whichever window happened to already be on top.
    /// </remarks>
    bool Overlaps => false;

    /// <summary>
    /// Writes the rectangle for each direct child of <paramref name="container"/>
    /// into <paramref name="destination"/>.
    /// </summary>
    /// <param name="container">The container being arranged.</param>
    /// <param name="area">
    /// The rectangle to divide. Outer gaps have already been applied by the engine.
    /// </param>
    /// <param name="options">Gap and minimum-size tuning.</param>
    /// <param name="destination">
    /// Receives one rectangle per child, in child order. Its length always equals
    /// <c>container.Children.Count</c>.
    /// </param>
    void Arrange(ContainerNode container, Rect area, in LayoutOptions options, Span<Rect> destination);

    /// <summary>
    /// Decides where a new child should be placed.
    /// </summary>
    /// <param name="container">The container receiving the node.</param>
    /// <param name="reference">
    /// The focused child to insert relative to, or <see langword="null"/> when there
    /// is no relevant focus.
    /// </param>
    /// <returns>An index in <c>[0, container.Children.Count]</c>.</returns>
    /// <remarks>
    /// Insertion is layout-specific and not merely "append": manual split inserts
    /// after the focused window, whereas master-stack inserts at the head and
    /// fibonacci subdivides the largest tile. Keeping this on
    /// <see cref="ILayout"/> is what stops that knowledge leaking into the command
    /// layer.
    /// </remarks>
    int ResolveInsertIndex(ContainerNode container, Node? reference);

    /// <summary>
    /// Whether moving focus from <paramref name="from"/> in
    /// <paramref name="direction"/> stays inside this container, and if so where it
    /// lands.
    /// </summary>
    /// <returns>
    /// The sibling to move to, or <see langword="null"/> when the movement leaves
    /// this container and should be retried on the parent.
    /// </returns>
    Node? Navigate(ContainerNode container, Node from, Direction direction);
}
