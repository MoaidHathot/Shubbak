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
    /// The prefix of the one-value-per-context sources: <c>context.presenting</c> is
    /// <c>presenting</c> while that context holds and empty when it does not.
    /// </summary>
    /// <remarks>
    /// The joined list is for reading; this is for one widget per context - an icon
    /// that shows while the camera is on, another while a meeting is muted - which a
    /// <c>when value=</c> on the joined string cannot do once two contexts hold at
    /// once. Empty rather than absent when a context drops, so the widget that was
    /// showing it hides, and so a <c>when of=</c> on it sees a change.
    /// </remarks>
    public const string KeyPrefix = "context.";

    /// <summary>The source name for one context.</summary>
    public static string KeyFor(string name) => KeyPrefix + name;

    /// <summary>
    /// The per-context values to publish when the list changes: every context that
    /// holds now set to its name, and every context that held before and does not now
    /// set to empty.
    /// </summary>
    /// <remarks>
    /// Only what changed or holds is written. A context that never held has no key,
    /// and a template naming it reads empty anyway; a context that dropped must be
    /// written empty once, or its widget would stay lit.
    /// </remarks>
    public static IReadOnlyList<KeyValuePair<string, string>> Changes(
        IReadOnlyList<string>? before, IReadOnlyList<string> after)
    {
        ArgumentNullException.ThrowIfNull(after);

        List<KeyValuePair<string, string>> changes = [];

        foreach (string name in after)
            changes.Add(new KeyValuePair<string, string>(KeyFor(name), name));

        if (before is not null)
        {
            foreach (string name in before)
                if (!after.Contains(name, StringComparer.Ordinal))
                    changes.Add(new KeyValuePair<string, string>(KeyFor(name), string.Empty));
        }

        return changes;
    }

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
