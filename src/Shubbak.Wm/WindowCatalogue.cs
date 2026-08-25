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
            // Concealed windows are eligible, because a concealed window is the
            // single most likely thing to be looking for. Asking the filter its
            // ordinary question would reject every window on an inactive workspace -
            // the ones Shubbak itself has hidden.
            ManageDecision decision = WindowFilter.Evaluate(handle, concealedAreEligible: true);

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

            WorkspaceNode? workspace = node?.Workspace;

            candidates.Add(new WindowCandidate(
                window.Handle,
                window.Title,
                window.ClassName,
                window.ProcessName,
                (int)window.ProcessId,
                window.Elevated,
                node is not null,
                node is not null ? null : window.Decision.Explain(),
                node?.State.ToString().ToLowerInvariant(),
                window.Concealment,
                workspace?.Name,
                node?.IsOnADisplayedWorkspace ?? false,
                workspace?.Monitor?.DeviceId,
                node?.IsSticky ?? false,
                node is not null && ReferenceEquals(node, focused),
                node?.FocusSequence ?? 0));
        }

        return candidates;
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
