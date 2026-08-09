using Shubbak.Core.Tree;

namespace Shubbak.Native.Tests;

/// <summary>
/// Tests for the windows Shubbak refuses to tile without being told to.
/// </summary>
/// <remarks>
/// <para>
/// Dialogs and shell surfaces need handling out of the box. Nobody writes a rule for
/// the file-copy progress window until it has already thrown their layout around, and
/// by then they have formed a view about whether the window manager is any good.
/// GlazeWM ships the same defaults for the same reason.
/// </para>
/// <para>
/// These are the defaults, not a policy anyone can reach: they exist because a
/// tiling window manager that tiles a Save dialog is broken on arrival.
/// </para>
/// </remarks>
public sealed class DefaultWindowRuleTests
{
    [Theory]
    [InlineData("#32770")]                  // the standard Win32 dialog class
    [InlineData("OperationStatusWindow")]   // file copy, move and delete progress
    public void DialogClassesStartFloating(string className)
    {
        Assert.Equal(
            WindowState.Floating,
            WindowFilter.InitialStateForClass(className, WindowState.Tiling));
    }

    [Fact]
    public void OrdinaryWindowsStartTiled()
    {
        Assert.Equal(
            WindowState.Tiling,
            WindowFilter.InitialStateForClass("MozillaWindowClass", WindowState.Tiling));
    }

    [Fact]
    public void TheConfiguredDefaultIsHonouredForOrdinaryWindows()
    {
        // Floating everything is a legitimate choice, and the dialog rule must not
        // quietly override the opposite preference either.
        Assert.Equal(
            WindowState.Floating,
            WindowFilter.InitialStateForClass("MozillaWindowClass", WindowState.Floating));
    }

    [Theory]
    [InlineData("SearchHost")]                 // Windows 11 search
    [InlineData("SearchApp")]                  // Windows 10 search
    [InlineData("StartMenuExperienceHost")]    // Start
    [InlineData("ShellExperienceHost")]        // action centre and flyouts
    [InlineData("ScreenClippingHost")]         // Win+Shift+S
    [InlineData("LockApp")]
    [InlineData("TextInputHost")]              // IME candidates, emoji panel
    public void ShellProcessesAreNeverManaged(string processName)
    {
        Assert.True(
            WindowFilter.IsExcludedProcessName(processName),
            $"{processName} should never be tiled; it is part of the shell, not an application.");
    }

    [Fact]
    public void OrdinaryProcessesAreNotExcluded()
    {
        Assert.False(WindowFilter.IsExcludedProcessName("firefox"));
        Assert.False(WindowFilter.IsExcludedProcessName("explorer"));
    }

    [Fact]
    public void ExplorerIsNotExcluded()
    {
        // Stated on its own because it is tempting. The Win+Space language switcher
        // is titled "Input Flyout" and hosted by explorer, so excluding the process
        // would take File Explorer with it.
        Assert.False(WindowFilter.IsExcludedProcessName("explorer"));
    }

    [Fact]
    public void TaskManagerIsNotExcludedByName()
    {
        // It used to be, which read as a decision about Task Manager and was really a
        // workaround for a rule nobody had written: an unelevated Shubbak cannot
        // position an elevated window, and Task Manager is elevated.
        //
        // Measured, not assumed - SetWindowPos on it returns ERROR_ACCESS_DENIED
        // while the same call on Firefox succeeds. Managing it anyway reserved a tile
        // and shrank its neighbours to make room for a window that never arrived.
        //
        // Excluding by name got the outcome right for one application and the reason
        // wrong for all of them, and it could never be undone by running elevated.
        // The rule lives in Evaluate now; this only pins that the name is gone.
        Assert.False(WindowFilter.IsExcludedClassName("TaskManagerWindow"));
    }

    [Fact]
    public void AnElevatedWindowSaysWhyItCannotBeManaged()
    {
        // The message a user sees from `shubbak inspect` when a window will not tile
        // and there is nothing wrong with the window. Without it the symptom is a gap
        // in the layout with no explanation anywhere.
        Assert.Contains(
            "elevated",
            ManageDecision.No(ExclusionReason.Elevated).Explain(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnElevatedWindowCanBeOverridden()
    {
        // A rule may still say `manage`. Shubbak will fail to move it, but that is
        // the user's call to make and the alternative is silently refusing.
        Assert.True(WindowFilter.CanBeOverridden(ExclusionReason.Elevated));
    }

    [Fact]
    public void TheShellSurfacesAroundItAreStillExcluded()
    {
        // The neighbours it was removed from, asserted so that removing one entry
        // cannot quietly take the list with it.
        Assert.True(WindowFilter.IsExcludedClassName("IME"));
        Assert.True(WindowFilter.IsExcludedClassName("MSCTFIME UI"));
        Assert.True(WindowFilter.IsExcludedClassName("OleMainThreadWndClass"));
        Assert.True(WindowFilter.IsExcludedClassName("TaskListThumbnailWnd"));
    }

    [Fact]
    public void TheLanguageSwitcherIsExcludedByClass()
    {
        // The only thing that identifies it. It is hosted by explorer, so process
        // matching would take File Explorer with it, and it lives long enough that
        // waiting for it to disappear does not work either - a settling delay of
        // 150ms let it through, and lengthening that would make every window the
        // user opens visibly late.
        Assert.True(WindowFilter.IsExcludedClassName("Shell_InputSwitchTopLevelWindow"));
    }

    [Fact]
    public void TheLockScreenIsNotAWindowToTile()
    {
        // Locking the machine creates two of these, and they are hosted by explorer -
        // so, like the language switcher above, the process cannot be excluded without
        // excluding File Explorer with it.
        //
        // Found in a log rather than reasoned about:
        //
        //   managed 0xFF088A "Backstop Window" (explorer)
        //       [LockScreenBackstopFrame] Tiling -> workspace 3
        //   managed 0x7096C "Input Occlusion Window" (explorer)
        //       [LockScreenInputOcclusionFrame] Tiling -> workspace 3
        //
        // And they are not transient. That pair sat in the tree for three and a half
        // hours, from the machine locking until it was next unlocked, so a workspace
        // holding one real window spent the night splitting itself three ways with two
        // invisible participants.
        Assert.True(WindowFilter.IsExcludedClassName("LockScreenBackstopFrame"));
        Assert.True(WindowFilter.IsExcludedClassName("LockScreenInputOcclusionFrame"));
    }

    [Fact]
    public void AWindowThatDeclinesActivationIsNotTiled()
    {
        // Structural, not named. A window carrying WS_EX_NOACTIVATE never becomes the
        // foreground window when clicked, so a tile holding one is a pane the user can
        // look at and never use - while taking space from windows they can.
        //
        // This is the kind of rule the named lists cannot express. A blocklist only
        // catches what has already gone wrong for somebody, which is why the lock
        // screen occupied a workspace for three and a half hours before anyone thought
        // to add its class.
        using var window = new TestWindow(
            exStyle: Windows.Win32.UI.WindowsAndMessaging.WINDOW_EX_STYLE.WS_EX_NOACTIVATE);

        ManageDecision decision = WindowFilter.Evaluate(window.Handle);

        Assert.False(decision.Manageable);
        Assert.Equal(ExclusionReason.CannotActivate, decision.Reason);
    }

    [Fact]
    public void AnOwnedWindowWithNoTitleBarIsAMenu()
    {
        // Autocomplete popups, title-bar menus and tooltips that happen to be
        // top-level. The Alt+Tab test catches most owned windows, but not one that is
        // its owner's last active popup - which is exactly what a menu that has just
        // been opened is.
        using var owner = new TestWindow("owner");

        using var popup = new TestWindow(
            "popup",
            style: Windows.Win32.UI.WindowsAndMessaging.WINDOW_STYLE.WS_POPUP,
            owner: owner);

        ManageDecision decision = WindowFilter.Evaluate(popup.Handle);

        Assert.False(decision.Manageable);
        Assert.Equal(ExclusionReason.OwnedPopup, decision.Reason);
    }

    [Fact]
    public void AnOwnedWindowWithATitleBarIsADialogAndIsStillConsidered()
    {
        // The rule rejects the shape of a popup, not the fact of having an owner. A
        // dialog is owned and has a title bar, and refusing those would be a much
        // larger change than intended - it is the difference between ignoring menus
        // and ignoring every Save As box.
        using var owner = new TestWindow("owner");
        using var dialog = new TestWindow("dialog", owner: owner);

        ManageDecision decision = WindowFilter.Evaluate(dialog.Handle);

        Assert.NotEqual(ExclusionReason.OwnedPopup, decision.Reason);
    }

    [Fact]
    public void AnOrdinaryWindowIsStillManageable()
    {
        // The guard on the two rules above. Both are structural, so a mistake in
        // either rejects windows nobody complained about - which is the failure mode
        // that made requiring WS_CAPTION too expensive to adopt.
        using var window = new TestWindow();

        ManageDecision decision = WindowFilter.Evaluate(window.Handle);

        Assert.True(decision.Manageable, $"an ordinary window was rejected: {decision.Explain()}");
    }

    [Fact]
    public void BothStructuralRulesCanBeOverruledByAConfiguredRule()
    {
        // Structural does not mean certain. An application may set WS_EX_NOACTIVATE
        // and expect to be driven by something other than a click, and an owned
        // frameless window is only usually a menu. A rule saying otherwise knows more
        // than the shape of the window does.
        Assert.True(WindowFilter.CanBeOverridden(ExclusionReason.CannotActivate));
        Assert.True(WindowFilter.CanBeOverridden(ExclusionReason.OwnedPopup));
    }

    [Fact]
    public void TheCredentialPromptIsNotAWindowToTile()
    {
        // A prompt asking for a PIN or a fingerprint, which arrives without warning
        // and should never be resized into a tile. komorebi ships this and we did not.
        Assert.True(WindowFilter.IsExcludedProcessName("CredentialUIBroker"));
    }

    [Fact]
    public void ASplashScreenIsNotAWindowToTile()
    {
        // Office shows this before the application window and takes it away again.
        // Tiling it rearranges the workspace twice for something nobody interacts with.
        Assert.True(WindowFilter.IsExcludedClassName("MsoSplash"));
    }

    [Fact]
    public void OrdinaryClassesAreNotExcluded()
    {
        Assert.False(WindowFilter.IsExcludedClassName("MozillaWindowClass"));
        Assert.False(WindowFilter.IsExcludedClassName("CabinetWClass"));

        // Explorer's own windows in particular, since the two exclusions above are
        // explorer-hosted and an over-broad match would take the file manager with
        // them.
        Assert.False(WindowFilter.IsExcludedClassName("ExplorerWClass"));
    }
}
