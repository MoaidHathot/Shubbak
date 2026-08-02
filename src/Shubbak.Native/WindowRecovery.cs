using Shubbak.Core.Diagnostics;
using Shubbak.Core.Tree;
using Shubbak.Core.Wm;

namespace Shubbak.Native;

/// <summary>
/// Finds and revives windows that were left concealed.
/// </summary>
/// <remarks>
/// <para>
/// The escape hatch for the failure that motivated all of this: a window manager which
/// exits without restoring what it hid leaves windows off screen, absent from the
/// taskbar and Alt+Tab, with their processes still running and no way for the user to
/// reach them.
/// </para>
/// <para>
/// Deliberately standalone, and deliberately independent of a running window manager.
/// The situation it exists for is precisely the one where that window manager is dead -
/// a crash, a kill, or a build that concealed windows in a way it could not undo.
/// </para>
/// <para>
/// <b>Concealment is not observable.</b> Windows offers no way to ask who hid a window
/// or why, and a desktop has dozens of windows that applications hide on purpose:
/// message-only helpers, tray hosts, media-key listeners, GDI+ scratch windows. On one
/// ordinary machine a purely structural search matched eighty-two of them, nearly all
/// junk. Reviving those would paper the screen in windows the user never had. So the
/// session recorded by Shubbak - which names the windows it was actually managing - is
/// the only trustworthy evidence, and <see cref="FindRemembered"/> is the supported
/// path. <see cref="FindAll"/> exists for when that record is gone, and its results
/// must be shown to a human before anything is done with them.
/// </para>
/// </remarks>
public static class WindowRecovery
{
    /// <summary>A window that appears to have been left concealed.</summary>
    public readonly record struct Candidate(
        nint Handle, string Title, string ClassName, string ProcessName, string Reason);

    /// <summary>
    /// Finds concealed windows that the session says Shubbak was managing.
    /// </summary>
    /// <remarks>
    /// The safe search, and the one <c>shubbak restore</c> uses by default. A window is
    /// revived only if a remembered entry matches it, so windows an application hid for
    /// its own reasons are never touched.
    /// </remarks>
    public static List<Candidate> FindRemembered(Session session)
    {
        List<Candidate> found = [];
        HashSet<int> claimed = [];

        foreach (nint handle in Concealed())
        {
            WindowIdentity identity = Win32Window.BuildIdentity(handle);

            if (SessionStore.Match(session, identity, claimed) is null) continue;

            found.Add(Describe(handle));
        }

        return found;
    }

    /// <summary>
    /// Finds every concealed window that structurally resembles an application window.
    /// </summary>
    /// <remarks>
    /// The unsafe search. Without a session there is nothing to distinguish a window
    /// Shubbak concealed from one an application hid deliberately, so this returns both
    /// and cannot tell them apart. Never act on it without showing the user the list
    /// first.
    /// </remarks>
    public static List<Candidate> FindAll()
    {
        List<Candidate> found = [];

        foreach (nint handle in Concealed()) found.Add(Describe(handle));

        return found;
    }

    /// <summary>
    /// Finds concealed windows that were cloaked rather than hidden.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The useful middle ground when the session is gone. Applications hide their own
    /// helper windows with <c>SW_HIDE</c>; Shubbak conceals by cloaking. The two are
    /// therefore separable in practice even though neither records who did it - on one
    /// ordinary desktop this told eight real application windows apart from ninety
    /// background helpers.
    /// </para>
    /// <para>
    /// Still a heuristic, not proof. The shell also cloaks windows on other virtual
    /// desktops, and those would be caught here too.
    /// </para>
    /// </remarks>
    public static List<Candidate> FindCloaked()
    {
        List<Candidate> found = [];

        foreach (nint handle in Concealed())
        {
            if (!Win32Window.IsVisible(handle)) continue;

            if (Win32Window.GetCloakState(handle) is not
                (Win32Window.CloakState.App or Win32Window.CloakState.Shell)) continue;

            found.Add(Describe(handle));
        }

        return found;
    }

    private static IEnumerable<nint> Concealed()
    {
        foreach (nint handle in Win32Window.EnumerateTopLevel())
        {
            if (!WindowCommitter.IsConcealed(handle)) continue;

            // Minimising is something users do deliberately, and un-minimising every
            // minimised window would be a bug of its own.
            if (Win32Window.IsMinimised(handle)) continue;

            if (!WindowFilter.Evaluate(handle, concealedAreEligible: true).Manageable) continue;

            yield return handle;
        }
    }

    private static Candidate Describe(nint handle)
    {
        uint processId = Win32Window.GetProcessId(handle);
        string? path = Win32Window.GetProcessPath(processId);

        string reason = !Win32Window.IsVisible(handle)
            ? "hidden"
            : $"cloaked ({Win32Window.GetCloakState(handle).ToString().ToLowerInvariant()})";

        return new Candidate(
            handle,
            Win32Window.GetTitle(handle),
            Win32Window.GetClassName(handle),
            path is null ? "?" : Path.GetFileNameWithoutExtension(path),
            reason);
    }

    /// <summary>Brings the given windows back on screen.</summary>
    /// <returns>How many were still alive to revive.</returns>
    public static int Revive(List<Candidate> candidates)
    {
        int revived = 0;

        foreach (Candidate candidate in candidates)
        {
            if (!Win32Window.Exists(candidate.Handle)) continue;

            WindowCommitter.Revive(candidate.Handle);
            revived++;

            Log.Info(LogCategory.Window,
                $"restored 0x{candidate.Handle:X} \"{candidate.Title}\" ({candidate.Reason})");
        }

        return revived;
    }
}
