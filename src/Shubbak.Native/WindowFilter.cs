using Shubbak.Core.Tree;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Shubbak.Native;

/// <summary>Why a window is not managed.</summary>
public enum ExclusionReason
{
    None,
    NotAWindow,
    NotVisible,
    CloakedByShell,
    CloakedByOwner,
    ChildWindow,
    ToolWindow,
    NotInAltTabList,
    ZeroSized,
    ShellWindow,
    NoTitle,
    ExcludedClass,
    ExcludedProcess,
    Elevated,
}

/// <summary>The verdict for one window, with the reason attached.</summary>
/// <param name="Manageable">Whether Shubbak should tile this window.</param>
/// <param name="Reason">Why not, when <paramref name="Manageable"/> is false.</param>
public readonly record struct ManageDecision(bool Manageable, ExclusionReason Reason)
{
    public static ManageDecision Yes => new(true, ExclusionReason.None);

    public static ManageDecision No(ExclusionReason reason) => new(false, reason);

    /// <summary>A human-readable explanation, used by <c>shubbak inspect</c>.</summary>
    public string Explain() => Reason switch
    {
        ExclusionReason.None => "manageable",
        ExclusionReason.NotAWindow => "handle is not a live window",
        ExclusionReason.NotVisible => "window is not visible",
        ExclusionReason.CloakedByShell =>
            "window is cloaked by the shell - a suspended UWP app, or a window on another Windows virtual desktop",
        ExclusionReason.CloakedByOwner => "window is cloaked because its owner is",
        ExclusionReason.ChildWindow => "window is a child, not top-level",
        ExclusionReason.ToolWindow => "window has WS_EX_TOOLWINDOW and not WS_EX_APPWINDOW",
        ExclusionReason.NotInAltTabList => "window is owned by another window, so it is not an Alt+Tab target",
        ExclusionReason.ZeroSized => "window has no area",
        ExclusionReason.ShellWindow => "window belongs to the shell (desktop or Progman)",
        ExclusionReason.NoTitle => "window has no title",
        ExclusionReason.ExcludedClass => "window class is excluded by default",
        ExclusionReason.ExcludedProcess => "window belongs to a shell process that is excluded by default",
        ExclusionReason.Elevated => "window belongs to an elevated process and Shubbak is not elevated",
        _ => "unknown",
    };
}

/// <summary>
/// Decides which windows Shubbak will tile.
/// </summary>
/// <remarks>
/// <para>
/// This is where tiling window managers on Windows most often feel flaky, because
/// "is this a real application window?" has no single API answer. The checks below
/// are ordered cheapest-first and each one is justified: a window manager that tiles
/// too eagerly reserves screen space for invisible phantoms, and one that tiles too
/// timidly leaves real windows floating.
/// </para>
/// <para>
/// Crucially, every rejection carries a <see cref="ExclusionReason"/>. That is what
/// makes <c>shubbak inspect</c> able to answer "why is this window not being
/// tiled?" - the single most common question a user has, and one neither GlazeWM
/// nor komorebi can answer.
/// </para>
/// </remarks>
public static class WindowFilter
{
    /// <summary>
    /// Processes whose windows are never managed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Shell surfaces that present as ordinary top-level windows. The language and
    /// keyboard-layout switcher raised by Win+Space is the clearest example: it is
    /// hosted by <c>TextInputHost.exe</c>, has a title, is visible, is not a tool
    /// window and passes the Alt+Tab test - so nothing short of knowing the process
    /// excludes it, and tiling it makes the switcher unusable.
    /// </para>
    /// <para>
    /// Matched on the executable name without extension, case-insensitively.
    /// </para>
    /// </remarks>
    private static readonly HashSet<string> s_excludedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "TextInputHost",              // Win+Space language switcher, IME candidates, emoji panel
        "ShellExperienceHost",        // action centre, some flyouts
        "StartMenuExperienceHost",    // Start
        "SearchHost",                 // Windows 11 search
        "SearchApp",                  // Windows 10 search
        "SearchUI",
        "PeopleExperienceHost",
        "LockApp",
        "ShellHost",
        "InputApp",
        "Windows.Internal.ShellExperience",
        "WindowsInternal.ComposableShell.Experiences.TextInput.InputApp",
    };

    /// <summary>
    /// Window classes excluded unconditionally.
    /// </summary>
    /// <remarks>
    /// Shell surfaces that are technically top-level, visible and untitled-or-not,
    /// but are part of the desktop rather than applications. Tiling any of these
    /// breaks the shell in ways that are hard to recover from without a reboot.
    /// </remarks>
    private static readonly HashSet<string> s_excludedClasses = new(StringComparer.Ordinal)
    {
        "Progman",                      // desktop
        "Shell_TrayWnd",                // taskbar
        "Shell_SecondaryTrayWnd",       // taskbar on secondary monitors
        "WorkerW",                      // desktop wallpaper host
        "Windows.UI.Core.CoreWindow",   // UWP shell surfaces
        "ApplicationManager_immersiveApplicationWindow",
        "Windows.Internal.Shell.TabProxyWindow",
        "MultitaskingViewFrame",        // task view
        "ForegroundStaging",
        "XamlExplorerHostIslandWindow", // Alt+Tab and snap assist in Windows 11
        "Windows.UI.Composition.DesktopWindowContentBridge", // XAML flyout host
        "Windows.UI.Input.InputSite.WindowClass",
        "IME",
        "MSCTFIME UI",
        "Default IME",
        "TaskManagerWindow",
        "OleMainThreadWndClass",
        "CicMarshalWndClass",
        "TaskListThumbnailWnd",
        "TaskListOverlayWnd",
        "EdgeUiInputTopWndClass",
        "NarratorHelperWindow",
        "Xaml_WindowedPopupClass",
    };

    /// <summary>
    /// Whether Shubbak should manage this window, and why not if it should not.
    /// </summary>
    /// <param name="handle">Native window handle.</param>
    /// <param name="requireTitle">
    /// Whether an empty title disqualifies a window. On by default: untitled
    /// top-level windows are overwhelmingly splash screens, invisible message-only
    /// helpers and IME candidate hosts, none of which should occupy a tile.
    /// </param>
    /// <param name="concealedAreEligible">
    /// Whether a window that is merely concealed should still be evaluated on its
    /// other merits.
    /// </param>
    /// <remarks>
    /// <para>
    /// <paramref name="concealedAreEligible"/> exists for startup recovery, and only
    /// for that. If Shubbak exits, crashes or is killed while windows are concealed,
    /// those windows are indistinguishable from ones the shell concealed for its own
    /// reasons - a window on another virtual desktop looks exactly like a window
    /// Shubbak cloaked, because in both cases the shell performed the cloak. Ordinary
    /// evaluation must therefore keep rejecting them.
    /// </para>
    /// <para>
    /// The adoption pass sets this so a concealed window can be considered, then
    /// reconciles the survivors against the recorded session and revives only the ones
    /// Shubbak is known to have been managing. Everything else stays untouched.
    /// </para>
    /// </remarks>
    public static ManageDecision Evaluate(
        nint handle, bool requireTitle = true, bool concealedAreEligible = false)
    {
        var hwnd = new HWND(handle);

        if (handle == 0 || !PInvoke.IsWindow(hwnd))
            return ManageDecision.No(ExclusionReason.NotAWindow);

        if (hwnd == PInvoke.GetShellWindow() || hwnd == PInvoke.GetDesktopWindow())
            return ManageDecision.No(ExclusionReason.ShellWindow);

        // Invisible windows are normally not ours to touch. During recovery they are
        // considered, because SW_HIDE is what a fallback concealment leaves behind and
        // a hidden window can be revived no other way.
        if (!concealedAreEligible && !PInvoke.IsWindowVisible(hwnd))
            return ManageDecision.No(ExclusionReason.NotVisible);

        WINDOW_STYLE style = Win32Window.GetStyle(handle);

        if ((style & WINDOW_STYLE.WS_CHILD) != 0)
            return ManageDecision.No(ExclusionReason.ChildWindow);

        WINDOW_EX_STYLE exStyle = Win32Window.GetExStyle(handle);

        // A tool window is a palette or utility surface. WS_EX_APPWINDOW is the
        // documented way for an application to say "despite that, treat me as a
        // normal window", and some legitimate apps rely on it.
        if ((exStyle & WINDOW_EX_STYLE.WS_EX_TOOLWINDOW) != 0 &&
            (exStyle & WINDOW_EX_STYLE.WS_EX_APPWINDOW) == 0)
        {
            return ManageDecision.No(ExclusionReason.ToolWindow);
        }

        string className = Win32Window.GetClassName(handle);
        if (s_excludedClasses.Contains(className))
            return ManageDecision.No(ExclusionReason.ExcludedClass);

        // Checked after the cheap style and class tests, because it costs a process
        // handle - but before the Alt+Tab test, which some of these windows pass.
        if (IsExcludedProcess(handle))
            return ManageDecision.No(ExclusionReason.ExcludedProcess);

        // Cloaking has to be read three ways, not as a boolean.
        //
        // The shell cloaks suspended UWP apps and everything on other Windows virtual
        // desktops - reserving screen space for those produces phantom tiles and steals
        // windows from other desktops, so both are normally rejected. It is also how
        // Shubbak conceals inactive workspaces, because a per-process cloak cannot
        // reach a window owned by another process. A shell cloak is therefore
        // ambiguous, and only the recorded session can resolve it - see
        // <paramref name="concealedAreEligible"/>.
        switch (Win32Window.GetCloakState(handle))
        {
            case Win32Window.CloakState.Shell when !concealedAreEligible:
                return ManageDecision.No(ExclusionReason.CloakedByShell);

            case Win32Window.CloakState.Inherited:
                return ManageDecision.No(ExclusionReason.CloakedByOwner);

            case Win32Window.CloakState.Shell:
            case Win32Window.CloakState.App:
            case Win32Window.CloakState.None:
            default:
                break;
        }

        if (!IsAltTabWindow(hwnd))
            return ManageDecision.No(ExclusionReason.NotInAltTabList);

        if (requireTitle && Win32Window.GetTitle(handle).Length == 0)
            return ManageDecision.No(ExclusionReason.NoTitle);

        // Minimised windows legitimately report a zero or off-screen rectangle, so
        // the size check must not apply to them.
        if (!PInvoke.IsIconic(hwnd) && Win32Window.GetBounds(handle).IsEmpty)
            return ManageDecision.No(ExclusionReason.ZeroSized);

        return ManageDecision.Yes;
    }

    /// <summary>Whether the owning executable is one Shubbak never manages.</summary>
    private static bool IsExcludedProcess(nint handle)
    {
        uint processId = Win32Window.GetProcessId(handle);
        if (processId == 0) return false;

        string? path = Win32Window.GetProcessPath(processId);
        if (path is null) return false;

        return s_excludedProcesses.Contains(Path.GetFileNameWithoutExtension(path));
    }

    /// <summary>
    /// The standard test for "would this window appear in Alt+Tab?".
    /// </summary>
    /// <remarks>
    /// <para>
    /// Documented by Raymond Chen and used by every shell replacement. The idea: a
    /// window belongs in the list only if it is the visible representative of its
    /// owner chain. Walking <c>GetLastActivePopup</c> from the root owner finds that
    /// representative; if it is not this window, then this window is a subordinate
    /// dialog and the owner should be tiled instead.
    /// </para>
    /// <para>
    /// Without it, modal dialogs and their parents both get tiles, so a save prompt
    /// shrinks the document window it belongs to.
    /// </para>
    /// </remarks>
    private static bool IsAltTabWindow(HWND hwnd)
    {
        HWND root = PInvoke.GetAncestor(hwnd, GET_ANCESTOR_FLAGS.GA_ROOTOWNER);

        HWND walk = HWND.Null;
        HWND candidate = root;

        while (candidate != walk)
        {
            walk = candidate;
            candidate = PInvoke.GetLastActivePopup(walk);
            if (PInvoke.IsWindowVisible(candidate)) break;
        }

        return candidate == hwnd;
    }

    /// <summary>
    /// The state a newly discovered window should start in.
    /// </summary>
    /// <remarks>
    /// A window that is already minimised or maximised when Shubbak starts must keep
    /// that state, or starting the window manager would visibly disturb every open
    /// window - the first impression that makes people uninstall.
    /// </remarks>
    public static WindowState InitialStateFor(nint handle, WindowState fallback)
    {
        if (Win32Window.IsMinimised(handle)) return WindowState.Minimised;
        return fallback;
    }
}
