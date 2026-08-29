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
    /// <summary>
    /// How many of the most recently focused windows keep pure recency order.
    /// </summary>
    /// <remarks>
    /// The window list is used for two different things and they want opposite
    /// orderings. Switching between the handful of windows you have been using is
    /// alt-tab, and wants recency, absolutely and without interference. Finding the
    /// one you have lost is a search through everything else, and there recency is
    /// nearly meaningless - it has not been focused, that is why it is lost - so
    /// proximity is the better guess.
    /// <para>
    /// So the top of the list is left exactly as it was and only the tail is
    /// regrouped. Eight is about as many windows as anybody holds in their head at
    /// once, and comfortably more than fit above the fold.
    /// </para>
    /// </remarks>
    public const int RecentlyFocusedCount = 8;

    /// <summary>Ranks reserved for the recently focused, above every proximity tier.</summary>
    private const long RecentBase = 1L << 50;

    /// <summary>How far one proximity tier outranks the next.</summary>
    private const long ProximityStep = 1L << 40;

    /// <summary>How many workspaces a "also on" badge names before it starts counting.</summary>
    /// <remarks>
    /// A window can be tagged onto every workspace on the machine, and the author runs
    /// nineteen of them. Unbounded, the badge is wider than the row and takes the
    /// title with it - so a window that had been made to follow you everywhere became
    /// the one window whose name you could not read.
    /// </remarks>
    private const int NamedTagLimit = 2;

    /// <summary>Describes every window as a row.</summary>
    /// <param name="windows">What <c>query all-windows</c> returned.</param>
    /// <param name="includeUnmanaged">Whether to offer windows Shubbak does not manage.</param>
    /// <param name="focusedWorkspace">
    /// Where "bring it here" means, for the row's actions. Null leaves that action out
    /// rather than offering one that cannot work.
    /// </param>
    /// <param name="workspaces">Every workspace, for the row's pickers.</param>
    /// <param name="severalMonitors">
    /// Whether to say which screen a window is on. Silent on a single display, where
    /// the answer is always the same and would be noise on every row.
    /// </param>
    /// <param name="focusedMonitor">
    /// The display the user is looking at, for ranking. Null ranks by recency alone.
    /// </param>
    public static IReadOnlyList<PaletteEntry> ForWindows(
        IEnumerable<WindowCandidate> windows,
        bool includeUnmanaged = true,
        string? focusedWorkspace = null,
        IReadOnlyList<string>? workspaces = null,
        bool severalMonitors = false,
        string? focusedMonitor = null)
    {
        ArgumentNullException.ThrowIfNull(windows);

        // Ordered once, here, so that "how recent is this" can be answered by position
        // rather than by comparing sequence numbers whose absolute values mean nothing.
        List<WindowCandidate> ordered =
        [
            .. windows
                .Where(w => includeUnmanaged || w.Managed)
                .OrderByDescending(w => w.FocusSequence),
        ];

        List<PaletteEntry> entries = new(ordered.Count);

        for (int i = 0; i < ordered.Count; i++)
        {
            WindowCandidate window = ordered[i];

            entries.Add(new PaletteEntry(
                Title(window),
                Describe(window, severalMonitors),
                Badges(window),

                // Focus is the answer to "where did it go" in every case: for a
                // managed window it switches workspace and raises it, and for one
                // the tree has never heard of the daemon falls through to revealing
                // it - uncloaking, restoring and foregrounding.
                $"focus-window {window.Handle.ToString(CultureInfo.InvariantCulture)}",

                Rank(window, i, focusedWorkspace, focusedMonitor),

                SwitchesTo: null,

                Actions: null,
                Explains: null,
                Expands: null,
                Chord: null,

                // How a mark aims at this window, and how the bulk actions aim at it
                // alongside five others.
                Target: PaletteActions.TargetOf(window),

                IconHandle: window.Handle,
                Destructive: false,
                Unavailable: false,

                // Deferred rather than skipped. The window and the context are captured
                // here, where they are both to hand - working them out later would mean
                // parsing a handle back out of a command string, which is the kind of
                // thing that breaks quietly the day the command format changes - but the
                // dozen records and two workspace-sized pickers are built only for the
                // row somebody actually asks about.
                ActionsFactory: () => PaletteActions.For(window, focusedWorkspace, workspaces)));
        }

        return entries;
    }

    /// <summary>
    /// Where a window sits in the list before anything has been typed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The recently focused keep their exact order and outrank everything. Below them,
    /// windows are grouped by how near they are to what the user is looking at - same
    /// workspace, then same display, then anywhere - and ordered by recency within each
    /// group.
    /// </para>
    /// <para>
    /// Windows that have never been focused carry a sequence of zero and so sort last
    /// within their group rather than being scattered through it, which puts anything
    /// genuinely lost at the bottom of the nearest group, where it is looked for.
    /// </para>
    /// </remarks>
    private static long Rank(
        WindowCandidate window, int recencyIndex, string? focusedWorkspace, string? focusedMonitor)
    {
        if (recencyIndex < RecentlyFocusedCount)
            return RecentBase + (RecentlyFocusedCount - recencyIndex);

        long proximity = 0;

        if (focusedWorkspace is { Length: > 0 } &&
            string.Equals(window.Workspace, focusedWorkspace, StringComparison.OrdinalIgnoreCase))
        {
            proximity = 2;
        }
        else if (focusedMonitor is { Length: > 0 } &&
                 string.Equals(window.Monitor, focusedMonitor, StringComparison.OrdinalIgnoreCase))
        {
            proximity = 1;
        }

        // Clamped so a sequence number can never spill into the tier above it, which
        // would silently undo the grouping for exactly the busiest desktops.
        long recency = Math.Clamp(window.FocusSequence, 0, ProximityStep - 1);

        return (proximity * ProximityStep) + recency;
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
                Actions: null,

                // So Ctrl+Shift+I and the action list both reach the full report from
                // here, exactly as they do from the window list.
                Explains: window.Handle,
                Expands: null,
                Chord: null,
                Target: PaletteActions.TargetOf(window),
                IconHandle: window.Handle,
                Destructive: false,
                Unavailable: false,
                ActionsFactory: () => PaletteActions.For(window, focusedWorkspace, workspaces)));
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

    /// <summary>
    /// Describes every command verb as a row.
    /// </summary>
    /// <param name="commands">What <c>query commands</c> returned.</param>
    /// <param name="status">
    /// What the window manager is currently doing, so that verbs which cannot achieve
    /// anything right now can say so.
    /// </param>
    /// <remarks>
    /// A verb that does not apply is marked rather than hidden. Hiding it would leave
    /// somebody searching for <c>wm-resume</c> and being told by an empty list that no
    /// such command exists - which is both false and the opposite of helpful, given
    /// that the reason they are searching for it is that something has gone wrong.
    /// </remarks>
    public static IReadOnlyList<PaletteEntry> ForCommands(
        IEnumerable<CommandInfo> commands, WmStatus? status = null)
    {
        ArgumentNullException.ThrowIfNull(commands);

        List<PaletteEntry> entries = [];

        foreach (CommandInfo command in commands)
        {
            string? unavailable = status is { } state ? WhyNotNow(command.Verb, state) : null;

            List<string> badges = command.Arguments.Count == 0
                ? []
                : [.. command.Arguments.Select(a => $"<{a}>")];

            if (unavailable is not null) badges.Add("not now");

            entries.Add(new PaletteEntry(
                command.Verb,
                unavailable ?? command.Summary,
                badges,

                // A verb that takes arguments cannot simply be run. Offering it as
                // text to complete is honest; running it with no argument would be
                // rejected by the parser and read as the palette being broken.
                command.Arguments.Count == 0 ? command.Verb : string.Empty,

                // Below everything that would do something, and above nothing. Still
                // findable by name, which is the whole point of leaving it in.
                Rank: unavailable is null ? 0 : -1,
                Unavailable: unavailable is not null));
        }

        return entries;
    }

    /// <summary>Why a verb would achieve nothing in the state the manager is in.</summary>
    /// <remarks>
    /// Only the handful whose applicability the palette can actually know. Guessing at
    /// the rest - whether the focused window is already tiled, whether a container has
    /// siblings to equalise - would need state the palette does not have and would be
    /// wrong often enough to make the marking untrustworthy everywhere.
    /// </remarks>
    private static string? WhyNotNow(string verb, WmStatus status) => verb switch
    {
        "wm-resume" when !status.Suspended =>
            "nothing to resume - the window manager is already running",

        "wm-suspend" when status.Suspended =>
            "already suspended",

        "wm-disable-binding-mode" when status.BindingMode is not { Length: > 0 } =>
            "no binding mode is active",

        _ => null,
    };

    /// <summary>
    /// Describes the user's own named sequences as rows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The answer to keybindings being a scarce resource. There are only so many
    /// chords a person can hold, and every one spent on something done twice a week is
    /// one not spent on something done twice a minute - so the things done twice a
    /// week do not get bound at all, and are then done by hand for ever.
    /// </para>
    /// <para>
    /// A palette entry costs nothing to have and nothing to remember: it is found by
    /// typing an approximation of its name. Twenty of them is a reasonable
    /// configuration; twenty keybindings is not.
    /// </para>
    /// <para>
    /// Ranked above the verbs, because somebody who named a thing is looking for the
    /// thing they named rather than for the primitive it happens to start with.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<PaletteEntry> ForMacros(IEnumerable<PaletteMacro> macros)
    {
        ArgumentNullException.ThrowIfNull(macros);

        return
        [
            .. macros.Select(m => new PaletteEntry(
                m.Name,

                m.Problem is { Length: > 0 } wrong
                    ? wrong
                    : m.Description is { Length: > 0 } said
                        ? said
                        : string.Join("  \u00B7  ", m.Commands),

                m.Problem is { Length: > 0 } ? ["cannot run"] : ["macro"],

                // Nothing to send when it did not parse. Enter does nothing rather
                // than posting something the window manager will only refuse again,
                // one command at a time, into a log nobody is reading.
                m.Problem is { Length: > 0 } ? string.Empty : string.Join('\n', m.Commands),

                Rank: 10,
                Unavailable: m.Problem is { Length: > 0 })),
        ];
    }

    /// <summary>
    /// The things the palette itself can do, which are not window manager verbs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>diagnose</c> is a method on the pipe rather than a command, so it has never
    /// appeared in any command list and could only be reached from a shell. That is
    /// precisely backwards: the report exists to be attached to a bug report, and the
    /// moment somebody wants one is the moment something has just gone wrong on their
    /// desktop - which is when they are looking at their desktop and not at a
    /// terminal.
    /// </para>
    /// <para>
    /// Marked with a scheme the host recognises, so that a row which is not a command
    /// cannot be mistaken for one and sent down the pipe as text.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<PaletteEntry> ForBuiltins() =>
    [
        new PaletteEntry(
            "diagnose",
            "Write a report for a bug tracker: environment, config, the live tree and the log",
            ["dalil"],
            BuiltinDiagnose,
            Rank: 5),
    ];

    /// <summary>The command a row carries when it asks the palette rather than the manager.</summary>
    public const string BuiltinDiagnose = "dalil:diagnose";

    /// <summary>The command that forgets every mark.</summary>
    /// <remarks>
    /// A row rather than a key, because it is needed exactly once per set of marks and
    /// only from the list that is already showing what would happen to them. A key
    /// would be a twenty-ninth thing to write down in the help.
    /// </remarks>
    public const string BuiltinClearMarks = "dalil:clear-marks";

    /// <summary>Whether a command belongs to the palette rather than to the window manager.</summary>
    public static bool IsBuiltin(string? command) =>
        command is { Length: > 0 } && command.StartsWith("dalil:", StringComparison.Ordinal);

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

            bool showing = monitor.ActiveWorkspace is { Length: > 0 };

            entries.Add(new PaletteEntry(
                ShortMonitor(monitor.DeviceId),
                showing
                    ? $"{monitor.Width}\u00D7{monitor.Height}  \u00B7  showing {monitor.ActiveWorkspace}"
                    : $"{monitor.Width}\u00D7{monitor.Height}  \u00B7  nothing on it",
                badges,
                showing ? $"focus --workspace {monitor.ActiveWorkspace}" : string.Empty,
                monitor.Primary ? 1 : 0,

                // A display showing nothing cannot be gone to. Saying so is better
                // than the row silently doing nothing, and far better than what it
                // used to do - fall through to the command box and type the display's
                // own name into it as though it were a verb.
                Unavailable: !showing));
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

        // First, because it is the thing somebody reading this report is on their way
        // to doing. Everything below explains why the window behaves as it does; this
        // is what changes it, already written out, using the two attributes the rest of
        // the report is about to spend twenty lines establishing.
        entries.Add(new PaletteEntry(
            "Write a rule for it",
            "compose",
            ["\u21B5 read it"],
            string.Empty,
            Rank: 1,
            SwitchesTo: null,
            Actions: null,
            Explains: null,
            Expands: RuleComposer.RuleFromReport(
                report.ClassName, report.ProcessName, report.ProcessPath, report.Title)));

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
                DescribeLayout(l),
                string.Equals(l, current, StringComparison.OrdinalIgnoreCase) ? ["in use"] : [],
                $"layout --set {l}",
                string.Equals(l, current, StringComparison.OrdinalIgnoreCase) ? 1 : 0)),
        ];
    }

    /// <summary>
    /// What a layout actually does, in the words somebody would use to want it.
    /// </summary>
    /// <remarks>
    /// The dim half of every one of these rows used to read "layout" - eleven rows, one
    /// word, the same word, next to a list whose heading already said it. That is a
    /// column of the row spent saying nothing, in the one mode where the row's own name
    /// is a piece of jargon: nobody who has not already read the manual knows whether
    /// they want <c>splith</c> or <c>fibonacci-mirrored</c>, and now they do not have
    /// to.
    /// <para>
    /// Searched as well as shown, so typing "stack" or "spiral" finds the layout that
    /// behaves that way rather than only the one that happens to be spelled that way.
    /// </para>
    /// </remarks>
    public static string DescribeLayout(string layout) => layout?.ToLowerInvariant() switch
    {
        "splith" => "side by side, in a row",
        "splitv" => "stacked, in a column",
        "fibonacci" => "spiral - each new window halves the last, starting sideways",
        "fibonacci-v" => "spiral - each new window halves the last, starting downwards",
        "fibonacci-mirrored" => "spiral, wound the other way",
        "master-left" => "one main window on the left, the rest stacked right",
        "master-right" => "one main window on the right, the rest stacked left",
        "master-top" => "one main window on top, the rest below",
        "master-bottom" => "one main window below, the rest on top",
        "grid" => "equal cells, as square as they will go",
        "monocle" => "one window at a time, filling the space",
        _ => "layout",
    };

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
                Actions: null,
                Explains: null,
                Expands: null,
                Chord: null,
                Target: PaletteActions.TargetOf(window),
                IconHandle: window.Handle,
                Destructive: false,
                Unavailable: false,
                ActionsFactory: () => PaletteActions.For(window, focusedWorkspace, workspaces)));
        }

        return entries;
    }

    /// <summary>
    /// The row that answers a question before it is asked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The palette knows something the user is about to go looking for: the window they
    /// were just in is not being managed, and the reason is one query away. Saying so
    /// at the top of the list costs one row and turns a hunt through the inspect mode
    /// into pressing Enter.
    /// </para>
    /// <para>
    /// Only while nothing has been typed. The moment somebody starts searching they
    /// have told the palette what they are looking for, and it is not this - a row
    /// that ignored the query and sat above the results would be the single most
    /// annoying thing in the interface.
    /// </para>
    /// </remarks>
    /// <param name="focused">The window that had focus when the palette opened.</param>
    public static IReadOnlyList<PaletteEntry> ForContext(WindowCandidate? focused)
    {
        if (focused is not { Managed: false } window) return [];

        // A window with nothing identifying about it is usually a shell surface that
        // was never going to be managed and that nobody is asking about.
        if (string.IsNullOrWhiteSpace(window.Title)) return [];

        string reason = Reason(window);

        return
        [
            new PaletteEntry(
                $"\u201C{Title(window)}\u201D is not being managed",
                reason is { Length: > 0 } why ? $"{why} \u2014 Enter for the full story" : "Enter for the full story",
                ["why?"],
                string.Empty,
                Rank: long.MaxValue,
                SwitchesTo: null,
                Actions: null,
                Explains: window.Handle),
        ];
    }

    /// <summary>
    /// Describes the palette's own keys and prefixes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Prefixes are the fastest way to change mode and the least findable thing in the
    /// interface: nobody guesses that <c>~</c> means layouts. Tab makes every mode
    /// reachable without knowing any of them, a digit jumps straight to one, the hint
    /// bar names them permanently, and this is where the whole set is written down.
    /// </para>
    /// <para>
    /// The mode rows are chooseable rather than being text about choosing. Somebody
    /// reading a list of keys will press Enter on the line they want; a help screen
    /// that ignores that has taught them the key and then refused to use it.
    /// </para>
    /// </remarks>
    /// <param name="bindings">The user's own keybindings, which nothing else can show them.</param>
    /// <param name="prefixes">
    /// The prefixes actually in effect, which are the user's to change and so cannot be
    /// read off a constant.
    /// </param>
    public static IReadOnlyList<PaletteEntry> ForHelp(
        IEnumerable<BindingInfo>? bindings = null, PalettePrefixes? prefixes = null)
    {
        PalettePrefixes table = prefixes ?? PalettePrefixes.Default;

        List<PaletteEntry> entries = [];

        for (int i = 0; i < PaletteModel.JumpOrder.Count; i++)
        {
            PaletteMode mode = PaletteModel.JumpOrder[i];
            char prefix = table.PrefixFor(mode);

            List<string> badges = [];
            if (prefix != '\0') badges.Add(prefix.ToString());

            // The jump key, which is the one route into a mode that works on every
            // keyboard layout in the world and needs nothing memorised.
            badges.Add($"Ctrl+{(i + 1).ToString(CultureInfo.InvariantCulture)}");

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
                badges,
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
    /// <para>
    /// Written here rather than in the window that implements them, so the list a user
    /// reads and the keys that actually work are the same text - and so a test can hold
    /// the two together.
    /// </para>
    /// <para>
    /// That test now exists, which it did not when the claim was first made. The list
    /// had already drifted: <c>Ctrl+Shift+C</c> was implemented, documented in the
    /// README and in the changelog, and missing from here; so were all five of the
    /// action chords, every one of which is printed as a badge in the action list it
    /// belongs to. The one page written to be the source of truth was the only place
    /// that did not mention them.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<(string Keys, string Does)> Keys { get; } =
    [
        ("Tab / Shift+Tab", "next or previous mode"),
        ("Ctrl+1 \u2026 Ctrl+8", "jump straight to a mode"),
        ("Enter", "act on the selected row"),

        // Both of these existed and were written down nowhere. The action list is the
        // only route to most of what the palette can do to a window, and inspecting is
        // the thing worth reaching for when a window is behaving oddly - so a user who
        // opens the help looking for either of them was reading the one page that did
        // not mention them.
        ("Ctrl+Enter", "what else can be done to this row, or to everything marked"),
        ("Ctrl+Space", "mark a window, to act on several at once"),

        ("Ctrl+Shift+I", "explain why a window is or is not managed"),
        ("Ctrl+Shift+F", "float the selected window, or tile it"),
        ("Ctrl+Shift+S", "make the selected window sticky, or unstick it"),
        ("Ctrl+Shift+M", "minimise the selected window, or restore it"),
        ("Ctrl+Shift+A", "start managing the selected window, or stop"),
        ("Ctrl+Shift+W", "close the selected window - asks first"),
        ("Alt+Enter", "bring the selected window to this workspace"),

        ("Ctrl+C", "copy the selected line"),
        ("Ctrl+Shift+C", "copy everything on screen"),
        ("Escape", "go back one level, or dismiss the palette"),
        ("Up / Down", "move the selection"),
        ("Ctrl+P / Ctrl+N", "move the selection"),
        ("Ctrl+K / Ctrl+J", "move the selection"),
        ("PageUp / PageDown", "move a screenful"),
        ("Ctrl+Home / Ctrl+End", "first or last row"),
        ("Left / Right", "move the caret"),
        ("Home / End", "start or end of what you typed"),
        ("Backspace", "delete a character, or go back"),
        ("Delete", "delete the character after the caret"),
        ("Ctrl+Backspace", "delete a word"),
        ("Ctrl+U", "clear what you typed, keeping the mode"),
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
    /// Short markers for the state a row is in, most important first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Words rather than symbols. A glyph needs a legend, and the palette is where
    /// someone goes when they are already confused about where a window went.
    /// </para>
    /// <para>
    /// The order is now load-bearing rather than incidental. A row has room for two or
    /// three badges and a window can easily earn six, so some are always going to be
    /// dropped - and the renderer used to drop them from the front of this list, which
    /// is where the important ones are. A window that was unmanaged, minimised,
    /// floating, sticky, tagged onto three workspaces and elevated would show the last
    /// three of those and silently omit the first three, which are the only ones that
    /// explain why it is not where it was left.
    /// </para>
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

        if (window.Elevated) badges.Add("elevated");

        if (window.Sticky) badges.Add("sticky");

        // The workspaces this window will follow the user to. Worth a badge of its own
        // because a window that relocates itself reads as a fault rather than as
        // something that was asked for, and nothing else on screen says otherwise.
        //
        // The workspace it is already on is left out. Tagging records complete
        // membership, so the set always contains where the window currently is, and
        // listing that alongside where it will go is the noisier half of the answer.
        //
        // Last, and counted rather than listed past a couple of names: this is the
        // longest badge a row can earn and the one it can most afford to lose.
        if (FollowsTo(window) is { Count: > 0 } elsewhere) badges.Add(FollowsBadge(elsewhere));

        return badges;
    }

    /// <summary>"also on 3, 4" - and "+5 more" rather than naming all nineteen.</summary>
    private static string FollowsBadge(IReadOnlyList<string> elsewhere)
    {
        if (elsewhere.Count <= NamedTagLimit) return $"also on {string.Join(", ", elsewhere)}";

        string named = string.Join(", ", elsewhere.Take(NamedTagLimit));

        return $"also on {named} +{(elsewhere.Count - NamedTagLimit).ToString(CultureInfo.InvariantCulture)}";
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
