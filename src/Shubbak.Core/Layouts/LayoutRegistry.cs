namespace Shubbak.Core.Layouts;

/// <summary>
/// Registry of the layouts available to config and IPC.
/// </summary>
/// <remarks>
/// P1 ships manual split only. P2 adds fibonacci, bsp, master-stack, columns, rows,
/// grid, tabbed, stacked and monocle; because <see cref="ILayout"/> already covers
/// arrange, insert and navigate, those are purely additive - no existing call site
/// changes.
/// </remarks>
public static class LayoutRegistry
{
    /// <summary>
    /// The layout new containers get when none is specified.
    /// </summary>
    /// <remarks>
    /// Horizontal, matching GlazeWM and i3: on a landscape display the first split a
    /// user makes is almost always side-by-side.
    /// </remarks>
    public static ILayout Default => SplitLayout.Horizontal;

    private static readonly Dictionary<string, ILayout> s_byName =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [SplitLayout.Horizontal.Name] = SplitLayout.Horizontal,
            [SplitLayout.Vertical.Name] = SplitLayout.Vertical,

            // Friendlier aliases, so config need not adopt i3's terminology.
            ["horizontal"] = SplitLayout.Horizontal,
            ["vertical"] = SplitLayout.Vertical,
            ["row"] = SplitLayout.Horizontal,
            ["column"] = SplitLayout.Vertical,
        };

    /// <summary>Every distinct registered layout.</summary>
    public static IEnumerable<ILayout> All => s_byName.Values.Distinct();

    /// <summary>Canonical names, excluding aliases.</summary>
    public static IEnumerable<string> CanonicalNames => All.Select(l => l.Name);

    public static bool TryResolve(string name, out ILayout layout) =>
        s_byName.TryGetValue(name, out layout!);

    /// <summary>Resolves a layout by name or alias.</summary>
    /// <exception cref="ArgumentException">The name is not registered.</exception>
    public static ILayout Resolve(string name)
    {
        if (TryResolve(name, out ILayout layout)) return layout;

        throw new ArgumentException(
            $"Unknown layout '{name}'. Available: {string.Join(", ", CanonicalNames.Order(StringComparer.Ordinal))}.",
            nameof(name));
    }
}
