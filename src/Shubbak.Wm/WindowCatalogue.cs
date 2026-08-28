using Shubbak.Core.Tree;
using Shubbak.Core.Wm;
using Shubbak.Ipc;
using Shubbak.Native;

namespace Shubbak.Wm;

/// <summary>
/// Everything on the desktop with a top-level window, managed or not.
/// </summary>
/// <remarks>
/// <para>
/// The tree only contains windows Shubbak manages, which makes it exactly the wrong
/// place to look for a window that has gone missing. A window is most easily lost
/// when nothing is arranging it: excluded by a rule, cloaked by the shell on another
/// virtual desktop, or left cloaked by a daemon that died before it could undo the
/// concealment.
/// </para>
/// <para>
/// The work is split across two threads on purpose, and the split is what the shape
/// of this file is for. Enumerating the desktop touches only Win32 and the static
/// filter, so it runs on the pipe thread that asked. Joining the result with the tree
/// touches state owned by the message loop, so it is marshalled there - and it is a
/// dictionary lookup per window rather than a system call per window.
/// </para>
/// </remarks>
internal static class WindowCatalogue
{
    /// <summary>What the desktop can say about a window without consulting the tree.</summary>
    internal readonly record struct Discovered(
        nint Handle,
        string Title,
        string ClassName,
        string ProcessName,
        uint ProcessId,
        bool Elevated,
        ManageDecision Decision,
        string Concealment);

    /// <summary>
    /// Reads every top-level window. Safe to call off the message loop.
    /// </summary>
    /// <remarks>
    /// Nothing here reads the tree, the registry or the committer's records.
    /// <see cref="Win32Window"/> and <see cref="WindowFilter"/> are stateless readers
    /// over Win32, and <see cref="ProcessIdentityCache"/> takes its own lock.
    /// </remarks>
    public static List<Discovered> Discover()
    {
        IReadOnlyList<nint> handles = Win32Window.EnumerateTopLevel();
        List<Discovered> found = new(handles.Count);

        foreach (nint handle in handles)
        {
            // Evaluated exactly as the daemon evaluates anything else.
            //
            // concealedAreEligible looks tempting here - Shubbak conceals inactive
            // workspaces by cloaking, so its own managed windows read as cloaked -
            // and it is the wrong tool. It exists for startup recovery and says so:
            // it relaxes the visibility test as well as the cloak test, which let
            // every hidden helper window on the desktop through as "manageable". On
            // one ordinary session that was 104 invisible rows out of 119.
            //
            // The tree is the authority on what is managed, and Join applies it
            // afterwards, so nothing is lost by asking the ordinary question here.
            ManageDecision decision = WindowFilter.Evaluate(handle);

            if (decision.Reason is ExclusionReason.NotAWindow) continue;

            string title = Win32Window.GetTitle(handle);
            if (!WorthListing(decision, title)) continue;

            uint processId = Win32Window.GetProcessId(handle);
            string? path = Win32Window.GetProcessPath(processId);

            found.Add(new Discovered(
                handle,
                title,
                Win32Window.GetClassName(handle),
                path is null ? string.Empty : Path.GetFileNameWithoutExtension(path),
                processId,
                Win32Window.IsElevated(processId),
                decision,
                DescribeConcealment(handle)));
        }

        return found;
    }

    /// <summary>
    /// Adds what only the tree knows. Must run on the message loop.
    /// </summary>
    /// <remarks>
    /// The tree is the authority on whether a window is managed, and it overrules the
    /// filter: Shubbak conceals the windows of inactive workspaces itself, so a window
    /// it manages perfectly well reads to the filter as cloaked by the shell. Reporting
    /// the filter's verdict for those would tell the user that every window not
    /// currently on screen had been excluded.
    /// </remarks>
    public static IReadOnlyList<WindowCandidate> Join(
        List<Discovered> discovered, WindowManager wm, WindowRegistry registry)
    {
        WindowNode? focused = wm.FocusedWindow;
        List<WindowCandidate> candidates = new(discovered.Count);

        foreach (Discovered window in discovered)
        {
            registry.TryGet(window.Handle, out WindowNode? node);

            // A hidden window nobody manages is an application's own business - a
            // console the process detached from, a helper parked out of sight. There
            // are a hundred of them on an ordinary desktop and none of them is what
            // anybody has lost.
            //
            // Dropped here rather than during discovery because a hidden window that
            // Shubbak *does* manage is the opposite case: it is concealed by
            // hide-method "hide", it cannot be found any other way, and it is exactly
            // what this query exists for. Only the tree can tell the two apart.
            if (node is null && window.Concealment is "hidden") continue;

            WorkspaceNode? workspace = node?.Workspace;

            candidates.Add(new WindowCandidate(
                window.Handle,
                window.Title,
                window.ClassName,
                window.ProcessName,
                (int)window.ProcessId,
                window.Elevated,
                node is not null,
                node is not null ? null : Explain(window, registry),
                node?.State.ToString().ToLowerInvariant(),
                window.Concealment,
                workspace?.Name,
                node?.IsOnADisplayedWorkspace ?? false,
                workspace?.Monitor?.DeviceId,
                node?.IsSticky ?? false,
                node is not null && ReferenceEquals(node, focused),
                node?.FocusSequence ?? 0,
                node?.ScratchpadName,
                node is { } tagged && tagged.Tags.Count > 0 ? [.. tagged.Tags] : null,
                node is not null ? null : Summarise(window, registry)));
        }

        return candidates;
    }

    /// <summary>
    /// Why a window is not managed, in terms a person can act on.
    /// </summary>
    /// <remarks>
    /// The filter's verdict alone is not always the answer, and saying so plainly
    /// matters more here than anywhere else - this query exists to explain a window's
    /// absence. A window that passes every test and is still not in the tree was
    /// refused by a rule or released by hand, and reporting the filter's cheerful
    /// "manageable" for it is worse than saying nothing.
    /// </remarks>
    private static string Explain(Discovered window, WindowRegistry registry)
    {
        if (registry.IsExcluded(window.Handle))
            return "a rule excluded it, or it was released by hand - toggle-managed takes it back";

        if (window.Decision.Manageable)
            return "Shubbak has not adopted it yet";

        return window.Decision.Explain();
    }

    /// <summary>The same answer in a few words, for a list rather than a report.</summary>
    /// <remarks>
    /// Mirrors <see cref="Explain"/> case for case, including the two answers the
    /// filter cannot give on its own. A client showing the short form in a list and
    /// the long one on demand must be describing the same window both times.
    /// </remarks>
    private static string Summarise(Discovered window, WindowRegistry registry)
    {
        if (registry.IsExcluded(window.Handle)) return "excluded by a rule";

        if (window.Decision.Manageable) return "not adopted yet";

        return window.Decision.Summarise();
    }

    /// <summary>
    /// Whether a window is worth telling a client about.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A real desktop has several hundred top-level windows and almost none of them
    /// are windows in the sense a user means: message-only sinks, IME hosts, tray
    /// owners, and one per shell component. Sending them all would make the payload
    /// twenty times larger than it needs to be and put unsearchable noise in front of
    /// the thing being looked for.
    /// </para>
    /// <para>
    /// A title is the test, because a window with no title cannot be searched for by
    /// name and is not what anybody has lost. Beyond that the exclusions divide into
    /// two kinds: those saying "this is not really a window", which are dropped, and
    /// those saying "this is a window Shubbak chose not to tile", which are exactly
    /// what this query exists to surface.
    /// </para>
    /// </remarks>
    private static bool WorthListing(ManageDecision decision, string title)
    {
        if (title.Length == 0) return false;
        if (decision.Manageable) return true;

        return decision.Reason is
            ExclusionReason.CloakedByShell or
            ExclusionReason.CloakedByOwner or
            ExclusionReason.ExcludedClass or
            ExclusionReason.ExcludedProcess or
            ExclusionReason.Elevated or
            ExclusionReason.Chromeless or
            ExclusionReason.NotVisible;
    }

    /// <summary>How a window is currently kept off screen, if it is.</summary>
    /// <remarks>
    /// Reported as the mechanism rather than as a boolean because the three are not
    /// interchangeable. Minimised is the user's own doing and the taskbar shows it;
    /// cloaked is recoverable and usually Shubbak's or the shell's doing; hidden is
    /// what a crashed window manager leaves behind and is the one a user cannot undo
    /// without help.
    /// </remarks>
    private static string DescribeConcealment(nint handle)
    {
        if (Win32Window.IsMinimised(handle)) return "minimised";
        if (!Win32Window.IsVisible(handle)) return "hidden";

        return Win32Window.GetCloakState(handle) is Win32Window.CloakState.None
            ? "none"
            : "cloaked";
    }
}
