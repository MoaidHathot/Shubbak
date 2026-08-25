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
