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

    /// <summary>
    /// Describes only the windows Shubbak is not managing, and why not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The palette's answer to <c>shubbak inspect --all</c>. The reason is on the row
    /// rather than one keystroke down in an action list, because the question this
    /// mode exists for - "what is being skipped, and why?" - is asked about the set
    /// rather than about any one window, and answering it one window at a time is how
    /// it was asked before.
    /// </para>
    /// <para>
    /// Ranked by how actionable the answer is rather than by recency. A window a rule
    /// excluded, or one that is merely waiting to be adopted, is something the user
    /// can change their mind about; a child window with no area is a fact about Win32.
    /// Recency would be meaningless here anyway - these windows have mostly never been
    /// focused, so it would sort almost everything equally and leave the order to
    /// chance.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<PaletteEntry> ForSkipped(
        IEnumerable<WindowCandidate> windows,
        string? focusedWorkspace = null,
        IReadOnlyList<string>? workspaces = null,
        bool severalMonitors = false)
    {
        ArgumentNullException.ThrowIfNull(windows);

        List<PaletteEntry> entries = [];

        foreach (WindowCandidate window in windows)
        {
            if (window.Managed) continue;

            entries.Add(new PaletteEntry(
                Title(window),
                Describe(window, severalMonitors),
                Badges(window),

                // Focus rather than inspect, because Enter is handled by the palette:
                // in this mode it opens the report instead of sending this. The
                // command is still here so that the row remains useful if it is ever
                // shown somewhere that does not know the mode.
                $"focus-window {window.Handle.ToString(CultureInfo.InvariantCulture)}",
                Actionable(window),
                SwitchesTo: null,
                Actions: PaletteActions.For(window, focusedWorkspace, workspaces),

                // So Ctrl+Shift+I and the action list both reach the full report from
                // here, exactly as they do from the window list.
                Explains: window.Handle));
        }

        return entries;
    }

    /// <summary>How much can be done about the reason a window was skipped.</summary>
    /// <remarks>
    /// Three tiers rather than a score. Something the user turned on, something
    /// Shubbak has simply not got to yet, and something about the window itself - and
    /// only the first two are worth looking at first.
    /// </remarks>
    private static long Actionable(WindowCandidate window) =>
        Reason(window) switch
        {
            "excluded by a rule" => 2,
            "not adopted yet" => 1,
            _ => 0,
        };

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
    /// <para>
    /// One row per fact, label on the dim side and value on the searched side, so a
    /// report too long to read through can be narrowed by typing: "cloak" finds the
    /// line about cloaking, "rule" finds the rules.
    /// </para>
    /// <para>
    /// Built from the report's fields. It used to be built by splitting the printed
    /// text at its column padding, which worked and meant the daemon's choice of
    /// whitespace was quietly an interface - widen a column and the labels here stop
    /// being labels, with nothing to notice it.
    /// </para>
    /// <para>
    /// Every row carries its whole text in <see cref="PaletteEntry.Expands"/>, because
    /// a row is drawn on one line and clipped. The values worth opening a report for -
    /// a path, a regular expression, the sentence saying a window cannot be moved and
    /// what to do about it - are exactly the ones too long to fit.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<PaletteEntry> ForReport(WindowReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        List<PaletteEntry> entries = [];

        void Add(string label, string value) => entries.Add(new PaletteEntry(
            value,
            label,
            [],
            string.Empty,

            // Negative and descending, so the report keeps the order it was written
            // in. It is an argument read top to bottom, and sorting it the way the
            // other lists are sorted would shuffle the reasoning.
            -entries.Count,
            SwitchesTo: null,
            Actions: null,
            Explains: null,
            Expands: $"{label}  {value}"));

        Add("handle", $"0x{report.Handle:X}");
        Add("title", report.Title);
        Add("class", report.ClassName);
        Add("process", report.ProcessName);
        Add("path", report.ProcessPath ?? "(unreadable - elevated process?)");
        Add("rect", $"({report.X},{report.Y} {report.Width}x{report.Height})");
        Add("style", $"0x{report.Style:X8}");
        Add("ex-style", $"0x{report.ExStyle:X8}");
        Add("visible", report.Visible ? "yes" : "no");
        Add("cloaked", report.Cloaked);
        Add("minimised", report.Minimised ? "yes" : "no");
        Add("manageable", $"{(report.Manageable ? "yes" : "no")} - {report.Verdict}");

        if (report.Node is { } node)
        {
            Add("managed", "yes");
            Add("node", $"#{node.Id}");
            Add("state", node.State);
            Add("workspace", node.Workspace);
            Add("focused", node.Focused ? "yes" : "no");
            Add("sticky", node.Sticky ? "yes - follows every workspace on its monitor" : "no");
            Add("tags", node.Tags.Count == 0
                ? "(none)"
                : $"{string.Join(", ", node.Tags)} - it will follow you there");

            if (node.Scratchpad is { Length: > 0 } slot) Add("scratchpad", slot);
        }
        else
        {
            Add("managed", report.ExcludedByRule ? "no - excluded by a rule" : "no");
        }

        // Only the rules, not a heading followed by them. A heading is a row you can
        // select and land on that says nothing, and the label column already carries
        // the word "rule" on every line beneath it.
        if (report.Rules.Count == 0)
        {
            Add("rules", "(none configured)");
        }
        else
        {
            foreach (RuleReport rule in report.Rules)
                Add(rule.Matched ? "rule  [x]" : "rule  [ ]", $"{rule.Name}  (line {rule.Line})");
        }

        // What turns "my rule does not fire" into a one-glance diagnosis: the rule is
        // usually fine, and the app definition is what missed.
        foreach (AppReport app in report.Apps)
        {
            Add(app.Matched ? "app  [x]" : "app  [ ]", app.Name);

            foreach (string matcher in app.FailedMatchers)
                Add("failed", matcher);
        }

        return entries;
    }

    /// <summary>One line saying why there is no report.</summary>
    /// <remarks>
    /// A frame of its own rather than an empty list, because Push refuses an empty one
    /// and the palette would then answer a request to explain a window by appearing to
    /// do nothing at all.
    /// </remarks>
    public static IReadOnlyList<PaletteEntry> ForReportFailure(string reason) =>
    [
        new PaletteEntry(
            string.IsNullOrWhiteSpace(reason) ? "Nothing to report" : reason,
            string.Empty,
            [],
            string.Empty,
            Expands: reason),
    ];

    /// <summary>
    /// Breaks one long value across as many rows as it needs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is how the palette shows something too long to fit without learning to
    /// wrap. A row is a fixed height and one clipped line, by design - the layout is
    /// arithmetic and the renderer draws single lines - so instead of making a row
    /// taller, the text becomes several rows, and the frame they are pushed into is
    /// the one that already exists for action lists and reports. Escape leaves it the
    /// way Escape leaves everything else.
    /// </para>
    /// <para>
    /// Measured rather than counted. Segoe UI is proportional, so wrapping at a
    /// character count leaves a ragged half-empty column on one line and clips the
    /// next. The measurer is passed in because the width of a string is a question
    /// only the renderer can answer, and this has to stay testable without one.
    /// </para>
    /// </remarks>
    /// <param name="text">The whole value.</param>
    /// <param name="width">Pixels available to a row.</param>
    /// <param name="measure">How wide a string would be drawn.</param>
    public static IReadOnlyList<PaletteEntry> ForWrapped(
        string text, int width, Func<string, int> measure)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(measure);

        List<PaletteEntry> entries = [];

        foreach (string line in Wrap(text, width, measure))
        {
            entries.Add(new PaletteEntry(
                line,
                string.Empty,
                [],
                string.Empty,

                // Descending, so the lines stay in the order they were written. A
                // sentence sorted by rank is not a sentence.
                -entries.Count));
        }

        return entries.Count > 0
            ? entries
            : [new PaletteEntry(string.Empty, string.Empty, [], string.Empty)];
    }

    /// <summary>Greedy word wrap, falling back to breaking a word that cannot fit.</summary>
    private static List<string> Wrap(string text, int width, Func<string, int> measure)
    {
        List<string> lines = [];

        foreach (string paragraph in text.Split('\n'))
        {
            string remaining = paragraph.TrimEnd('\r');

            if (remaining.Length == 0)
            {
                lines.Add(string.Empty);
                continue;
            }

            // A width that cannot hold anything would loop for ever asking for a
            // smaller prefix. One line whole is a worse answer than wrapping and a
            // much better one than hanging.
            if (width <= 0)
            {
                lines.Add(remaining);
                continue;
            }

            while (remaining.Length > 0)
            {
                if (measure(remaining) <= width)
                {
                    lines.Add(remaining);
                    break;
                }

                int cut = LongestFit(remaining, width, measure);

                // Prefer the last space inside what fits, so words stay whole. Only
                // when there is one: a path or a regular expression has none, and
                // those are exactly the values worth opening in full.
                int space = remaining.LastIndexOf(' ', Math.Min(cut, remaining.Length - 1));

                if (space > 0)
                {
                    lines.Add(remaining[..space]);
                    remaining = remaining[(space + 1)..];
                }
                else
                {
                    lines.Add(remaining[..cut]);
                    remaining = remaining[cut..];
                }
            }
        }

        return lines;
    }

    /// <summary>The longest prefix that still fits, and never fewer than one character.</summary>
    /// <remarks>
    /// Linear from the front rather than a binary search. The strings are one row wide
    /// - tens of characters, not thousands - and this runs when a person presses Enter
    /// rather than on every frame.
    /// </remarks>
    private static int LongestFit(string text, int width, Func<string, int> measure)
    {
        int fits = 1;

        for (int length = 1; length <= text.Length; length++)
        {
            if (measure(text[..length]) > width) break;

            fits = length;
        }

        return fits;
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
                    PaletteMode.Inspect => "windows Shubbak is not managing, and why not",
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

        // Both of these existed and were written down nowhere. The action list is the
        // only route to most of what the palette can do to a window, and inspecting is
        // the thing worth reaching for when a window is behaving oddly - so a user who
        // opens the help looking for either of them was reading the one page that did
        // not mention them.
        ("Ctrl+Enter", "what else can be done to this row"),
        ("Ctrl+Shift+I", "explain why a window is or is not managed"),
        ("Ctrl+C", "copy the selected line"),
        ("Escape", "dismiss the palette"),
        ("Up / Down", "move the selection"),
        ("Ctrl+P / Ctrl+N", "move the selection"),
        ("Ctrl+K / Ctrl+J", "move the selection"),
        ("PageUp / PageDown", "move a screenful"),
        ("Ctrl+Home / Ctrl+End", "first or last row"),
        ("Backspace", "delete a character, or go back"),
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
    /// <para>
    /// Searched as well as shown. Finding a window by its application when the title
    /// says nothing about it - "Untitled document" - is most of what this is for.
    /// </para>
    /// <para>
    /// For a window Shubbak is not managing, why not. The window manager has always
    /// sent this and the palette has always thrown it away, so the list could say a
    /// window was unmanaged but never why - leaving the reason one keystroke down in
    /// an action list nobody knew was there. It takes the place of the workspace,
    /// which an unmanaged window does not have, so no row grows to carry it.
    /// </para>
    /// <para>
    /// The short form, because this line is clipped rather than wrapped. The full
    /// sentence - with the part that says what to do about it - is what Inspect
    /// shows.
    /// </para>
    /// </remarks>
    private static string Describe(WindowCandidate window, bool severalMonitors)
    {
        string process = string.IsNullOrEmpty(window.ProcessName) ? window.ClassName : window.ProcessName;

        string described = window.Workspace is { } workspace
            ? $"{process}  ·  {workspace}"
            : window.Managed
                ? process
                : Reason(window) is { Length: > 0 } why
                    ? $"{process}  ·  {why}"
                    : process;

        // Only with more than one display. On a single monitor the answer is the same
        // on every row, which is noise rather than information.
        return severalMonitors && ShortMonitor(window.Monitor) is { Length: > 0 } screen
            ? $"{described}  ·  {screen}"
            : described;
    }

    /// <summary>
    /// Why a window is not managed, in as few words as the manager can put it.
    /// </summary>
    /// <remarks>
    /// Falls back to the long form when the short one is absent, which is what an
    /// older window manager sends: a clipped sentence still says more than nothing,
    /// and it is still searchable in full whatever the row has room to draw.
    /// </remarks>
    public static string Reason(WindowCandidate window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (window.Managed) return string.Empty;

        return window.ExclusionSummary is { Length: > 0 } summary
            ? summary
            : window.ExclusionReason ?? string.Empty;
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
