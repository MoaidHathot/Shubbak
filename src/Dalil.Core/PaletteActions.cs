using System.Globalization;
using Shubbak.Config;
using Shubbak.Ipc;

namespace Dalil.Core;

/// <summary>Something that can be done to a row.</summary>
/// <param name="Name">What it is called in the list.</param>
/// <param name="Description">One line explaining it.</param>
/// <param name="Command">What to send; newline-separated for a sequence.</param>
/// <param name="Destructive">
/// Whether doing it by accident would cost something.
/// <para>
/// Read rather than merely recorded. It used to be neither: the field was set, it was
/// asserted in a test, and nothing on screen or in the input path had ever looked at
/// it - so "Close it" was drawn identically to "Float it" and behaved identically to
/// it too, and the only protection against the difference was a global switch that
/// disabled every chord including the harmless ones.
/// </para>
/// </param>
/// <param name="Chord">How it is spelled when pressed directly.</param>
/// <param name="Children">
/// When present, choosing this opens a list of these rather than running anything.
/// Mirrors <see cref="PaletteEntry.SwitchesTo"/>, which already means "this row
/// changes what you are looking at instead of doing something".
/// </param>
/// <param name="Explains">
/// When set, choosing this asks the window manager to describe that window instead of
/// doing anything to it.
/// </param>
/// <param name="Expands">
/// When set, choosing this opens the text rather than running anything - the same
/// route a report row too long for its line already takes. It is how the palette shows
/// something it has composed rather than something it has been sent.
/// </param>
/// <param name="Copies">
/// When set, choosing this puts the text on the clipboard rather than running anything.
/// <para>
/// The step after <paramref name="Expands"/>. A composed rule could be read in the
/// palette and copied from it - with a chord written down only in the documentation -
/// and the row that composed it offered nothing else. This is the row that does the
/// copying by name, so the way to get the rule out is a thing you can see.
/// </para>
/// </param>
/// <param name="Applies">
/// When set, choosing this asks the window manager to add the rule to the configuration
/// file and reload. The one action that writes to the user's file, and it says so in
/// its name; nothing here applies anything by implication.
/// </param>
/// <param name="Removes">
/// When set, choosing this asks the window manager to take the rule out of the
/// configuration file and reload. The other half of <paramref name="Applies"/>, so
/// that whatever the palette added it can also undo.
/// </param>
/// <param name="Composes">
/// When set, choosing this fetches the window's report and opens the rules that could
/// be written for it. A report rather than the row's own attributes, because the
/// choices depend on things only the report knows: whether the filter could be
/// overruled, and which rules already match.
/// </param>
public sealed record PaletteAction(
    string Name,
    string Description,
    string Command,
    bool Destructive = false,
    string? Chord = null,
    IReadOnlyList<PaletteAction>? Children = null,
    long? Explains = null,
    string? Expands = null,
    string? Copies = null,
    RuleToAdd? Applies = null,
    RuleToRemove? Removes = null,
    long? Composes = null);

/// <summary>A rule to be added to the configuration file, when somebody chooses to.</summary>
/// <param name="Kdl">The rule, as the palette showed it.</param>
/// <param name="Handle">The window it was written for, so the answer can say what became of it.</param>
public sealed record RuleToAdd(string Kdl, long? Handle);

/// <summary>A rule to be removed from the configuration file, as a report identified it.</summary>
/// <param name="Name">The rule's name, as the report gave it.</param>
/// <param name="Line">The line it begins on, as the report gave it.</param>
/// <param name="Handle">The window concerned, so the answer can say what became of it.</param>
public sealed record RuleToRemove(string Name, int Line, long? Handle);

/// <summary>
/// What the palette can do to a window, beyond going to it.
/// </summary>
/// <remarks>
/// <para>
/// Built where the handle and the state are both known, rather than reconstructed
/// later from a command string. Parsing a handle back out of <c>focus-window 12345</c>
/// would work and would be the kind of thing that quietly breaks the day the command
/// format changes.
/// </para>
/// <para>
/// Every action is a sequence beginning with <c>focus-window</c>, sent as one
/// newline-separated message. That is what the multi-command pipe is for: the two
/// halves cannot be separated by anything that moves focus in between, which is
/// exactly the race that "focus it, then close it" would otherwise have.
/// </para>
/// <para>
/// State-aware, because an action list that offers to minimise a minimised window is
/// a list nobody trusts. The verbs are toggles underneath; only the wording changes.
/// </para>
/// </remarks>
public static class PaletteActions
{
    /// <summary>The command that aims at one window, whatever kind of window it is.</summary>
    /// <remarks>
    /// A stashed window is cloaked, and focusing a cloaked window reveals it without
    /// unstashing it - so it vanishes again at the next layout pass, which reads as
    /// the palette having failed. Summoning by slot is the only way to reach one, and
    /// it focuses the window itself, so it substitutes for the focus prefix rather
    /// than being an extra step.
    /// <para>
    /// Every action is built on this prefix, so getting it wrong here was never
    /// limited to "Go to it": closing, tagging and un-managing a stashed window were
    /// all aimed at a window that was about to conceal itself again.
    /// </para>
    /// </remarks>
    public static string TargetOf(WindowCandidate window)
    {
        ArgumentNullException.ThrowIfNull(window);

        return window.Scratchpad is { Length: > 0 } slot
            ? $"scratchpad {CommandParser.Quote(slot)}"
            : $"focus-window {window.Handle.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>Everything that can be done to one window.</summary>
    /// <param name="window">The window as the manager described it.</param>
    /// <param name="focusedWorkspace">
    /// Where "bring it here" means. Null when nothing is focused, in which case the
    /// action is not offered rather than being offered and failing.
    /// </param>
    /// <param name="workspaces">
    /// Every workspace, for the tag picker and the move picker. Empty leaves both out
    /// rather than offering a picker with nothing in it.
    /// </param>
    public static IReadOnlyList<PaletteAction> For(
        WindowCandidate window,
        string? focusedWorkspace,
        IReadOnlyList<string>? workspaces = null)
    {
        ArgumentNullException.ThrowIfNull(window);

        bool stashed = window.Scratchpad is { Length: > 0 };
        string focus = TargetOf(window);

        List<PaletteAction> actions = [];

        // "Go to it" is what focusing means for an ordinary window. For a stashed one
        // the same words would describe summoning, which is what the row already does
        // when chosen - so offering it here would be a second copy of the row's own
        // action, worded as though it were something else.
        if (!stashed)
        {
            actions.Add(new PaletteAction(
                "Go to it",
                "Switch to its workspace and raise it",
                focus));
        }
        else
        {
            actions.Add(new PaletteAction(
                "Summon it",
                $"Bring it back from the {window.Scratchpad} slot",
                focus));
        }

        // Not offered for a stashed window: summoning already lands it on whichever
        // workspace is focused, which is what "here" means. Offering it would be the
        // same action twice, the second time as a move that cannot move anything.
        if (!stashed &&
            focusedWorkspace is { Length: > 0 } here &&
            !string.Equals(window.Workspace, here, StringComparison.Ordinal))
        {
            actions.Add(new PaletteAction(
                "Bring it here",
                $"Move it to workspace {here}",
                $"{focus}\nmove --workspace {CommandParser.Quote(here)}",
                Chord: "Alt+Enter"));
        }

        // The other direction, which did not exist.
        //
        // The palette could bring a window here and could tag it onto a workspace, and
        // could not send it to one - despite `move --workspace` being a verb the window
        // manager has always accepted. Tagging is not a substitute: a tag is a
        // membership that makes the window follow you about, which is a different and
        // much stranger thing than putting it somewhere and leaving it there.
        if (!stashed && workspaces is { Count: > 0 })
        {
            List<PaletteAction> destinations = MoveChoices(window, focus, workspaces);

            if (destinations.Count > 0)
            {
                actions.Add(new PaletteAction(
                    "Move it to\u2026",
                    "Send it to another workspace and leave it there",
                    string.Empty,
                    Children: destinations));
            }
        }

        actions.Add(window.Concealment is "minimised" || window.State is "minimised"
            ? new PaletteAction("Restore", "Bring it back from the taskbar", $"{focus}\ntoggle-minimized",
                Chord: "Ctrl+Shift+M")
            : new PaletteAction("Minimise", "Put it away", $"{focus}\ntoggle-minimized",
                Chord: "Ctrl+Shift+M"));

        if (window.Managed)
        {
            // `toggle-floating` from both, exactly as Minimise and Make sticky above
            // send one command each and vary only the wording. Sending `float` or
            // `tile` instead - which this did - makes the row's effect depend on the
            // state it was built from, and that state is a snapshot.
            //
            // Every path here can hand over a stale one. The host seeds a reopened
            // palette from its cached read before the fresh one lands, and refuses to
            // refresh at all while the palette is closed; a drill-in frame is frozen
            // when it is pushed and deliberately ignores refreshes so rows cannot move
            // under the user's finger; and the actions themselves are resolved from a
            // WindowCandidate captured when the row was built. So a window floated a
            // moment ago could still be described as tiled.
            //
            // The consequence was invisible rather than wrong. `float` on a window
            // that is already floating reaches SetWindowStateCore, which returns at
            // once when the state already matches - no event, no error, nothing on
            // screen. The key appeared dead while Enter on the same row worked,
            // because by then the refresh had landed and the row had become the other
            // verb.
            //
            // A toggle cannot be stale. The chord has always been documented as one:
            // the help screen calls it "float the selected window, or tile it".
            actions.Add(window.State is "floating"
                ? new PaletteAction("Tile it", "Put it back into the tiling flow",
                    $"{focus}\ntoggle-floating", Chord: "Ctrl+Shift+F")
                : new PaletteAction("Float it", "Take it out of the tiling flow",
                    $"{focus}\ntoggle-floating", Chord: "Ctrl+Shift+F"));

            actions.Add(window.Sticky
                ? new PaletteAction("Unstick", "Stop showing it on every workspace", $"{focus}\nsticky",
                    Chord: "Ctrl+Shift+S")
                : new PaletteAction("Make sticky", "Show it on every workspace", $"{focus}\nsticky",
                    Chord: "Ctrl+Shift+S"));
        }

        // Only when there is something to clear, and worded as the way out of a state
        // rather than as a feature. A tagged window relocates itself whenever one of
        // its workspaces is activated, which reads as a fault - and until this existed
        // the only way to find the escape hatch was to read your own configuration.
        //
        // Kept at this level rather than inside the picker below. It is the emergency
        // exit for exactly that confusion, and burying it one keystroke deeper would
        // undo the point of having it.
        if (PaletteEntries.FollowsTo(window) is { Count: > 0 } elsewhere)
        {
            actions.Add(new PaletteAction(
                "Stop it following me",
                $"Clear its tags, so it stays put instead of moving to {string.Join(", ", elsewhere)}",
                $"{focus}\ntag --clear"));
        }

        if (window.Managed && workspaces is { Count: > 0 })
        {
            actions.Add(new PaletteAction(
                "Tags\u2026",
                "Choose which workspaces this window follows you to",

                // Opens rather than runs. No chord: a chord that produces another list
                // to choose from is an odd pairing, and tagging is not done in a hurry.
                string.Empty,
                Children: TagChoices(window, focus, workspaces)));
        }

        // Reversible, and therefore not destructive. It was marked so for years and
        // sorted to the bottom beside closing, which is the one action here that
        // genuinely cannot be undone - `toggle-managed` is a toggle, and pressing it
        // twice leaves the desktop exactly as it was found.
        actions.Add(window.Managed
            ? new PaletteAction("Stop managing it", "Leave it where it is and stop tiling it",
                $"{focus}\ntoggle-managed", Chord: "Ctrl+Shift+A")
            : new PaletteAction("Manage it", "Take it under management and tile it",
                $"{focus}\ntoggle-managed", Chord: "Ctrl+Shift+A"));

        // What `shubbak inspect` hands you and then leaves you to type out. The window
        // manager has always known the class and the process; the user has always had
        // to transcribe them into KDL by hand, which is a transcription job with one
        // very easy way to get it silently wrong.
        //
        // Opens a list rather than a rule, because which rule depends on the window:
        // one that is managed wants ignoring, one the filter turned down wants managing,
        // and one a rule already decides wants that rule removed. The report says which,
        // so the report is fetched first - see RuleChoices.
        actions.Add(new PaletteAction(
            "Write a rule for it\u2026",
            "Ignore it, manage it, float it or send it somewhere - as the KDL that would, ready to add",
            string.Empty,
            Composes: window.Handle));

        actions.Add(new PaletteAction(
            "Close it",
            "Ask the window to close",
            $"{focus}\nclose",
            Destructive: true,
            Chord: "Ctrl+Shift+W"));

        // Last, because it is the one that does nothing to the window - and first
        // among the things worth reaching for when a window is behaving oddly, which
        // is why it exists at all. The window manager already assembles this report;
        // until now only the command line could ask for it.
        //
        // Named for the command that produces it. "Explain this window" described it
        // better and was findable only by somebody who had already found it: a user
        // who knows `shubbak inspect` exists and wants it here searches for "inspect",
        // and the description carries the other wording so both spellings hit.
        actions.Add(new PaletteAction(
            "Inspect this window",
            "Explain why it is or is not managed, and which rules matched",
            string.Empty,
            Chord: "Ctrl+Shift+I",
            Explains: window.Handle));

        return actions;
    }

    /// <summary>
    /// Everything that can be done to several windows at once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reason a palette is worth having over a keybinding. Moving six windows to
    /// one workspace by keyboard is six rounds of find-it, focus-it, move-it, with the
    /// focus landing somewhere different after each one; here it is six marks and one
    /// choice. Nothing about it needs a new command in the window manager: the pipe has
    /// always accepted a newline-separated sequence, which is exactly a list of aim
    /// and act repeated.
    /// </para>
    /// <para>
    /// Deliberately a smaller set than the single-window list. An action that reads a
    /// window's state to decide its own wording - minimise or restore, float or tile -
    /// has no honest wording for a mixed selection, so those are offered only as the
    /// underlying toggle where the toggle makes sense for a set, and left out where it
    /// does not.
    /// </para>
    /// </remarks>
    /// <param name="targets">The focus command for each marked window, in the order marked.</param>
    /// <param name="focusedWorkspace">Where "bring them here" means.</param>
    /// <param name="workspaces">Every workspace, for the move picker.</param>
    public static IReadOnlyList<PaletteAction> ForMany(
        IReadOnlyList<string> targets,
        string? focusedWorkspace,
        IReadOnlyList<string>? workspaces = null)
    {
        ArgumentNullException.ThrowIfNull(targets);

        if (targets.Count == 0) return [];

        string many = targets.Count == 1 ? "1 window" : $"{targets.Count} windows";

        List<PaletteAction> actions = [];

        if (focusedWorkspace is { Length: > 0 } here)
        {
            actions.Add(new PaletteAction(
                "Bring them here",
                $"Move {many} to workspace {here}",
                Sequence(targets, $"move --workspace {CommandParser.Quote(here)}")));
        }

        if (workspaces is { Count: > 0 })
        {
            List<PaletteAction> destinations =
            [
                .. workspaces
                    .Where(w => !string.Equals(w, focusedWorkspace, StringComparison.OrdinalIgnoreCase))
                    .Select(w => new PaletteAction(
                        w,
                        $"Send {many} to {w}",
                        Sequence(targets, $"move --workspace {CommandParser.Quote(w)}"))),
            ];

            if (destinations.Count > 0)
            {
                actions.Add(new PaletteAction(
                    "Move them to\u2026",
                    "Send them all to one workspace",
                    string.Empty,
                    Children: destinations));
            }
        }

        actions.Add(new PaletteAction(
            "Float them",
            $"Take {many} out of the tiling flow",
            Sequence(targets, "float")));

        actions.Add(new PaletteAction(
            "Tile them",
            $"Put {many} back into the tiling flow",
            Sequence(targets, "tile")));

        actions.Add(new PaletteAction(
            "Minimise them",
            $"Put {many} away",
            Sequence(targets, "toggle-minimized")));

        actions.Add(new PaletteAction(
            "Close them",
            $"Ask {many} to close",
            Sequence(targets, "close"),
            Destructive: true));

        return actions;
    }

    /// <summary>
    /// What can be done with a rule once it has been composed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three steps between reading the rule and having it in the file, and the first is
    /// the one that takes the other two off the user's hands: the window manager adds
    /// the rule and reloads. It says so in its name, because it is the one row in the
    /// palette that writes to a file somebody maintains by hand, and the answer it
    /// comes back with carries the way to undo it.
    /// </para>
    /// <para>
    /// Copying and opening the file remain, for the user who would rather place the
    /// rule themselves. Both were possible before - Ctrl+Shift+C in the rule frame, and
    /// the path from "config path" into an editor by hand - and neither was written
    /// anywhere on the screen.
    /// </para>
    /// </remarks>
    /// <param name="rule">The composed rule, as <see cref="RuleComposer"/> wrote it.</param>
    /// <param name="handle">The window it was written for, or null.</param>
    /// <param name="complete">
    /// Whether the rule's <c>do</c> block has been decided. An undecided rule is one the
    /// loader would drop, so adding it is not offered - the user finishes it by hand.
    /// </param>
    public static IReadOnlyList<PaletteAction> ForRule(string rule, long? handle = null, bool complete = true)
    {
        ArgumentNullException.ThrowIfNull(rule);

        List<PaletteAction> actions = [];

        if (complete)
        {
            actions.Add(new PaletteAction(
                "Add it to the config and reload",
                "Append the rule to shubbak.kdl, reload, and say what happened to the window",
                string.Empty,
                Applies: new RuleToAdd(rule, handle)));
        }

        actions.Add(new PaletteAction(
            "Copy the rule",
            "Put the whole rule on the clipboard, ready to paste into shubbak.kdl",
            string.Empty,
            Copies: rule));

        // The palette's own command rather than the manager's: the file is the
        // palette's to find, and opening it is the shell's to do.
        actions.Add(new PaletteAction(
            "Open the config",
            "Open shubbak.kdl with whatever edits .kdl files, to paste the rule into",
            PaletteEntries.BuiltinOpenConfig));

        return actions;
    }

    /// <summary>
    /// The rules that could be written for one window, best first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every row is a complete rule - the verb decided, the name saying what it does -
    /// so the common case is Enter to read it and Ctrl+Enter to add it, and nothing to
    /// type. The order is decided by the window's state: a managed window most often
    /// wants ignoring, a window the filter turned down most often wants managing, and
    /// a window a rule already decides most often wants that rule gone. The first row
    /// is the one that inverts what the window is now.
    /// </para>
    /// <para>
    /// Nothing is offered that would do nothing. <c>manage</c> is left out when the
    /// filter's reason cannot be overruled - a cloaked window, a child control - and
    /// when the window is manageable anyway; a rule that looked right and did nothing
    /// is the worst thing this list could hand somebody. When the daemon does not say
    /// whether the reason can be overruled, the rule is offered and its description
    /// says it may not work.
    /// </para>
    /// <para>
    /// The undecided rule is last and always there, for the user who wants the matchers
    /// written and the verb left to them.
    /// </para>
    /// </remarks>
    /// <param name="report">The window, as <c>inspect</c> described it.</param>
    /// <param name="focusedWorkspace">Where the user is, left out of "send it to".</param>
    /// <param name="workspaces">Every workspace, for "send it to". Empty leaves that out.</param>
    public static IReadOnlyList<PaletteAction> RuleChoices(
        WindowReport report,
        string? focusedWorkspace,
        IReadOnlyList<string>? workspaces = null)
    {
        ArgumentNullException.ThrowIfNull(report);

        List<PaletteAction> choices = [];
        long handle = report.Handle;

        // Rules already deciding for this window. Ignoring and forcing count only on
        // the manage trigger, which is the only one where those verbs act.
        List<RuleReport> ignoring = [.. report.Rules.Where(r => r.Matched && OnManage(r) && Does(r, "ignore"))];
        List<RuleReport> forcing = [.. report.Rules.Where(r => r.Matched && OnManage(r) && Does(r, "manage"))];

        foreach (RuleReport rule in ignoring)
        {
            choices.Add(new PaletteAction(
                "Stop ignoring it",
                $"Remove rule \"{rule.Name}\" (line {rule.Line}) from the config and reload",
                string.Empty,
                Removes: new RuleToRemove(rule.Name, rule.Line, handle)));
        }

        foreach (RuleReport rule in forcing)
        {
            choices.Add(new PaletteAction(
                "Stop forcing it",
                $"Remove rule \"{rule.Name}\" (line {rule.Line}) from the config and reload",
                string.Empty,
                Removes: new RuleToRemove(rule.Name, rule.Line, handle)));
        }

        string subject = report.ProcessName is { Length: > 0 } ? report.ProcessName : report.ClassName;
        string verdict = report.VerdictSummary is { Length: > 0 } ? report.VerdictSummary : "the filter turns it down";

        // The filter's opinion, and whether a rule may argue with it. A daemon that
        // predates the field leaves the question open.
        bool overridable = report.Overridable ?? true;
        bool certain = report.Overridable is not null;

        PaletteAction Choice(string name, string description, params string[] does) =>
            Complete(report, name, description, does);

        PaletteAction Ignore(string description) => Choice("Ignore it", description, "ignore");

        if (report.Managed)
        {
            bool floating = string.Equals(report.Node?.State, "Floating", StringComparison.OrdinalIgnoreCase);

            // Managed although the filter says no: forced, by a rule or by hand. A rule
            // is what makes the forcing outlast a reload - unless one already does.
            if (!report.Manageable && forcing.Count == 0 && overridable)
            {
                choices.Add(Choice(
                    "Manage it",
                    $"Keep managing it across reloads - the filter would turn it down: {verdict}",
                    "manage"));
            }

            choices.Add(Ignore("Never tile this window - it is released on the next reload and left alone after that"));

            choices.Add(floating
                ? Choice("Keep it floating", "Float it every time it appears, rather than only until it is closed", "float")
                : Choice("Float it", "Take it out of the tiling flow every time it appears", "float"));

            choices.Add(floating
                ? Choice("Tile it", "Put it into the tiling flow every time it appears", "tile")
                : Choice("Keep it tiled", "Tile it every time it appears, even if the default is to float", "tile"));

            if (Destinations(report.Node?.Workspace, focusedWorkspace, workspaces) is { Count: > 0 } sendTo)
            {
                choices.Add(new PaletteAction(
                    "Send it to workspace\u2026",
                    "Open it on that workspace from now on",
                    string.Empty,
                    Children: [.. sendTo.Select(w => Choice(w, $"Open {subject} on {w} from now on", $"move --workspace \"{RuleComposer.Escape(w)}\""))]));
            }
        }
        else if (report.ExcludedByRule && ignoring.Count == 0)
        {
            // Released by hand, and a reload would take it back. This is what makes the
            // release stick, and it is the row this list exists for.
            choices.Add(Ignore("Make the release stick - a reload would otherwise take it back"));
        }
        else if (report.Manageable)
        {
            // Passes the filter and is not in the tree: not adopted yet, or excluded by
            // a rule listed above. A manage rule would change nothing.
            if (ignoring.Count == 0)
                choices.Add(Ignore("Never tile this window"));
        }
        else if (overridable && ignoring.Count == 0)
        {
            string caveat = certain ? string.Empty : " - if the filter's reason allows it";

            choices.Add(Choice("Manage it", $"Take it on despite the filter: {verdict}{caveat}", "manage"));
            choices.Add(Choice("Manage it, floating", $"Take it on and keep it out of the tiling flow{caveat}", "manage", "float"));

            if (Destinations(null, focusedWorkspace, workspaces) is { Count: > 0 } onto)
            {
                choices.Add(new PaletteAction(
                    "Manage it on workspace\u2026",
                    "Take it on and open it there from now on",
                    string.Empty,
                    Children: [.. onto.Select(w => Choice(w, $"Manage {subject} on {w}{caveat}", "manage", $"move --workspace \"{RuleComposer.Escape(w)}\""))]));
            }

            choices.Add(Ignore("Leave it alone even if the filter changes its mind"));
        }
        else if (ignoring.Count == 0)
        {
            // The filter's reason is a fact, not an opinion - no rule can take this
            // window on, and offering one would look right and do nothing.
            choices.Add(Ignore($"Leave it alone for good - no rule can take it on: {verdict}"));
        }

        // Matched rules that shape rather than decide, offered for removal too.
        foreach (RuleReport rule in report.Rules)
        {
            if (!rule.Matched || ignoring.Contains(rule) || forcing.Contains(rule)) continue;
            if (rule.Does is not { Count: > 0 } does) continue;

            choices.Add(new PaletteAction(
                $"Remove \"{rule.Name}\"",
                $"It runs {string.Join(", ", does)} on this window (line {rule.Line}); remove it from the config and reload",
                string.Empty,
                Removes: new RuleToRemove(rule.Name, rule.Line, handle)));
        }

        // Always last, always there: the matchers written and the verb left open.
        string undecided = RuleComposer.Rule(null, report.ClassName, report.ProcessName, report.Title, null, report.ProcessPath);

        choices.Add(new PaletteAction(
            "Match it, decide later",
            "The matchers written out and the do block left for you - to copy and finish by hand",
            string.Empty,
            Children: ForRule(undecided, handle, complete: false),
            Expands: undecided));

        return choices;
    }

    /// <summary>One complete rule as a row: read on Enter, add or copy from Ctrl+Enter.</summary>
    private static PaletteAction Complete(WindowReport report, string name, string description, string[] does)
    {
        string rule = RuleComposer.Rule(null, report.ClassName, report.ProcessName, report.Title, does, report.ProcessPath);

        return new PaletteAction(
            name,
            description,
            string.Empty,
            Children: ForRule(rule, report.Handle, complete: true),
            Expands: rule);
    }

    /// <summary>Where a rule could send the window: every workspace but the one it is on.</summary>
    private static List<string> Destinations(string? current, string? focused, IReadOnlyList<string>? workspaces)
    {
        if (workspaces is not { Count: > 0 }) return [];

        // Its own workspace is left out, and so is the focused one when the window is
        // not managed - a rule sending it where the user already is would be the
        // default placement written down.
        string? exclude = current ?? focused;

        return [.. workspaces.Where(w => !string.Equals(w, exclude, StringComparison.OrdinalIgnoreCase))];
    }

    private static bool OnManage(RuleReport rule) =>
        rule.Trigger is null || string.Equals(rule.Trigger, "manage", StringComparison.OrdinalIgnoreCase);

    private static bool Does(RuleReport rule, string verb) =>
        rule.Does is { } does && does.Contains(verb, StringComparer.OrdinalIgnoreCase);

    /// <summary>Aim and act, once per window, as one message.</summary>
    /// <remarks>
    /// The window manager stops a sequence at the first failure, which is the right
    /// behaviour here and worth knowing about: a window that closed between being
    /// marked and being acted on stops the rest rather than having the next command
    /// land on whatever now holds the focus.
    /// </remarks>
    private static string Sequence(IReadOnlyList<string> targets, string verb) =>
        string.Join('\n', targets.Select(t => $"{t}\n{verb}"));

    /// <summary>One row per workspace this window could be sent to.</summary>
    /// <remarks>
    /// Its own workspace is left out entirely rather than listed as unavailable. In the
    /// tag picker the current workspace is shown because tagging is about membership
    /// and an incomplete list of memberships would read as a bug; moving is about a
    /// destination, and "move it to where it already is" is not a destination anybody
    /// is choosing between.
    /// </remarks>
    private static List<PaletteAction> MoveChoices(
        WindowCandidate window, string focus, IReadOnlyList<string> workspaces)
    {
        List<PaletteAction> choices = [];

        foreach (string workspace in workspaces)
        {
            if (string.Equals(workspace, window.Workspace, StringComparison.OrdinalIgnoreCase)) continue;

            choices.Add(new PaletteAction(
                workspace,
                $"Send it to {workspace} and leave it there",
                $"{focus}\nmove --workspace {CommandParser.Quote(workspace)}"));
        }

        return choices;
    }

    /// <summary>
    /// One row per workspace, showing where the window stands with each.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Add, remove and toggle all fall out of a single surface, which is why this is
    /// one picker rather than three actions. The command is <c>tag --toggle</c> in
    /// every case; what changes is only the wording, so the row says what Enter will
    /// actually do rather than leaving it to be inferred.
    /// </para>
    /// <para>
    /// The workspace the window already sits on is listed and carries no command. The
    /// window manager refuses that tag outright - it would be a membership that
    /// relocation could never satisfy - so offering it would be offering something
    /// certain to be rejected. Leaving it out entirely would be worse: its absence
    /// from an otherwise complete list reads as a bug rather than as a rule.
    /// </para>
    /// </remarks>
    private static List<PaletteAction> TagChoices(
        WindowCandidate window, string focus, IReadOnlyList<string> workspaces)
    {
        List<PaletteAction> choices = [];

        foreach (string workspace in workspaces)
        {
            bool here = string.Equals(workspace, window.Workspace, StringComparison.OrdinalIgnoreCase);

            bool tagged = window.Tags is { } tags &&
                tags.Any(t => string.Equals(t, workspace, StringComparison.OrdinalIgnoreCase));

            if (here)
            {
                choices.Add(new PaletteAction(workspace, "it is here", string.Empty));
                continue;
            }

            choices.Add(new PaletteAction(
                workspace,

                // Symmetric, and stating the current state before what Enter does.
                // "Enter tags it, so it follows you there" reads as "Enter-tags it"
                // on the way past, which is a bad way to learn a key.
                tagged
                    ? "tagged - Enter removes it"
                    : "not tagged - Enter adds it",
                $"{focus}\ntag --toggle {CommandParser.Quote(workspace)}"));
        }

        return choices;
    }

    /// <summary>Presents actions as rows.</summary>
    /// <remarks>
    /// Ranked in the order they were built rather than alphabetically, so the useful
    /// ones stay at the top and the destructive ones stay at the bottom where a
    /// mistaken Enter is least likely to reach them.
    /// </remarks>
    public static IReadOnlyList<PaletteEntry> AsEntries(IReadOnlyList<PaletteAction> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);

        List<PaletteEntry> entries = [];

        for (int i = 0; i < actions.Count; i++)
        {
            PaletteAction action = actions[i];

            List<string> badges = [];
            if (action.Chord is { } chord) badges.Add(chord);

            // An action that opens another list says so, because Enter on it does
            // something visibly different from Enter on every row beside it.
            //
            // Unless Enter opens its text instead. A row that reads and also carries a
            // list keeps the list behind Ctrl+Enter, and a badge saying "2 ›" beside
            // "↵ read it" would promise Enter a list it is not going to show.
            if (action.Children is { Count: > 0 } && action.Expands is not { Length: > 0 })
                badges.Add($"{action.Children.Count} \u203A");

            entries.Add(new PaletteEntry(
                action.Name,
                action.Description,
                badges,
                action.Command,
                Rank: actions.Count - i,
                SwitchesTo: null,

                // Carried through so the window can push them as the next frame. The
                // action list is itself a list of rows, so a row's children ride in
                // the same place a window row's actions do.
                Actions: action.Children,
                Explains: action.Explains,
                Expands: action.Expands,

                // And the chord, so the badge beside the row is something the row can
                // actually be found by. It used to exist only as that caption, which
                // is why pressing it in the list it was printed in did nothing.
                Chord: action.Chord,

                // Drawn in the warning colour, and confirmed before it happens. The
                // flag was set here and read nowhere, which is how "Close it" came to
                // look and behave exactly like "Float it".
                Destructive: action.Destructive,
                Copies: action.Copies,
                Applies: action.Applies,
                Removes: action.Removes,
                Composes: action.Composes));
        }

        return entries;
    }

    /// <summary>
    /// The two rows that stand between a destructive action and its consequences.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what replaced <c>action-guard</c>. That setting was a single switch over
    /// every chord at once, and its default made every chord in the palette inert
    /// except the one that took no action at all - while the action list went on
    /// printing those chords beside the rows they belonged to. So the keys were
    /// advertised in the one place they were redundant and disabled in the only place
    /// they would have saved anything.
    /// </para>
    /// <para>
    /// Confirming the two actions that cannot be undone, rather than disabling the
    /// eight that can, gets the safety without the cost. Refusing is first and
    /// selected, so the reflex of pressing Enter twice does not close a window.
    /// </para>
    /// </remarks>
    /// <param name="what">The action being confirmed, named as it was in the list.</param>
    /// <param name="command">What to send if it is confirmed.</param>
    public static IReadOnlyList<PaletteEntry> Confirmation(string what, string command) =>
    [
        new PaletteEntry(
            "No, leave it alone",
            "Go back without doing anything",
            [],
            string.Empty,
            Rank: 2),

        new PaletteEntry(
            $"Yes \u2014 {what}",
            "This cannot be undone",
            ["Enter"],
            command,
            Rank: 1,
            Destructive: true),
    ];
}
