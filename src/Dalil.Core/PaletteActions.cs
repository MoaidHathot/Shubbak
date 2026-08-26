using System.Globalization;
using Shubbak.Ipc;

namespace Dalil.Core;

/// <summary>Something that can be done to a row.</summary>
/// <param name="Name">What it is called in the list.</param>
/// <param name="Description">One line explaining it.</param>
/// <param name="Command">What to send; newline-separated for a sequence.</param>
/// <param name="Destructive">Whether doing it by accident would cost something.</param>
/// <param name="Chord">How it is spelled when direct chords are enabled.</param>
public sealed record PaletteAction(
    string Name,
    string Description,
    string Command,
    bool Destructive = false,
    string? Chord = null);

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
    public static IReadOnlyList<PaletteAction> For(WindowCandidate window, string? focusedWorkspace)
    {
        ArgumentNullException.ThrowIfNull(window);

        string focus = $"focus-window {window.Handle.ToString(CultureInfo.InvariantCulture)}";
        List<PaletteAction> actions = [];

        actions.Add(new PaletteAction(
            "Go to it",
            "Switch to its workspace and raise it",
            focus));

        if (focusedWorkspace is { Length: > 0 } here &&
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
        if (PaletteEntries.FollowsTo(window) is { Count: > 0 } elsewhere)
        {
            actions.Add(new PaletteAction(
                "Stop it following me",
                $"Clear its tags, so it stays put instead of moving to {string.Join(", ", elsewhere)}",
                $"{focus}\ntag --clear"));
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

        return actions;
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

            entries.Add(new PaletteEntry(
                action.Name,
                action.Description,
                badges,
                action.Command,
                Rank: actions.Count - i));
        }

        return entries;
    }
}
