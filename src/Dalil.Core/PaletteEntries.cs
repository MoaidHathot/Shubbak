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
    /// <param name="focusedWorkspace">
    /// Where "bring it here" means, for the row's actions. Null leaves that action out
    /// rather than offering one that cannot work.
    /// </param>
    /// <param name="workspaces">Every workspace, for the row's tag picker.</param>
    /// <param name="severalMonitors">
    /// Whether to say which screen a window is on. Silent on a single display, where
    /// the answer is always the same and would be noise on every row.
    /// </param>
    public static IReadOnlyList<PaletteEntry> ForWindows(
        IEnumerable<WindowCandidate> windows,
        bool includeUnmanaged = true,
        string? focusedWorkspace = null,
        IReadOnlyList<string>? workspaces = null,
        bool severalMonitors = false)
    {
        ArgumentNullException.ThrowIfNull(windows);

        List<PaletteEntry> entries = [];

        foreach (WindowCandidate window in windows)
        {
            if (!includeUnmanaged && !window.Managed) continue;

            entries.Add(new PaletteEntry(
                Title(window),
                Describe(window, severalMonitors),
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
                window.FocusSequence,

                SwitchesTo: null,

                // Built here, where the handle and the state are both to hand. Working
                // them out later would mean parsing a handle back out of a command
                // string, which is the kind of thing that breaks quietly the day the
                // command format changes.
                Actions: PaletteActions.For(window, focusedWorkspace, workspaces)));
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
    /// <remarks>
    /// The layout and the monitor are shown as well as the window count. Both were
    /// already being fetched and thrown away, and both answer a question the list is
    /// otherwise silent about - which workspace is using fibonacci, and which screen
    /// it is on.
    /// </remarks>
    public static IReadOnlyList<PaletteEntry> ForWorkspaces(
        IEnumerable<WorkspaceInfo> workspaces, bool severalMonitors = false)
    {
        ArgumentNullException.ThrowIfNull(workspaces);

        return
        [
            .. workspaces.Select(w => new PaletteEntry(
                string.IsNullOrEmpty(w.DisplayName) ? w.Name : w.DisplayName,
                DescribeWorkspace(w, severalMonitors),
                w.Focused ? ["focused"] : w.Active ? ["displayed"] : [],
                $"focus --workspace {w.Name}",

                // Occupied workspaces first: an empty one is somewhere to go, not
                // something to find.
                w.WindowCount)),
        ];
    }

    private static string DescribeWorkspace(WorkspaceInfo workspace, bool severalMonitors)
    {
        string count = workspace.WindowCount == 1 ? "1 window" : $"{workspace.WindowCount} windows";

        string described = $"{count}  \u00B7  {workspace.Layout}";

        return severalMonitors && ShortMonitor(workspace.Monitor) is { Length: > 0 } screen
            ? $"{described}  \u00B7  {screen}"
            : described;
    }

    /// <summary>
    /// A device id shortened to something a person recognises.
    /// </summary>
    /// <remarks>
    /// <c>\\.\DISPLAY1</c> is what Windows calls a monitor and is mostly punctuation.
    /// Only the tail carries meaning, and only when there is more than one.
    /// </remarks>
    public static string ShortMonitor(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return string.Empty;

        int cut = deviceId.LastIndexOf('\\');

        return cut >= 0 && cut < deviceId.Length - 1 ? deviceId[(cut + 1)..] : deviceId;
    }

    /// <summary>Describes every monitor as a row.</summary>
    /// <remarks>
    /// Choosing one goes to the workspace it is showing, which is the only way to
    /// "focus a monitor" - the window manager has no command that names a display, and
    /// activating what is on it amounts to the same thing.
    /// </remarks>
    public static IReadOnlyList<PaletteEntry> ForMonitors(IEnumerable<MonitorInfoDto> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);

        List<PaletteEntry> entries = [];

        foreach (MonitorInfoDto monitor in monitors)
        {
            List<string> badges = [];
            if (monitor.Primary) badges.Add("primary");
            if (monitor.Dpi != 96) badges.Add($"{monitor.Dpi} dpi");

            entries.Add(new PaletteEntry(
                ShortMonitor(monitor.DeviceId),
                monitor.ActiveWorkspace is { Length: > 0 } showing
                    ? $"{monitor.Width}\u00D7{monitor.Height}  \u00B7  showing {showing}"
                    : $"{monitor.Width}\u00D7{monitor.Height}",
                badges,
                monitor.ActiveWorkspace is { Length: > 0 } target
                    ? $"focus --workspace {target}"
                    : string.Empty,
                monitor.Primary ? 1 : 0));
        }

        return entries;
    }

    /// <summary>
    /// Turns a window manager report into rows.
    /// </summary>
    /// <remarks>
    /// The report is plain text meant for a terminal, laid out in columns padded with
    /// spaces - <c>cloaked      None</c>. Split at that padding it reads as label and
    /// value, which is what the rows want, and as rows it becomes searchable: "cloak"
    /// finds the line about cloaking in a report too long to read through.
    /// <para>
    /// A colon is accepted as a separator too, for the lines that use one. Neither is
    /// required: a line with no separator at all stays whole rather than being forced
    /// into a shape it does not have.
    /// </para>
    /// <para>
    /// Blank lines are dropped. They separate paragraphs on a terminal, and in a list
    /// they would only be empty rows you can select and land on.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<PaletteEntry> ForReport(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        List<PaletteEntry> entries = [];

        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            (string label, string value) = SplitReportLine(trimmed);

            entries.Add(new PaletteEntry(
                value.Length > 0 ? value : label,
                value.Length > 0 ? label : string.Empty,
                [],
                string.Empty,

                // Negative and descending, so the report keeps the order it was
                // written in. It is an argument read top to bottom, and sorting it the
                // way the other lists are sorted would shuffle the reasoning.
                -entries.Count));
        }

        return entries.Count > 0
            ? entries
            : [new PaletteEntry("Nothing to report", string.Empty, [], string.Empty)];
    }

    /// <summary>Splits one report line into its label and its value.</summary>
    private static (string Label, string Value) SplitReportLine(string line)
    {
        for (int i = 0; i < line.Length - 1; i++)
        {
            if (line[i] == ' ' && line[i + 1] == ' ')
            {
                string value = line[i..].TrimStart();

                // A line that is only a label followed by padding has no value, and
                // pretending otherwise would produce a row with an empty title.
                return value.Length > 0 ? (line[..i], value) : (line, string.Empty);
            }

            // Only a colon with something after it separates anything. A line that
            // merely contains one - a path, a window title - stays whole.
            if (line[i] == ':' && i > 0 && line[(i + 1)..].Trim() is { Length: > 0 } after)
                return (line[..i], after);
        }

        return (line, string.Empty);
    }

    /// <summary>Describes every layout as a row.</summary>
    /// <param name="layouts">Names from the layout registry.</param>
    /// <param name="current">
    /// The layout the focused container is using, marked so the list says where you
    /// are as well as where you could go.
    /// </param>
    public static IReadOnlyList<PaletteEntry> ForLayouts(
        IEnumerable<string> layouts, string? current = null)
    {
        ArgumentNullException.ThrowIfNull(layouts);

        return
        [
            .. layouts.Select(l => new PaletteEntry(
                l,
                "layout",
                string.Equals(l, current, StringComparison.OrdinalIgnoreCase) ? ["in use"] : [],
                $"layout --set {l}",
                string.Equals(l, current, StringComparison.OrdinalIgnoreCase) ? 1 : 0)),
        ];
    }

    /// <summary>
    /// Describes the windows put away in a scratchpad.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Retrieved by naming the slot, not by focusing the window. Focusing it would
    /// reveal it and leave it stashed, so it would vanish again at the next layout
    /// pass - which reads as the palette having failed.
    /// </para>
    /// <para>
    /// The slot is the title of the row and the window is the subtitle, because the
    /// slot is the thing a user chose and will half-remember. Somebody who put a
    /// terminal in <c>notes</c> is looking for <c>notes</c>.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<PaletteEntry> ForScratchpad(
        IEnumerable<WindowCandidate> windows,
        string? focusedWorkspace = null,
        IReadOnlyList<string>? workspaces = null)
    {
        ArgumentNullException.ThrowIfNull(windows);

        List<PaletteEntry> entries = [];

        foreach (WindowCandidate window in windows)
        {
            if (window.Scratchpad is not { Length: > 0 } slot) continue;

            entries.Add(new PaletteEntry(
                slot,
                Title(window),
                [.. Badges(window), "stashed"],
                $"scratchpad {slot}",
                window.FocusSequence,
                SwitchesTo: null,
                Actions: PaletteActions.For(window, focusedWorkspace, workspaces)));
        }

        return entries;
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
    public static IReadOnlyList<PaletteEntry> ForHelp(IEnumerable<BindingInfo>? bindings = null)
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
                    PaletteMode.Monitors => "your displays, and what each is showing",
                    PaletteMode.Scratchpad => "windows you have put away",
                    _ => "these keys",
                },
                prefix == '\0' ? ["no prefix", "Tab"] : [$"{prefix}", "Tab"],
                string.Empty,
                Rank: 300,
                SwitchesTo: mode));
        }

        foreach ((string keys, string does) in Keys)
            entries.Add(new PaletteEntry(keys, does, [], string.Empty, Rank: 200));

        // The user's own keybindings, which nothing else can show them. Reading the
        // config file back means applying every for-each expansion by hand, and the
        // expansions are exactly where a binding goes missing.
        //
        // Searchable from either side: typing "palette" finds the key that raises it,
        // and typing "alt" finds everything on the Alt key.
        foreach (BindingInfo binding in bindings ?? [])
        {
            List<string> badges = [];
            if (binding.Mode is { Length: > 0 } mode) badges.Add(mode);
            if (!binding.RepeatsOnHold) badges.Add("no repeat");

            entries.Add(new PaletteEntry(
                binding.Key,
                string.Join(", ", binding.Commands),
                badges,

                // Shown, not run. A keybinding is a name for something; pressing Enter
                // on the line that describes Alt+Q should not close a window.
                string.Empty,
                Rank: 100));
        }

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
    private static string Describe(WindowCandidate window, bool severalMonitors)
    {
        string process = string.IsNullOrEmpty(window.ProcessName) ? window.ClassName : window.ProcessName;

        string described = window.Workspace is { } workspace
            ? $"{process}  ·  {workspace}"
            : process;

        // Only with more than one display. On a single monitor the answer is the same
        // on every row, which is noise rather than information.
        return severalMonitors && ShortMonitor(window.Monitor) is { Length: > 0 } screen
            ? $"{described}  ·  {screen}"
            : described;
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

        // The workspaces this window will follow the user to. Worth a badge of its own
        // because a window that relocates itself reads as a fault rather than as
        // something that was asked for, and nothing else on screen says otherwise.
        //
        // The workspace it is already on is left out. Tagging records complete
        // membership, so the set always contains where the window currently is, and
        // listing that alongside where it will go is the noisier half of the answer.
        if (FollowsTo(window) is { Count: > 0 } elsewhere)
            badges.Add($"also on {string.Join(", ", elsewhere)}");

        if (window.Elevated) badges.Add("elevated");

        return badges;
    }

    /// <summary>The workspaces a window is tagged onto, other than its own.</summary>
    public static IReadOnlyList<string> FollowsTo(WindowCandidate window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (window.Tags is not { Count: > 0 } tags) return [];

        return
        [
            .. tags.Where(t => !string.Equals(t, window.Workspace, StringComparison.OrdinalIgnoreCase)),
        ];
    }
}
