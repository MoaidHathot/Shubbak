using Shubbak.Core.Wm;

namespace Shubbak.Native.Tests;

/// <summary>
/// Tests for cloaking - how Shubbak conceals the windows of inactive workspaces.
/// </summary>
/// <remarks>
/// <para>
/// These exist because of a real bug. The original implementation concealed windows
/// with <c>ShowWindow(SW_HIDE)</c> and never restored them: a clean exit, a crash or
/// a kill all left every window on an inactive workspace off screen, absent from
/// Alt+Tab and the taskbar, with its process still running. Worse, restarting could
/// not recover them, because the window filter rejects invisible windows - so they
/// were stranded until the application itself was restarted.
/// </para>
/// <para>
/// The platform layer had no tests at all, which is why it shipped. These are the
/// missing ones.
/// </para>
/// </remarks>
public sealed class CloakingTests
{
    [Fact]
    public void ANewWindowIsNotCloaked()
    {
        using var window = new TestWindow();

        Assert.Equal(Win32Window.CloakState.None, Win32Window.GetCloakState(window.Handle));
    }

    [Fact]
    public void CloakingReportsAnApplicationLevelCloak()
    {
        // The distinction that makes the whole approach work. Shubbak's own cloak
        // must be distinguishable from the shell's, or the filter cannot tell a
        // concealed workspace window from a suspended UWP app.
        using var window = new TestWindow();

        Assert.True(Win32Window.Cloak(window.Handle), "the compositor refused to cloak");

        Assert.Equal(Win32Window.CloakState.App, Win32Window.GetCloakState(window.Handle));
    }

    [Fact]
    public void ACloakedWindowIsStillVisibleToWin32()
    {
        // This property is the entire reason for preferring cloaking. A cloaked
        // window still passes IsWindowVisible, so a restarted Shubbak adopts it
        // through the ordinary path and un-cloaks it. A hidden window would be
        // rejected as invisible and lost for good.
        using var window = new TestWindow();

        Win32Window.Cloak(window.Handle);

        Assert.True(Win32Window.IsVisible(window.Handle));
    }

    [Fact]
    public void UncloakingRestoresTheWindow()
    {
        using var window = new TestWindow();

        Win32Window.Cloak(window.Handle);
        Assert.True(Win32Window.Uncloak(window.Handle));

        Assert.Equal(Win32Window.CloakState.None, Win32Window.GetCloakState(window.Handle));
    }

    [Fact]
    public void CloakingIsIdempotent()
    {
        using var window = new TestWindow();

        Win32Window.Cloak(window.Handle);
        Win32Window.Cloak(window.Handle);

        Assert.Equal(Win32Window.CloakState.App, Win32Window.GetCloakState(window.Handle));

        Win32Window.Uncloak(window.Handle);
        Win32Window.Uncloak(window.Handle);

        Assert.Equal(Win32Window.CloakState.None, Win32Window.GetCloakState(window.Handle));
    }

    [Fact]
    public void ACloakedWindowIsStillManageable()
    {
        // The regression guard. If the filter rejected app-cloaked windows, a
        // restart after a crash would strand every concealed window - which is
        // exactly the bug this work fixed.
        using var window = new TestWindow();

        Assert.True(WindowFilter.Evaluate(window.Handle).Manageable);

        Win32Window.Cloak(window.Handle);

        ManageDecision decision = WindowFilter.Evaluate(window.Handle);

        Assert.True(
            decision.Manageable,
            $"an app-cloaked window must stay manageable, got: {decision.Explain()}");
    }

    [Fact]
    public void AHiddenWindowIsNotManageable()
    {
        // The other half of the story, and why SW_HIDE is unrecoverable: once
        // hidden, a window can never be re-adopted.
        using var window = new TestWindow(visible: false);

        ManageDecision decision = WindowFilter.Evaluate(window.Handle);

        Assert.False(decision.Manageable);
        Assert.Equal(ExclusionReason.NotVisible, decision.Reason);
    }

    [Fact]
    public void CloakingSurvivesAcrossOwners()
    {
        // Two windows cloaked independently must not affect each other; the tracking
        // in WindowCommitter assumes per-window state.
        using var first = new TestWindow("first");
        using var second = new TestWindow("second");

        Win32Window.Cloak(first.Handle);

        Assert.Equal(Win32Window.CloakState.App, Win32Window.GetCloakState(first.Handle));
        Assert.Equal(Win32Window.CloakState.None, Win32Window.GetCloakState(second.Handle));
    }

    [Fact]
    public void CloakStateOfADeadWindowIsNotCloaked()
    {
        // Queried constantly during adoption, including for windows that closed a
        // moment ago. It must answer rather than throw.
        var window = new TestWindow();
        nint handle = window.Handle;
        window.Dispose();

        Assert.Equal(Win32Window.CloakState.None, Win32Window.GetCloakState(handle));
    }
}

/// <summary>Tests for <see cref="WindowCommitter"/>'s concealment tracking.</summary>
public sealed class WindowCommitterConcealmentTests
{
    /// <summary>
    /// Conceals a window through the committer.
    /// </summary>
    /// <remarks>
    /// Hide and Show are private because nothing outside the committer should be
    /// making that decision, so they are driven the way the daemon drives them: a
    /// placement with <c>Visible: false</c>.
    /// </remarks>
    private static void Conceal(WindowCommitter committer, nint handle)
    {
        committer.Commit(
            [new Core.Layouts.Placement(
                new Core.Tree.WindowNode(handle, Identity), default, Visible: false)],
            static p => (nint)p.Window.Handle);
    }

    private static void Reveal(WindowCommitter committer, nint handle, Core.Geometry.Rect rect)
    {
        committer.Commit(
            [new Core.Layouts.Placement(
                new Core.Tree.WindowNode(handle, Identity), rect, Visible: true)],
            static p => (nint)p.Window.Handle);
    }

    private static Core.Tree.WindowIdentity Identity => new()
    {
        ProcessName = "test",
        ClassName = "ShubbakNativeTestWindow",
        Title = "Shubbak test window",
    };

    [Fact]
    public void ConcealingTakesAWindowOffScreen()
    {
        // Asserts the outcome, not the mechanism. Which mechanism runs depends on
        // whether the shell has an application view for the window, and it does not
        // keep one for a borderless test popup - so pinning the mechanism here would
        // only measure the harness. That cross-process claim belongs in
        // CrossProcessCloakingTests, where it is made against a real application.
        using var window = new TestWindow();
        var committer = new WindowCommitter();

        Conceal(committer, window.Handle);

        TestWindow.PumpUntil(() =>
            !Win32Window.IsVisible(window.Handle) ||
            Win32Window.GetCloakState(window.Handle) != Win32Window.CloakState.None);

        Assert.True(
            !Win32Window.IsVisible(window.Handle) ||
            Win32Window.GetCloakState(window.Handle) != Win32Window.CloakState.None,
            "the window was left on screen");

        Assert.Equal(1, committer.ConcealedCount);

        committer.RestoreAll();
        TestWindow.PumpOnce();
    }

    [Fact]
    public void RevealingPutsItBack()
    {
        using var window = new TestWindow();
        var committer = new WindowCommitter();

        Conceal(committer, window.Handle);
        TestWindow.PumpOnce();

        Reveal(committer, window.Handle, new Core.Geometry.Rect(100, 100, 320, 240));
        TestWindow.PumpOnce();

        Assert.Equal(Win32Window.CloakState.None, Win32Window.GetCloakState(window.Handle));
        Assert.True(Win32Window.IsVisible(window.Handle));
        Assert.Equal(0, committer.ConcealedCount);
    }

    [Fact]
    public void RestoreAllBringsEverythingBack()
    {
        // What runs on shutdown. Before this existed, exiting left every window on an
        // inactive workspace concealed with no way to reach it.
        using var first = new TestWindow("first");
        using var second = new TestWindow("second");

        var committer = new WindowCommitter();

        Conceal(committer, first.Handle);
        Conceal(committer, second.Handle);

        Assert.Equal(2, committer.ConcealedCount);

        int restored = committer.RestoreAll();

        Assert.Equal(2, restored);
        Assert.Equal(0, committer.ConcealedCount);
        Assert.Equal(Win32Window.CloakState.None, Win32Window.GetCloakState(first.Handle));
        Assert.Equal(Win32Window.CloakState.None, Win32Window.GetCloakState(second.Handle));
    }

    [Fact]
    public void RestoreAllIsSafeWithNothingConcealed()
    {
        var committer = new WindowCommitter();

        Assert.Equal(0, committer.RestoreAll());
    }

    [Fact]
    public void RestoreAllSkipsWindowsThatHaveClosed()
    {
        // Entirely routine on shutdown: an application may exit before Shubbak does.
        var window = new TestWindow();
        var committer = new WindowCommitter();

        Conceal(committer, window.Handle);
        window.Dispose();

        Assert.Equal(0, committer.RestoreAll());
    }

    [Fact]
    public void ForgettingAWindowDropsItFromTheConcealedSet()
    {
        using var window = new TestWindow();
        var committer = new WindowCommitter();

        Conceal(committer, window.Handle);
        committer.Forget(window.Handle);

        Assert.Equal(0, committer.ConcealedCount);

        // Left cloaked, deliberately: Forget is called when a window is unmanaged,
        // and by then the caller has already decided what should happen to it.
        Win32Window.Uncloak(window.Handle);
    }

    [Fact]
    public void ConfiguringHideUsesShowWindowRatherThanCloaking()
    {
        using var window = new TestWindow();
        var committer = new WindowCommitter { HideMethod = WindowHideMethod.Hide };

        Conceal(committer, window.Handle);
        TestWindow.PumpUntil(() => !Win32Window.IsVisible(window.Handle));

        // Hidden rather than cloaked - the escape hatch for environments where the
        // compositor is unavailable.
        Assert.Equal(Win32Window.CloakState.None, Win32Window.GetCloakState(window.Handle));
        Assert.False(Win32Window.IsVisible(window.Handle));

        committer.RestoreAll();
        TestWindow.PumpOnce();
    }

    [Fact]
    public void RestoreUsesTheMethodThatConcealedTheWindow()
    {
        // Restoring with the wrong call leaves the window off screen: un-cloaking
        // something that was hidden does nothing at all.
        using var window = new TestWindow();
        var committer = new WindowCommitter { HideMethod = WindowHideMethod.Hide };

        Conceal(committer, window.Handle);

        // Recorded synchronously, unlike the window's visibility - SW_HIDE is posted
        // to the owning thread. This is the part of the contract the test is about:
        // that a hidden window is remembered as hidden, so RestoreAll reverses it the
        // matching way rather than un-cloaking something that was never cloaked.
        Assert.True(committer.IsConcealing(window.Handle));

        TestWindow.PumpUntil(() => !Win32Window.IsVisible(window.Handle));

        committer.RestoreAll();
        TestWindow.PumpUntil(() => Win32Window.IsVisible(window.Handle));

        Assert.True(Win32Window.IsVisible(window.Handle));
    }
}
