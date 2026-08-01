using System.Globalization;

namespace Shubbak.Config.Kdl;

/// <summary>The kind of a KDL scalar.</summary>
public enum KdlValueKind
{
    Text,
    Number,
    Boolean,
    Null,
}

/// <summary>A KDL scalar: an argument or a property value.</summary>
public sealed record KdlValue
{
    public required KdlValueKind Kind { get; init; }

    public required TextSpan Span { get; init; }

    /// <summary>The text as written, used in diagnostics.</summary>
    public required string Raw { get; init; }

    public string? StringValue { get; init; }

    public double NumberValue { get; init; }

    /// <summary>True when the number was written without a fractional part.</summary>
    public bool IsInteger { get; init; }

    public bool BooleanValue { get; init; }

    public bool IsNull => Kind == KdlValueKind.Null;

    /// <summary>
    /// The value as a string, converting numbers and booleans.
    /// </summary>
    /// <remarks>
    /// Deliberately permissive. Workspace names in the author's config include
    /// <c>1</c>, <c>-</c> and <c>\</c>; requiring quotes around the numeric ones
    /// while the symbolic ones go bare would be an irritating inconsistency to
    /// remember.
    /// </remarks>
    public string AsString() => Kind switch
    {
        KdlValueKind.Text => StringValue ?? string.Empty,
        KdlValueKind.Number => IsInteger
            ? ((long)NumberValue).ToString(CultureInfo.InvariantCulture)
            : NumberValue.ToString(CultureInfo.InvariantCulture),
        KdlValueKind.Boolean => BooleanValue ? "true" : "false",
        _ => string.Empty,
    };

    public bool TryAsInt(out int value)
    {
        if (Kind is KdlValueKind.Number &&
            NumberValue >= int.MinValue && NumberValue <= int.MaxValue)
        {
            value = (int)NumberValue;
            return true;
        }

        value = 0;
        return false;
    }

    public bool TryAsDouble(out double value)
    {
        if (Kind is KdlValueKind.Number)
        {
            value = NumberValue;
            return true;
        }

        value = 0;
        return false;
    }

    public bool TryAsBool(out bool value)
    {
        if (Kind == KdlValueKind.Boolean)
        {
            value = BooleanValue;
            return true;
        }

        value = false;
        return false;
    }

    public override string ToString() => Raw;
}

/// <summary>
/// A KDL node: a name, positional arguments, properties, and optional children.
/// </summary>
public sealed class KdlNode
{
    public required string Name { get; init; }

    public required TextSpan NameSpan { get; init; }

    /// <summary>The whole node including its children block.</summary>
    public required TextSpan Span { get; init; }

    public IReadOnlyList<KdlValue> Arguments { get; init; } = [];

    public IReadOnlyDictionary<string, KdlValue> Properties { get; init; } =
        new Dictionary<string, KdlValue>(StringComparer.Ordinal);

    public IReadOnlyList<KdlNode> Children { get; init; } = [];

    /// <summary>The positional argument at <paramref name="index"/>, or null.</summary>
    public KdlValue? Argument(int index) =>
        index >= 0 && index < Arguments.Count ? Arguments[index] : null;

    /// <summary>The named property, or null.</summary>
    public KdlValue? Property(string name) =>
        Properties.TryGetValue(name, out KdlValue? value) ? value : null;

    /// <summary>The first child with this name, or null.</summary>
    public KdlNode? Child(string name)
    {
        foreach (KdlNode child in Children)
            if (string.Equals(child.Name, name, StringComparison.Ordinal)) return child;

        return null;
    }

    /// <summary>Every child with this name.</summary>
    public IEnumerable<KdlNode> ChildrenNamed(string name)
    {
        foreach (KdlNode child in Children)
            if (string.Equals(child.Name, name, StringComparison.Ordinal)) yield return child;
    }

    public override string ToString() => $"{Name} ({Arguments.Count} args, {Children.Count} children)";
}

/// <summary>A parsed KDL document.</summary>
public sealed class KdlDocument
{
    public required IReadOnlyList<KdlNode> Nodes { get; init; }

    public KdlNode? Node(string name)
    {
        foreach (KdlNode node in Nodes)
            if (string.Equals(node.Name, name, StringComparison.Ordinal)) return node;

        return null;
    }

    public IEnumerable<KdlNode> NodesNamed(string name)
    {
        foreach (KdlNode node in Nodes)
            if (string.Equals(node.Name, name, StringComparison.Ordinal)) yield return node;
    }
}
