namespace Shubbak.Core.Wm;

/// <summary>
/// Whether an event can change where windows sit.
/// </summary>
/// <remarks>
/// <para>
/// The daemon re-applies the layout from events, and used to do so for every event
/// of every kind. That is correct and wasteful in a way that compounds: a full pass
/// re-arranges the whole tree and reads the position of every visible window, and a
/// pending pass also shortens the message pump's wait from 250 ms to 7 ms <i>and
/// raises the system timer resolution to 1 ms</i>.
/// </para>
/// <para>
/// The timer is the part that reaches beyond Shubbak. It is a machine-wide setting,
/// so a window whose title changes continuously - a playing video, a terminal
/// showing its working directory, a browser cycling adverts - held the whole system
/// at a fine timer indefinitely, for a layout pass that could never move anything.
/// </para>
/// </remarks>
public static class WmEventGeometry
{
    /// <summary>Whether <paramref name="wmEvent"/> can change window geometry.</summary>
    /// <remarks>
    /// <para>
    /// Written as a list of exclusions rather than a list of inclusions, so the
    /// default for anything unrecognised is <c>true</c>. The two mistakes are not
    /// equal: treating an inert event as geometric costs one redundant pass, while
    /// treating a geometric event as inert leaves windows in the wrong place until
    /// something unrelated happens to force a relayout - which is precisely the
    /// defect that <see cref="ContainerResized"/> was added to fix.
    /// </para>
    /// <para>
    /// So a new event kind is dirty until someone proves otherwise, and proving it
    /// means adding a case here and a test beside it.
    /// </para>
    /// </remarks>
    public static bool AffectsGeometry(this WmEvent wmEvent) => wmEvent switch
    {
        // Nothing but the reason a request was declined. The most common event on the
        // desktop by some margin: every repeat of a held key that cannot be satisfied
        // - focusing left from the leftmost window - produces one.
        CommandRejected => false,

        // Only WindowNode.Identity. The layout engine never reads it.
        WindowTitleChanged => false,

        // Routed to the binding table and the log; the tree is untouched.
        BindingModeChanged => false,

        // Carries a path. Reloading may well change the gaps, but the reload path
        // marks the layout dirty itself, having already applied the new options -
        // this event is the announcement to other processes, sent afterwards.
        ConfigReloaded => false,

        _ => true,
    };

    /// <summary>Whether any event in a batch can change window geometry.</summary>
    public static bool AffectGeometry(this IReadOnlyList<WmEvent> events)
    {
        // Indexed rather than foreach: this runs on the tick path, and the enumerator
        // for an IReadOnlyList is an interface call per element that allocates.
        for (int i = 0; i < events.Count; i++)
            if (events[i].AffectsGeometry())
                return true;

        return false;
    }
}
