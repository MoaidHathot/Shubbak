using Shubbak.Core.Geometry;
using Shubbak.Core.Tree;

namespace Shubbak.Core.Layouts;

/// <summary>
/// The spiral layout: each new window halves the remaining space, alternating axis.
/// </summary>
/// <remarks>
/// <para>
/// Known as <c>dwindle</c> in Hyprland, <c>fibonacci</c> in awesome and xmonad, and
/// <c>spiral</c> elsewhere. Unlike manual split it <b>restructures automatically</b>
/// - the user never places a window, the layout decides - which is precisely the
/// ergonomic appeal: opening a fourth terminal needs no thought about direction.
/// </para>
/// <para>
/// Implemented over a <i>flat</i> child list rather than by building a nested tree.
/// The spiral is computed geometrically at arrange time, so the tree stays shallow
/// and switching a container to and from this layout is lossless. Building nesting
/// instead would leave a tangle of containers behind on every layout change - which
/// is why window managers that do it that way cannot switch layouts cleanly.
/// </para>
/// <para>
/// Ratios are honoured: each split uses the ratio of the child being placed against
/// the total remaining, so interactive resize still works.
/// </para>
/// </remarks>
public sealed class FibonacciLayout : ILayout
{
    /// <summary>Splits horizontally first, i.e. the first divide is left/right.</summary>
    public static FibonacciLayout Horizontal { get; } = new(Axis.Horizontal, mirrored: false);

    /// <summary>Splits vertically first.</summary>
    public static FibonacciLayout Vertical { get; } = new(Axis.Vertical, mirrored: false);

    /// <summary>Spirals towards the top-left instead of the bottom-right.</summary>
    public static FibonacciLayout Mirrored { get; } = new(Axis.Horizontal, mirrored: true);

    private FibonacciLayout(Axis firstAxis, bool mirrored)
    {
        FirstAxis = firstAxis;
        IsMirrored = mirrored;
    }

    /// <summary>The axis of the first split.</summary>
    public Axis FirstAxis { get; }

    /// <summary>Whether the spiral runs towards the origin.</summary>
    public bool IsMirrored { get; }

    public string Name => IsMirrored
        ? "fibonacci-mirrored"
        : FirstAxis == Axis.Horizontal ? "fibonacci" : "fibonacci-v";

    /// <summary>
    /// Null: the axis alternates as the spiral descends, so no single axis
    /// describes the container. Navigation therefore falls back to geometry.
    /// </summary>
    public Axis? PrimaryAxis => null;

    public void Arrange(ContainerNode container, Rect area, in LayoutOptions options, Span<Rect> destination)
    {
        ArgumentNullException.ThrowIfNull(container);

        IReadOnlyList<Node> children = container.Children;
        int count = children.Count;

        if (count != destination.Length)
            throw new ArgumentException(
                $"Destination length {destination.Length} does not match child count {count}.",
                nameof(destination));

        if (count == 0) return;

        if (count == 1)
        {
            destination[0] = area;
            return;
        }

        Rect remaining = area;
        Axis axis = FirstAxis;

        // Every child except the last takes a slice of what is left; the last one
        // inherits the remainder, which is what closes the spiral exactly.
        //
        // The share is this child's weight measured against the *average* weight of
        // those still to be placed. That is the only formulation under which equally
        // sized children produce a true halving at every step: comparing against the
        // remaining total instead would give the first of four children a quarter of
        // the screen rather than a half, which is not a spiral at all.
        double remainingWeight = 0;
        for (int i = 0; i < count; i++) remainingWeight += children[i].SizeRatio;

        for (int i = 0; i < count - 1; i++)
        {
            double own = children[i].SizeRatio;
            int othersCount = count - i - 1;
            double othersAverage = othersCount > 0 ? (remainingWeight - own) / othersCount : own;

            double denominator = own + othersAverage;
            double share = denominator <= double.Epsilon
                ? 0.5
                : Math.Clamp(own / denominator, 0.05, 0.95);

            (Rect taken, Rect rest) = LayoutMath.Split(remaining, axis, share, options.InnerGap);

            // Mirroring swaps which half the window takes, turning the spiral
            // inside out without changing any of the arithmetic.
            destination[i] = IsMirrored ? rest : taken;
            remaining = IsMirrored ? taken : rest;

            remainingWeight -= own;
            axis = axis.Cross();
        }

        destination[count - 1] = remaining;
    }

    /// <summary>
    /// New windows go at the end, which is what makes the spiral subdivide the
    /// smallest remaining region rather than displacing an existing tile.
    /// </summary>
    public int ResolveInsertIndex(ContainerNode container, Node? reference)
    {
        ArgumentNullException.ThrowIfNull(container);
        return container.Children.Count;
    }

    public Node? Navigate(ContainerNode container, Node from, Direction direction)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(from);

        // The spiral has no list order that corresponds to screen direction, so
        // navigation is geometric: pick the nearest child whose rectangle lies the
        // requested way and overlaps on the cross axis.
        return GeometricNavigator.Navigate(container, from, direction);
    }

    public override string ToString() => Name;
}
