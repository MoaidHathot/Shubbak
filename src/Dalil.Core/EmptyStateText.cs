namespace Dalil.Core;

/// <summary>
/// What the palette says when a list has nothing in it, by why it is empty.
/// </summary>
/// <remarks>
/// <para>
/// One message served every mode: "Nothing to show - the window manager may still be
/// starting up". True of the window list for two seconds after logon and misleading
/// everywhere else. An empty scratchpad is not a window manager that is slow, it is a
/// scratchpad with nothing in it, and the useful sentence is the one that says how to
/// put something there. An empty inspect list is good news - every window is managed
/// - and was being reported as a fault.
/// </para>
/// <para>
/// Pure, so the words can be tested without a window. The three causes are told
/// apart in order: a window manager that cannot be reached is the whole story
/// whatever the mode; a search that found nothing is about the search; and only an
/// unfiltered, connected, empty list is about the mode.
/// </para>
/// </remarks>
public static class EmptyStateText
{
    /// <summary>The headline and the line beneath it.</summary>
    /// <param name="mode">The list that is empty.</param>
    /// <param name="searched">Whether there is a term that filtered everything out.</param>
    /// <param name="connected">Whether the window manager could be reached.</param>
    public static (string Headline, string Hint) For(PaletteMode mode, bool searched, bool connected)
    {
        if (!connected)
        {
            return ("Can't reach the window manager", "Is shubbak-wm running? `shubbak status` will say.");
        }

        if (searched)
        {
            return ("No matches", "Backspace to widen the search, or Tab to look somewhere else");
        }

        return mode switch
        {
            PaletteMode.Windows => (
                "No windows",
                "Nothing is open that the palette lists; ! shows the windows it leaves out"),

            PaletteMode.Scratchpad => (
                "Nothing stashed",
                "`scratchpad` puts the focused window away, and this is where it comes back from"),

            PaletteMode.Inspect => (
                "Every window is managed",
                "A window a rule or a filter left out would be listed here, with the reason"),

            PaletteMode.Workspaces => (
                "No workspaces",
                "Declare them in the config's workspaces block, or type a name and Enter to make one"),

            PaletteMode.Layouts or PaletteMode.Monitors or PaletteMode.Commands => (
                "Nothing to show yet",
                "The window manager has not answered; it may still be starting up"),

            _ => (
                "Nothing to show",
                "The window manager may still be starting up"),
        };
    }
}
