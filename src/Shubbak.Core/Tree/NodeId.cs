using System.Globalization;

namespace Shubbak.Core.Tree;

/// <summary>
/// Process-unique identity for a <see cref="Node"/>.
/// </summary>
/// <remarks>
/// A distinct type rather than a raw <see langword="long"/> so that node ids,
/// window handles, workspace indices, and monitor indices cannot be accidentally
/// interchanged - a class of bug that is easy to write and tedious to find.
/// Ids are never reused within a process, so a stale id from the bar or an IPC
/// client resolves to "gone" rather than to a different window.
/// </remarks>
public readonly record struct NodeId(long Value) : IComparable<NodeId>
{
    private static long s_next;

    public static NodeId None => default;

    public bool IsNone => Value == 0;

    internal static NodeId Next() => new(Interlocked.Increment(ref s_next));

    public int CompareTo(NodeId other) => Value.CompareTo(other.Value);

    public static bool operator <(NodeId left, NodeId right) => left.Value < right.Value;
    public static bool operator <=(NodeId left, NodeId right) => left.Value <= right.Value;
    public static bool operator >(NodeId left, NodeId right) => left.Value > right.Value;
    public static bool operator >=(NodeId left, NodeId right) => left.Value >= right.Value;

    public override string ToString() =>
        Value.ToString(CultureInfo.InvariantCulture);
}
