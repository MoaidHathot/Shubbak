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
    CannotActivate,
    OwnedPopup,
    Chromeless,
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
        ExclusionReason.CannotActivate =>
            "window has WS_EX_NOACTIVATE, so clicking it can never give it focus",
        ExclusionReason.OwnedPopup =>
            "window is owned by another window and has no title bar, so it is a menu or popup rather than a window",
        ExclusionReason.Chromeless =>
            "window has neither a title bar nor a resizable frame, so it has one fixed size and nothing to drag - " +
            "a tray flyout or panel rather than a window",
        ExclusionReason.ZeroSized => "window has no area",
        ExclusionReason.ShellWindow => "window belongs to the shell (desktop or Progman)",
        ExclusionReason.NoTitle => "window has no title",
        ExclusionReason.ExcludedClass => "window class is excluded by default",
        ExclusionReason.ExcludedProcess => "window belongs to a shell process that is excluded by default",
        ExclusionReason.Elevated =>
            "window runs at a higher integrity level than Shubbak, so Windows refuses to move it - " +
            "start Shubbak elevated, or use a signed build installed under Program Files with uiAccess",
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
        "ScreenClippingHost",         // Win+Shift+S snipping overlay
        "PeopleExperienceHost",
        "LockApp",
        "ShellHost",
        "InputApp",

        // The credential and Windows Hello prompt. Shipped by komorebi and absent
        // here, which is the gap worth closing: a prompt asking for a PIN or a
        // fingerprint is the last window that should be resized into a tile, and it
        // arrives without warning.
        "CredentialUIBroker",

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

        // Task Manager is deliberately absent from this list, and its absence is the
        // point. It was excluded here by name, which looked like a decision about
        // Task Manager and was really a workaround for a rule that had never been
        // wired up: an unelevated Shubbak cannot position an elevated window at all.
        // That rule now exists in Evaluate, so Task Manager is excluded for the
        // reason it was always being excluded for - and it is managed normally when
        // Shubbak is started elevated, which the name-based version could never do.
        "OleMainThreadWndClass",
        "CicMarshalWndClass",
        "TaskListThumbnailWnd",
        "TaskListOverlayWnd",

        // The Win+Space language switcher. Hosted by explorer, so it cannot be
        // excluded by process without excluding File Explorer too, and it survives
        // long enough that waiting for it to go away does not work either. Only the
        // class identifies it.
        "Shell_InputSwitchTopLevelWindow",

        // The lock screen, which is the same shape of problem. Locking the machine
        // creates these two, and they are hosted by explorer - so, as above, the
        // process cannot be excluded without excluding File Explorer with it.
        //
        // Observed rather than guessed. They were found being adopted as ordinary
        // tiling windows:
        //
        //   managed 0xFF088A "Backstop Window" (explorer)
        //       [LockScreenBackstopFrame] Tiling -> workspace 3
        //   managed 0x7096C "Input Occlusion Window" (explorer)
        //       [LockScreenInputOcclusionFrame] Tiling -> workspace 3
        //
        // They are not transient. That pair stayed in the tree for three and a half
        // hours, from the machine locking to the next time it was unlocked and the
        // windows destroyed, so a workspace that had one real window on it spent the
        // night splitting itself three ways with two invisible participants.
        "LockScreenBackstopFrame",
        "LockScreenInputOcclusionFrame",

        // Office's splash screen, which appears before the application window and is
        // gone by the time anyone could want it tiled. Tiling it rearranges the
        // workspace twice for a window nobody interacts with.
        "MsoSplash",

        "EdgeUiInputTopWndClass",
        "NarratorHelperWindow",
        "Xaml_WindowedPopupClass",

        // The Windows 11 screenshot overlay, which is what ScreenClippingHost above
        // became. Excluded by class rather than by process, because the Snipping Tool
        // also has an ordinary editor window that is perfectly reasonable to tile.
        //
        // It lives for a second or two, and tiling it animated the real windows aside
        // to make room for something that was about to disappear.
        "SnipOverlayRootWindow",
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

        // A window that declines to be activated cannot be focused by clicking it, so
        // a tile holding one is a pane the user can look at and never use. Tiling it
        // also takes space from windows that can be used.
        //
        // Structural rather than named, which is the point: this and the check below
        // reject a shape of window instead of a list of window names. The named lists
        // further down only catch what has already gone wrong for somebody, which is
        // why the lock screen sat in a workspace for three and a half hours before
        // anyone thought to add it.
        //
        // GlazeWM applies the same rule for the same reason. komorebi arrives at a
        // similar place from the other direction, by requiring WS_CAPTION - which is
        // stricter, and which it can afford because it ships a database of two hundred
        // applications to walk back the false positives.
        if ((exStyle & WINDOW_EX_STYLE.WS_EX_NOACTIVATE) != 0)
            return ManageDecision.No(ExclusionReason.CannotActivate);

        // An owned window with no title bar is a menu, an autocomplete popup or a
        // tooltip that happens to be top-level. The Alt+Tab test below catches most
        // owned windows, but not one that is its owner's last active popup - which is
        // exactly what a menu that has just been opened is.
        //
        // Owned windows that do have a title bar are dialogs, and those are wanted:
        // this rejects the shape of a popup, not the fact of having an owner.
        if (Win32Window.HasOwner(handle) && (style & WINDOW_STYLE.WS_CAPTION) == 0)
            return ManageDecision.No(ExclusionReason.OwnedPopup);

        string className = Win32Window.GetClassName(handle);
        if (s_excludedClasses.Contains(className))
            return ManageDecision.No(ExclusionReason.ExcludedClass);

        // The same shape, unowned.
        //
        // A window with neither a title bar nor a resizable frame has declared that it
        // has exactly one size and no frame for the user to drag. Choosing the size is
        // the whole of what a tiling window manager does, so tiling one is a category
        // error: the window is stretched into a tile it was never laid out for, and
        // the tile is taken from windows that would have used it.
        //
        // Reported against Elgato Control Center, whose entire UI is a tray flyout -
        // WPF, no owner, style 0x06080000, WS_EX_LAYERED, title "Elgato Control
        // Center". Every other gate passed it: not a tool window, not WS_EX_NOACTIVATE,
        // unowned so the check above never looked at its missing caption, and a class
        // name of HwndWrapper[ControlCenter.exe;;<guid>] with a fresh guid per
        // instance, so no list of class names could ever have caught it.
        //
        // Deliberately two bits rather than one. Requiring WS_CAPTION alone is what
        // komorebi does, and it rejects every application that draws its own chrome -
        // which is why komorebi then needs a database of two hundred applications to
        // walk the false positives back. Requiring that WS_THICKFRAME also be absent
        // spares them: a window that draws its own title bar still leaves the sizing
        // border on, because it still wants to be resized. Windows Terminal is the
        // case to keep checking, and it survives for exactly that reason. What is left
        // after both bits are missing is the flyout, the HUD and the splash screen.
        //
        // After the class list and before the process id, which is not arbitrary. This
        // rule subsumes most of the shell surfaces the class list names, and placing it
        // first meant Settings and the emoji panel started answering "no title bar"
        // where they had answered "excluded by default" - a true statement and a worse
        // one, since `shubbak inspect` exists to give the most specific reason it can.
        // A class name is a cheap string; the process id below costs a handle, so this
        // still runs before anything expensive.
        if ((style & WINDOW_STYLE.WS_CAPTION) == 0 &&
            (style & WINDOW_STYLE.WS_THICKFRAME) == 0)
        {
            return ManageDecision.No(ExclusionReason.Chromeless);
        }

        // Checked after the cheap style and class tests, because it costs a process
        // handle - but before the Alt+Tab test, which some of these windows pass.
        //
        // One process id answers two questions, so they are asked together.
        uint processId = Win32Window.GetProcessId(handle);
        if (processId != 0)
        {
            // An unelevated Shubbak cannot position an elevated window at all: UIPI
            // refuses the call. Measured, not inferred - SetWindowPos on Task Manager
            // returns false with ERROR_ACCESS_DENIED while the same call on Firefox
            // succeeds.
            //
            // Passing such a window over is better than managing it. Managing it
            // reserves a tile and shrinks its neighbours to make room, and the window
            // never arrives - it stays wherever Windows put it, which can be another
            // monitor entirely, while a gap waits for it on this one. That is exactly
            // how it was reported.
            //
            // Asked of this process first, and only then of the window, because the
            // process answer decides whether the window question is worth asking. A
            // build that can drive higher-integrity windows - elevated, or signed and
            // installed under Program Files with uiAccess in its manifest, which is
            // what GlazeWM ships - skips this gate entirely and manages them like
            // anything else. That is deliberate: the packaged build should need no
            // code change to gain the behaviour, and the check should disappear on
            // its own rather than being a list of application names to maintain.
            if (!Win32Privilege.CanDriveHigherIntegrity && Win32Window.IsElevated(processId))
                return ManageDecision.No(ExclusionReason.Elevated);

            if (Win32Window.GetProcessPath(processId) is { } path &&
                IsExcludedProcessName(Path.GetFileNameWithoutExtension(path)))
            {
                return ManageDecision.No(ExclusionReason.ExcludedProcess);
            }
        }


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

    /// <summary>
    /// Whether a rule, or the user, may overturn this exclusion.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Most of the exclusions are heuristics, and heuristics are wrong sometimes: a
    /// chat client arrives without a title, a call window declares itself a palette,
    /// an ordinary application is owned by an invisible parent and so never appears
    /// in Alt+Tab. Those are exactly the judgements a rule should be able to
    /// overturn, and leaving them unreachable meant editing the source in order to
    /// use an application.
    /// </para>
    /// <para>
    /// The rest are not opinions. A handle that is not a window, the desktop itself,
    /// the shell, and a child control cannot be tiled in any meaningful sense; a
    /// window manager that offered to try would be broken rather than configurable.
    /// </para>
    /// </remarks>
    public static bool CanBeOverridden(ExclusionReason reason) => reason switch
    {
        ExclusionReason.NotAWindow => false,
        ExclusionReason.ShellWindow => false,
        ExclusionReason.ChildWindow => false,

        // A cloaked window is on another virtual desktop or suspended. Managing it
        // would drag it onto this one, which is not what a rule is asking for.
        ExclusionReason.CloakedByShell => false,
        ExclusionReason.CloakedByOwner => false,

        ExclusionReason.NotVisible => false,
        ExclusionReason.ZeroSized => false,

        // The heuristics.
        ExclusionReason.ToolWindow => true,
        ExclusionReason.NotInAltTabList => true,

        // Both are structural, and both are still opinions. An application may set
        // WS_EX_NOACTIVATE and expect to be driven by something other than a click,
        // and an owned frameless window is only usually a menu. A rule saying
        // otherwise knows more than the shape of the window does.
        ExclusionReason.CannotActivate => true,
        ExclusionReason.OwnedPopup => true,

        // The same caveat, and one more reason to keep it overridable: a borderless
        // fullscreen game has the shape of a flyout and is not one. Someone who wants
        // theirs on a workspace says so, and gets it.
        ExclusionReason.Chromeless => true,

        ExclusionReason.NoTitle => true,
        ExclusionReason.ExcludedClass => true,
        ExclusionReason.ExcludedProcess => true,
        ExclusionReason.Elevated => true,

        ExclusionReason.None => true,
        _ => false,
    };

    /// <summary>Whether the owning executable is one Shubbak never manages.</summary>
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

        return InitialStateForClass(Win32Window.GetClassName(handle), fallback);
    }

    /// <summary>The state a window of this class should start in.</summary>
    /// <remarks>
    /// Split from <see cref="InitialStateFor"/> so the policy can be stated and
    /// tested without conjuring a real window of each class - which for a Win32
    /// dialog means driving an application into showing one.
    /// </remarks>
    public static WindowState InitialStateForClass(string className, WindowState fallback) =>
        s_floatingClasses.Contains(className) ? WindowState.Floating : fallback;

    /// <summary>Whether windows owned by this executable are never managed.</summary>
    /// <remarks>Named separately from the handle-based check so it can be tested.</remarks>
    public static bool IsExcludedProcessName(string processName) =>
        s_excludedProcesses.Contains(processName);

    /// <summary>Whether windows of this class are never managed.</summary>
    /// <remarks>Named separately from the handle-based check so it can be tested.</remarks>
    public static bool IsExcludedClassName(string className) =>
        s_excludedClasses.Contains(className);

    /// <summary>
    /// Window classes that start floating rather than tiled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Dialogs. Tiling one is always wrong: it is sized for its content, it is
    /// usually modal, and it is gone in seconds - so tiling it resizes every other
    /// window on the workspace twice for nothing. GlazeWM ships the same two as
    /// built-in defaults rather than leaving them to config, and for the same reason:
    /// nobody would think to write the rule until it had already annoyed them.
    /// </para>
    /// <para>
    /// <c>#32770</c> is the standard Win32 dialog class - Save, Open, Properties, and
    /// most third-party dialogs. <c>OperationStatusWindow</c> is the file copy,
    /// move and delete progress window.
    /// </para>
    /// </remarks>
    private static readonly HashSet<string> s_floatingClasses = new(StringComparer.Ordinal)
    {
        "#32770",
        "OperationStatusWindow",
    };
}
