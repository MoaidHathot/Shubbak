using System.Globalization;
using Shubbak.Ipc;

namespace Dalil.Core;

/// <summary>
/// Turns what the window manager reports into rows the palette can offer.
/// </summary>
/// <remarks>
/// Pure, and separate from the model, because "what a row says" is a judgement worth
/// testing on its own: which badge a state earns, what a row's Enter key does, and
/// what happens to a window with no title are all decisions rather than plumbing.
/// </remarks>
public static class PaletteEntries
{
    /// <summary>Describes every window as a row.</summary>
    /// <param name="windows">What <c>query all-windows</c> returned.</param>
    /// <param name="includeUnmanaged">Whether to offer windows Shubbak does not manage.</param>
    public static IReadOnlyList<PaletteEntry> ForWindows(
        IEnumerable<WindowCandidate> windows, bool includeUnmanaged = true)
    {
        ArgumentNullException.ThrowIfNull(windows);

        List<PaletteEntry> entries = [];

        foreach (WindowCandidate window in windows)
        {
            if (!includeUnmanaged && !window.Managed) continue;

            entries.Add(new PaletteEntry(
                Title(window),
                Describe(window),
                Badges(window),

                // Focus is the answer to "where did it go" in every case: for a
                // managed window it switches workspace and raises it, and for one
                // the tree has never heard of the daemon falls through to revealing
                // it - uncloaking, restoring and foregrounding.
                $"focus-window {window.Handle.ToString(CultureInfo.InvariantCulture)}",

                // Recency, so the list is useful before anything is typed. Windows
                // that have never been focused sort last among themselves rather than
                // being scattered, which puts anything genuinely lost at the bottom
                // where it is looked for.
                window.FocusSequence));
        }

        return entries;
    }

    /// <summary>Describes every command verb as a row.</summary>
    public static IReadOnlyList<PaletteEntry> ForCommands(IEnumerable<CommandInfo> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);

        return
        [
            .. commands.Select(c => new PaletteEntry(
                c.Verb,
                c.Summary,
                c.Arguments.Count == 0 ? [] : [.. c.Arguments.Select(a => $"<{a}>")],

                // A verb that takes arguments cannot simply be run. Offering it as
                // text to complete is honest; running it with no argument would be
                // rejected by the parser and read as the palette being broken.
                c.Arguments.Count == 0 ? c.Verb : string.Empty)),
        ];
    }

    /// <summary>Describes every workspace as a row.</summary>
    public static IReadOnlyList<PaletteEntry> ForWorkspaces(IEnumerable<WorkspaceInfo> workspaces)
    {
        ArgumentNullException.ThrowIfNull(workspaces);

        return
        [
            .. workspaces.Select(w => new PaletteEntry(
                string.IsNullOrEmpty(w.DisplayName) ? w.Name : w.DisplayName,
                w.WindowCount == 1 ? "1 window" : $"{w.WindowCount} windows",
                w.Focused ? ["focused"] : w.Active ? ["displayed"] : [],
                $"focus --workspace {w.Name}",

                // Occupied workspaces first: an empty one is somewhere to go, not
                // something to find.
                w.WindowCount)),
        ];
    }

    /// <summary>Describes every layout as a row.</summary>
    public static IReadOnlyList<PaletteEntry> ForLayouts(IEnumerable<string> layouts)
    {
        ArgumentNullException.ThrowIfNull(layouts);

        return [.. layouts.Select(l => new PaletteEntry(l, "layout", [], $"layout --set {l}"))];
    }

    /// <summary>
    /// Describes the palette's own keys and prefixes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Prefixes are the fastest way to change mode and the least findable thing in the
    /// interface: nobody guesses that <c>~</c> means layouts. Tab makes every mode
    /// reachable without knowing any of them, the hint bar names them permanently, and
    /// this is where the whole set is written down.
    /// </para>
    /// <para>
    /// The mode rows are chooseable rather than being text about choosing. Somebody
    /// reading a list of keys will press Enter on the line they want; a help screen
    /// that ignores that has taught them the key and then refused to use it.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<PaletteEntry> ForHelp()
    {
        List<PaletteEntry> entries = [];

        foreach (PaletteMode mode in Enum.GetValues<PaletteMode>())
        {
            char prefix = PaletteModel.PrefixFor(mode);

            entries.Add(new PaletteEntry(
                PaletteModel.NameOf(mode),
                mode switch
                {
                    PaletteMode.Windows => "every window on the desktop, managed or not",
                    PaletteMode.Commands => "every command the window manager accepts",
                    PaletteMode.Workspaces => "go to a workspace",
                    PaletteMode.Layouts => "change the layout of this container",
                    _ => "these keys",
                },
                prefix == '\0' ? ["no prefix", "Tab"] : [$"{prefix}", "Tab"],
                string.Empty,
                Rank: 100,
                SwitchesTo: mode));
        }

        foreach ((string keys, string does) in Keys)
            entries.Add(new PaletteEntry(keys, does, [], string.Empty));

        return entries;
    }

    /// <summary>Every key the palette itself handles.</summary>
    /// <remarks>
    /// Written here rather than in the window that implements them, so the list a
    /// user reads and the keys that actually work are the same text - and so a test
    /// can hold the two together.
    /// </remarks>
    public static IReadOnlyList<(string Keys, string Does)> Keys { get; } =
    [
        ("Tab / Shift+Tab", "next or previous mode"),
        ("Enter", "act on the selected row"),
        ("Escape", "dismiss the palette"),
        ("Up / Down", "move the selection"),
        ("Ctrl+P / Ctrl+N", "move the selection"),
        ("Ctrl+K / Ctrl+J", "move the selection"),
        ("PageUp / PageDown", "move a screenful"),
        ("Ctrl+Home / Ctrl+End", "first or last row"),
        ("Backspace", "delete a character"),
        ("Ctrl+Backspace", "delete a word"),
        ("Ctrl+U", "clear what you typed"),
    ];

    /// <summary>
    /// What to call a window in the list.
    /// </summary>
    /// <remarks>
    /// A window with no title is not necessarily uninteresting - it may be exactly
    /// the one that has gone wrong - so it is named by its class rather than left
    /// blank, which would be an unsearchable and unclickable row.
    /// </remarks>
    private static string Title(WindowCandidate window) =>
        string.IsNullOrWhiteSpace(window.Title)
            ? $"({window.ClassName})"
            : window.Title;

    /// <summary>
    /// The dimmer half of a row: which application, and where it is.
    /// </summary>
    /// <remarks>
    /// Searched as well as shown. Finding a window by its application when the title
    /// says nothing about it - "Untitled document" - is most of what this is for.
    /// </remarks>
    private static string Describe(WindowCandidate window)
    {
        string process = string.IsNullOrEmpty(window.ProcessName) ? window.ClassName : window.ProcessName;

        return window.Workspace is { } workspace
            ? $"{process}  ·  {workspace}"
            : process;
    }

    /// <summary>
    /// Short markers for the state a row is in.
    /// </summary>
    /// <remarks>
    /// Words rather than symbols. A glyph needs a legend, and the palette is where
    /// someone goes when they are already confused about where a window went.
    /// </remarks>
    private static List<string> Badges(WindowCandidate window)
    {
        List<string> badges = [];

        if (!window.Managed) badges.Add("unmanaged");

        // Only when it explains why the window is not on screen. A managed window on
        // an inactive workspace is cloaked by Shubbak itself, which is ordinary and
        // not worth a badge - "on workspace 3" already said it.
        if (window.Concealment is "minimised") badges.Add("minimised");
        else if (window.Concealment is "hidden") badges.Add("hidden");
        else if (window.Concealment is "cloaked" && !window.Managed) badges.Add("cloaked");

        if (window.State is "floating" or "fullscreen" or "monitorfullscreen" or "maximised")
            badges.Add(window.State);

        if (window.Sticky) badges.Add("sticky");
        if (window.Elevated) badges.Add("elevated");

        return badges;
    }
}
