namespace Taj.Core;

/// <summary>
/// What the bar says about the contexts the window manager holds.
/// </summary>
/// <remarks>
/// <para>
/// A context is a named condition the window manager has decided holds - a talk is
/// being given, a second monitor is attached, somebody said so over the pipe - and
/// while it holds the window manager's configuration changes shape. The bar has two
/// uses for the names: a rule can pick a profile by one, and a widget can show them.
/// This is the second.
/// </para>
/// <para>
/// Pure and separate from the connection that feeds it, like <see cref="WindowManagerStatus"/>,
/// so the spelling can be held to account without a window manager, a pipe or a bar.
/// </para>
/// </remarks>
public static class ActiveContexts
{
    /// <summary>Source name carrying the active contexts, for <c>{{ contexts }}</c>.</summary>
    public const string Key = "contexts";

    /// <summary>
    /// The names as one value, in the order the window manager listed them.
    /// </summary>
    /// <remarks>
    /// Joined with a comma and a space, which is how <c>shubbak status</c> spells the
    /// same list, so the bar and the command line agree. Empty when none hold, and a
    /// template widget hides itself when its result is empty.
    /// </remarks>
    public static string Label(IReadOnlyList<string>? names) =>
        names is { Count: > 0 } ? string.Join(", ", names) : string.Empty;

    /// <summary>
    /// Whether two answers from the window manager name the same contexts in the same
    /// order.
    /// </summary>
    /// <remarks>
    /// Order counts. The window manager lists contexts in declaration order, which is
    /// the order they cascade in, and two lists that differ only in order are two
    /// different answers about what is in force.
    /// </remarks>
    public static bool Same(IReadOnlyList<string>? before, IReadOnlyList<string> after)
    {
        ArgumentNullException.ThrowIfNull(after);

        if (before is null || before.Count != after.Count) return false;

        for (int i = 0; i < after.Count; i++)
            if (!string.Equals(before[i], after[i], StringComparison.Ordinal)) return false;

        return true;
    }
}
