using Shubbak.Core.Tree;
using Shubbak.Core.Wm;

namespace Shubbak.Native.Tests;

/// <summary>
/// Tests for recovering windows an earlier run left concealed.
/// </summary>
/// <remarks>
/// <para>
/// These guard the only safety property that matters here: recovery must never revive
/// a window Shubbak did not conceal. A desktop carries dozens of windows applications
/// hide on purpose - message-only helpers, tray hosts, media-key listeners, GDI+
/// scratch windows. On one ordinary machine a purely structural search matched
/// eighty-two of them, nearly all junk, and reviving those would paper the screen in
/// windows the user never opened.
/// </para>
/// <para>
/// Windows cannot tell you who hid a window, so the recorded session is the only
/// trustworthy evidence, and the default path is gated on it.
/// </para>
/// </remarks>
public sealed class WindowRecoveryTests
{
    private static Session SessionOf(params RememberedWindow[] windows) =>
        new(1, DateTimeOffset.Now, [.. windows]);

    [Fact]
    public void AHiddenWindowIsSeenAsConcealed()
    {
        using var window = new TestWindow();
        var committer = new WindowCommitter { HideMethod = WindowHideMethod.Hide };

        Assert.False(WindowCommitter.IsConcealed(window.Handle));

        committer.Conceal(window.Handle);

        // The committer's own record is set synchronously, so this is the part that
        // can be asserted without waiting for anything.
        Assert.True(committer.IsConcealing(window.Handle));

        // Whether the window has actually gone off screen is a separate question:
        // SW_HIDE is posted to the owning thread rather than applied, so it lands
        // whenever that thread next pumps.
        TestWindow.PumpUntil(() => WindowCommitter.IsConcealed(window.Handle));

        Assert.True(
            WindowCommitter.IsConcealed(window.Handle),
            "the window was still on screen after the posted hide should have landed");

        committer.RestoreAll();
        TestWindow.PumpUntil(() => Win32Window.IsVisible(window.Handle));
    }

    [Fact]
    public void RevivingBringsAHiddenWindowBack()
    {
        // Deliberately not through the committer that hid it: revival has to work when
        // the process that concealed the window is gone, which is the entire point.
        using var window = new TestWindow();
        var committer = new WindowCommitter { HideMethod = WindowHideMethod.Hide };

        committer.Conceal(window.Handle);
        TestWindow.PumpUntil(() => !Win32Window.IsVisible(window.Handle));
        Assert.False(Win32Window.IsVisible(window.Handle));

        WindowCommitter.Revive(window.Handle);
        TestWindow.PumpUntil(() => Win32Window.IsVisible(window.Handle));

        Assert.True(Win32Window.IsVisible(window.Handle));
    }

    [Fact]
    public void RevivingAWindowThatWasNeverConcealedIsHarmless()
    {
        // Revival tries every reversal because it cannot know which was used. Each has
        // to be safe on a window that never received it.
        using var window = new TestWindow();

        WindowCommitter.Revive(window.Handle);
        TestWindow.PumpOnce();

        Assert.True(Win32Window.IsVisible(window.Handle));
        Assert.Equal(Win32Window.CloakState.None, Win32Window.GetCloakState(window.Handle));
    }

    [Fact]
    public void AnEmptySessionRecoversNothing()
    {
        // The property that keeps 'shubbak restore' from carpeting the desktop in
        // background helper windows.
        using var window = new TestWindow();
        var committer = new WindowCommitter { HideMethod = WindowHideMethod.Hide };

        committer.Conceal(window.Handle);
        TestWindow.PumpUntil(() => WindowCommitter.IsConcealed(window.Handle));

        Assert.Empty(WindowRecovery.FindRemembered(SessionOf()));

        committer.RestoreAll();
        TestWindow.PumpOnce();
    }

    [Fact]
    public void ASessionForOtherWindowsRecoversNothing()
    {
        using var window = new TestWindow();
        var committer = new WindowCommitter { HideMethod = WindowHideMethod.Hide };

        committer.Conceal(window.Handle);
        TestWindow.PumpOnce();

        Session session = SessionOf(new RememberedWindow(
            "some-other-process", "SomeOtherClass", 12345, "2", [], false, "Tiling"));

        Assert.Empty(WindowRecovery.FindRemembered(session));

        committer.RestoreAll();
        TestWindow.PumpOnce();
    }

    [Fact]
    public void MinimisedWindowsAreNeverCandidates()
    {
        // Minimising is something users do. Un-minimising everything that is minimised
        // would be a bug of its own, so it is excluded before any other test.
        using var window = new TestWindow();

        WindowActions.Minimise(window.Handle);
        TestWindow.PumpOnce();

        Assert.DoesNotContain(
            WindowRecovery.FindAll(),
            candidate => candidate.Handle == window.Handle);
    }

    [Fact]
    public void TheCommitterKnowsWhichWindowsItIsConcealing()
    {
        // The event pipeline depends on this to tell its own concealment apart from a
        // window the user or its application really did put away. Without it, the
        // cloak Shubbak performs comes straight back as a cloak event, the window is
        // unmanaged, and switching back to the workspace finds nothing to reveal -
        // every window on it stranded.
        using var window = new TestWindow();
        var committer = new WindowCommitter { HideMethod = WindowHideMethod.Hide };

        Assert.False(committer.IsConcealing(window.Handle));

        committer.Conceal(window.Handle);
        TestWindow.PumpOnce();

        Assert.True(committer.IsConcealing(window.Handle));

        committer.Reveal(window.Handle);
        TestWindow.PumpOnce();

        Assert.False(committer.IsConcealing(window.Handle));
    }

    [Fact]
    public void ConcealmentIsNotClaimedForWindowsSomeoneElseHid()
    {
        // The other half: a window hidden by its own application must not be mistaken
        // for ours, or the event pipeline would stop unmanaging windows that really
        // have gone away.
        using var window = new TestWindow();
        var committer = new WindowCommitter();

        WindowActions.Minimise(window.Handle);
        TestWindow.PumpOnce();

        Assert.False(committer.IsConcealing(window.Handle));
    }
}
