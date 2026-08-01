namespace Shubbak.Core.Layouts;

/// <summary>
/// Registry of the layouts available to config, keybindings and IPC.
/// </summary>
/// <remarks>
/// Adding a layout is purely additive: <see cref="ILayout"/> already covers
/// arrange, insert and navigate, so nothing outside this file changes.
/// </remarks>
public static class LayoutRegistry
{
    /// <summary>
    /// The layout new containers get when none is specified.
    /// </summary>
    /// <remarks>
    /// Manual horizontal split, matching GlazeWM and i3: predictable, and the only
    /// layout in which the user decides where a window goes.
    /// </remarks>
    public static ILayout Default => SplitLayout.Horizontal;

    private static readonly Dictionary<string, ILayout> s_byName =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Manual split.
            [SplitLayout.Horizontal.Name] = SplitLayout.Horizontal,
            [SplitLayout.Vertical.Name] = SplitLayout.Vertical,
            ["horizontal"] = SplitLayout.Horizontal,
            ["vertical"] = SplitLayout.Vertical,
            ["row"] = SplitLayout.Horizontal,
            ["rows"] = SplitLayout.Vertical,
            ["column"] = SplitLayout.Vertical,
            ["columns"] = SplitLayout.Horizontal,

            // Automatic spiral.
            [FibonacciLayout.Horizontal.Name] = FibonacciLayout.Horizontal,
            [FibonacciLayout.Vertical.Name] = FibonacciLayout.Vertical,
            [FibonacciLayout.Mirrored.Name] = FibonacciLayout.Mirrored,
            ["dwindle"] = FibonacciLayout.Horizontal,
            ["spiral"] = FibonacciLayout.Horizontal,

            // Master and stack.
            [MasterStackLayout.Left.Name] = MasterStackLayout.Left,
            [MasterStackLayout.Right.Name] = MasterStackLayout.Right,
            [MasterStackLayout.Top.Name] = MasterStackLayout.Top,
            [MasterStackLayout.Bottom.Name] = MasterStackLayout.Bottom,
            ["master"] = MasterStackLayout.Left,
            ["main"] = MasterStackLayout.Left,

            // Two-dimensional and stacked.
            [GridLayout.Instance.Name] = GridLayout.Instance,
            [MonocleLayout.Instance.Name] = MonocleLayout.Instance,
            ["fullscreen-stack"] = MonocleLayout.Instance,
        };

    /// <summary>
    /// The order <c>layout --cycle</c> walks.
    /// </summary>
    /// <remarks>
    /// Deliberately short and ordered by how different each is from the last, so
    /// repeatedly pressing the key produces visibly distinct arrangements rather
    /// than several near-identical ones.
    /// </remarks>
    private static readonly ILayout[] s_cycle =
    [
        SplitLayout.Horizontal,
        FibonacciLayout.Horizontal,
        MasterStackLayout.Left,
        GridLayout.Instance,
        MonocleLayout.Instance,
    ];

    /// <summary>Every distinct registered layout.</summary>
    public static IEnumerable<ILayout> All => s_byName.Values.Distinct();

    /// <summary>Canonical names, excluding aliases.</summary>
    public static IEnumerable<string> CanonicalNames =>
        All.Select(l => l.Name).Distinct().Order(StringComparer.Ordinal);

    public static bool TryResolve(string name, out ILayout layout) =>
        s_byName.TryGetValue(name, out layout!);

    /// <summary>Resolves a layout by name or alias.</summary>
    /// <exception cref="ArgumentException">The name is not registered.</exception>
    public static ILayout Resolve(string name)
    {
        if (TryResolve(name, out ILayout layout)) return layout;

        throw new ArgumentException(
            $"Unknown layout '{name}'. Available: {string.Join(", ", CanonicalNames)}.",
            nameof(name));
    }

    /// <summary>The next layout in the cycle after <paramref name="current"/>.</summary>
    public static ILayout Next(ILayout current)
    {
        ArgumentNullException.ThrowIfNull(current);

        int index = Array.IndexOf(s_cycle, current);

        // A layout outside the cycle - a mirrored spiral, say - starts the cycle
        // from the beginning rather than being treated as an error.
        return index < 0 ? s_cycle[0] : s_cycle[(index + 1) % s_cycle.Length];
    }

    /// <summary>The previous layout in the cycle.</summary>
    public static ILayout Previous(ILayout current)
    {
        ArgumentNullException.ThrowIfNull(current);

        int index = Array.IndexOf(s_cycle, current);
        return index < 0 ? s_cycle[^1] : s_cycle[(index - 1 + s_cycle.Length) % s_cycle.Length];
    }
}
