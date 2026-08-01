using Shubbak.Core.Geometry;
using Shubbak.Core.Tree;

namespace Shubbak.Core.Layouts;

/// <summary>
/// One large "master" area beside a stack of the remaining windows.
/// </summary>
/// <remarks>
/// <para>
/// The classic dwm/xmonad layout, and the one most people mean by "tiling". It
/// suits the common working pattern - one thing you are looking at, several you are
/// referring to - better than an even split does.
/// </para>
/// <para>
/// The master area's share is taken from the first child's
/// <see cref="Node.SizeRatio"/>, so dragging the divider works exactly as it does
/// in a split layout, with no layout-specific resize code.
/// </para>
/// </remarks>
public sealed class MasterStackLayout : ILayout
{
    /// <summary>Master on the left, stack on the right.</summary>
    public static MasterStackLayout Left { get; } = new(Axis.Horizontal, masterFirst: true, 1);

    /// <summary>Master on the right, stack on the left.</summary>
    public static MasterStackLayout Right { get; } = new(Axis.Horizontal, masterFirst: false, 1);

    /// <summary>Master on top, stack below.</summary>
    public static MasterStackLayout Top { get; } = new(Axis.Vertical, masterFirst: true, 1);

    /// <summary>Master at the bottom, stack above.</summary>
    public static MasterStackLayout Bottom { get; } = new(Axis.Vertical, masterFirst: false, 1);

    private MasterStackLayout(Axis axis, bool masterFirst, int masterCount)
    {
        Axis = axis;
        MasterFirst = masterFirst;
        MasterCount = masterCount;
    }

    /// <summary>The axis separating master from stack.</summary>
    public Axis Axis { get; }

    /// <summary>Whether master comes first in screen order.</summary>
    public bool MasterFirst { get; }

    /// <summary>How many windows share the master area.</summary>
    public int MasterCount { get; }

    public string Name => (Axis, MasterFirst) switch
    {
        (Axis.Horizontal, true) => "master-left",
        (Axis.Horizontal, false) => "master-right",
        (Axis.Vertical, true) => "master-top",
        _ => "master-bottom",
    };

    public Axis? PrimaryAxis => Axis;

    /// <summary>Returns a variant with a different number of master windows.</summary>
    public MasterStackLayout WithMasterCount(int count) =>
        new(Axis, MasterFirst, Math.Max(1, count));

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

        int masters = Math.Min(MasterCount, count - 1);
        int stackCount = count - masters;

        // The master's share is its per-window weight measured against the stack's
        // per-window weight. Using the raw sum instead would give master 1/n of the
        // screen by default - a third with three windows - whereas the whole point
        // of the layout is that master is large. Comparing per-window weights makes
        // untouched ratios yield exactly half, and still lets a resize grow it.
        double masterWeight = 0;
        for (int i = 0; i < masters; i++) masterWeight += children[i].SizeRatio;

        double stackWeight = 0;
        for (int i = masters; i < count; i++) stackWeight += children[i].SizeRatio;

        double masterPer = masterWeight / masters;
        double stackPer = stackCount > 0 ? stackWeight / stackCount : masterPer;

        double denominator = masterPer + stackPer;
        double share = denominator <= double.Epsilon
            ? 0.5
            : Math.Clamp(masterPer / denominator, 0.1, 0.9);

        (Rect first, Rect second) = LayoutMath.Split(area, Axis, share, options.InnerGap);

        Rect masterArea = MasterFirst ? first : second;
        Rect stackArea = MasterFirst ? second : first;

        // Master and stack both run along the cross axis, so a two-window master
        // sits one above the other when the split itself is left/right.
        Axis stackAxis = Axis.Cross();

        FillArea(children, 0, masters, masterArea, stackAxis, options.InnerGap, destination);
        FillArea(children, masters, count - masters, stackArea, stackAxis, options.InnerGap, destination);
    }

    private static void FillArea(
        IReadOnlyList<Node> children, int offset, int count,
        Rect area, Axis axis, int gap, Span<Rect> destination)
    {
        if (count <= 0) return;

        if (count == 1)
        {
            destination[offset] = area;
            return;
        }

        Span<int> sizes = count <= 32 ? stackalloc int[count] : new int[count];
        int available = Math.Max(0, area.Extent(axis) - (gap * (count - 1)));

        // Evenly rather than by ratio: the stack's individual ratios are reserved
        // for the master divider, and a stack whose members drift to different
        // heights is more confusing than useful.
        LayoutMath.DistributeEvenly(available, sizes);
        LayoutMath.PlaceAlongAxis(area, axis, sizes, gap, destination[offset..(offset + count)]);
    }

    /// <summary>
    /// New windows join the head of the stack, just after the master windows.
    /// </summary>
    /// <remarks>
    /// Matches dwm: the newest window is the most likely to be promoted to master
    /// next, so putting it adjacent keeps that one keystroke away.
    /// </remarks>
    public int ResolveInsertIndex(ContainerNode container, Node? reference)
    {
        ArgumentNullException.ThrowIfNull(container);
        return Math.Min(MasterCount, container.Children.Count);
    }

    public Node? Navigate(ContainerNode container, Node from, Direction direction)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(from);

        return GeometricNavigator.Navigate(container, from, direction);
    }

    public override string ToString() => Name;
}
