using System.Globalization;
using Shubbak.Ipc;

namespace Dalil.Core;

/// <summary>Something that can be done to a row.</summary>
/// <param name="Name">What it is called in the list.</param>
/// <param name="Description">One line explaining it.</param>
/// <param name="Command">What to send; newline-separated for a sequence.</param>
/// <param name="Destructive">Whether doing it by accident would cost something.</param>
/// <param name="Chord">How it is spelled when direct chords are enabled.</param>
/// <param name="Children">
/// When present, choosing this opens a list of these rather than running anything.
/// Mirrors <see cref="PaletteEntry.SwitchesTo"/>, which already means "this row
/// changes what you are looking at instead of doing something".
/// </param>
/// <param name="Explains">
/// When set, choosing this asks the window manager to describe that window instead of
/// doing anything to it.
/// </param>
public sealed record PaletteAction(
    string Name,
    string Description,
    string Command,
    bool Destructive = false,
    string? Chord = null,
    IReadOnlyList<PaletteAction>? Children = null,
    long? Explains = null);

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
    /// <summary>Everything that can be done to one window.</summary>
    /// <param name="window">The window as the manager described it.</param>
    /// <param name="focusedWorkspace">
    /// Where "bring it here" means. Null when nothing is focused, in which case the
    /// action is not offered rather than being offered and failing.
    /// </param>
    /// <param name="workspaces">
    /// Every workspace, for the tag picker. Empty leaves the picker out rather than
    /// offering one with nothing in it.
    /// </param>
    public static IReadOnlyList<PaletteAction> For(
        WindowCandidate window,
        string? focusedWorkspace,
        IReadOnlyList<string>? workspaces = null)
    {
        ArgumentNullException.ThrowIfNull(window);

        // A stashed window is cloaked, and focusing a cloaked window reveals it
        // without unstashing it - so it vanishes again at the next layout pass, which
        // reads as the palette having failed. Summoning by slot is the only way to
        // reach one, and it focuses the window itself, so it substitutes for the focus
        // prefix rather than being an extra step.
        //
        // Every action below is built on this prefix, so getting it wrong here was not
        // limited to "Go to it": closing, tagging and un-managing a stashed window
        // were all aimed at a window that was about to conceal itself again.
        bool stashed = window.Scratchpad is { Length: > 0 };

        string focus = stashed
            ? $"scratchpad {window.Scratchpad}"
            : $"focus-window {window.Handle.ToString(CultureInfo.InvariantCulture)}";

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

                // Opens rather than runs. No chord, even when the guard is off: a
                // chord that produces another list to choose from is an odd pairing,
                // and tagging is not done in a hurry.
                string.Empty,
                Children: TagChoices(window, focus, workspaces)));
        }

        // Adopting a window the user's own rule excluded is a decision, not a
        // convenience, which is why it is worded as one and marked.
        actions.Add(window.Managed
            ? new PaletteAction("Stop managing it", "Leave it where it is and stop tiling it",
                $"{focus}\ntoggle-managed", Destructive: true, Chord: "Ctrl+Shift+A")
            : new PaletteAction("Manage it", "Take it under management and tile it",
                $"{focus}\ntoggle-managed", Destructive: true, Chord: "Ctrl+Shift+A"));

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
                Explains: action.Explains));
        }

        return entries;
    }
}
