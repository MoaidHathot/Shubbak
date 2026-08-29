using System.Globalization;
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
public sealed record PaletteAction(
    string Name,
    string Description,
    string Command,
    bool Destructive = false,
    string? Chord = null,
    IReadOnlyList<PaletteAction>? Children = null,
    long? Explains = null,
    string? Expands = null);

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
            ? $"scratchpad {slot}"
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
                $"{focus}\nmove --workspace {here}",
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
            actions.Add(window.State is "floating"
                ? new PaletteAction("Tile it", "Put it back into the tiling flow", $"{focus}\ntile",
                    Chord: "Ctrl+Shift+F")
                : new PaletteAction("Float it", "Take it out of the tiling flow", $"{focus}\nfloat",
                    Chord: "Ctrl+Shift+F"));

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
        actions.Add(new PaletteAction(
            "Write a rule for it",
            "Compose the KDL that would match this window, ready to paste",
            string.Empty,
            Expands: RuleComposer.Rule(null, window.ClassName, window.ProcessName, window.Title)));

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
                Sequence(targets, $"move --workspace {here}")));
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
                        Sequence(targets, $"move --workspace {w}"))),
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
                $"{focus}\nmove --workspace {workspace}"));
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
                $"{focus}\ntag --toggle {workspace}"));
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
            if (action.Children is { Count: > 0 }) badges.Add($"{action.Children.Count} \u203A");

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
                Destructive: action.Destructive));
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
