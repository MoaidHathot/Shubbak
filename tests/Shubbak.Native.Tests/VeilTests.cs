using Shubbak.Core.Geometry;
using Xunit.Abstractions;

namespace Shubbak.Native.Tests;

/// <summary>
/// Tests for the veil: the cloak a newly arrived window wears from the tick that
/// sees it until its placement is seen to have landed.
/// </summary>
/// <remarks>
/// <para>
/// Against a window of another process, as every veil in production is, because the
/// shell's cloak is the mechanism and the shell keeps no view for this test
/// process's borderless popups. The one same-process case worth having is the refusal
/// that follows from that.
/// </para>
/// <para>
/// What is pinned here is the bookkeeping rather than the compositor: that the
/// layout's own Show leaves a veil alone, that a workspace switch turns it into an
/// ordinary concealment, that the timeout lifts it regardless, and that releasing the
/// window or shutting down leaves nothing cloaked behind. Each is a way a window could
/// have been stranded invisible.
/// </para>
/// </remarks>
public sealed class VeilTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    [Fact]
    public void AVeiledWindowIsConcealedAndTheLayoutsShowLeavesItSo()
    {
        using var foreign = ForeignWindow.Acquire(output);
        if (foreign is null) return;

        var committer = new WindowCommitter();

        Assert.True(committer.Veil(foreign.Handle), "the shell refused to cloak");
        Assert.True(committer.IsConcealing(foreign.Handle));
        Assert.Equal(1, committer.VeiledCount);

        WaitForCloakState(foreign.Handle, Win32Window.CloakState.Shell);

        // The layout pass calls Show for every visible placement, the veiled one
        // included, on the very tick the veil went on. It must not lift it.
        Rect before = WindowCommitter.VisibleBounds(foreign.Handle);
        var tile = new Rect(before.X + 200, before.Y + 120, before.Width, before.Height);

        committer.Commit([Place(foreign.Handle, tile)], static p => (nint)p.Window.Handle);

        Assert.Equal(1, committer.VeiledCount);
        Assert.Equal(Win32Window.CloakState.Shell, Win32Window.GetCloakState(foreign.Handle));

        // Lifted once the placement is seen to have landed, which for a responsive
        // window is a moment later.
        TestWindow.PumpUntil(() => committer.UnveilDue(Generous) > 0);

        Assert.Equal(0, committer.VeiledCount);
        Assert.False(committer.IsConcealing(foreign.Handle));
        WaitForCloakState(foreign.Handle, Win32Window.CloakState.None);
        Assert.Equal(Win32Window.CloakState.None, Win32Window.GetCloakState(foreign.Handle));
        Assert.Equal(tile, WindowCommitter.VisibleBounds(foreign.Handle));
    }

    [Fact]
    public void TheTimeoutLiftsAVeilWhateverTheWindowHasDone()
    {
        // The safety valve. A window that never applies its rectangle is still shown,
        // wherever it is, once it has been waited for long enough.
        using var foreign = ForeignWindow.Acquire(output);
        if (foreign is null) return;

        var committer = new WindowCommitter();

        Assert.True(committer.Veil(foreign.Handle), "the shell refused to cloak");

        Rect before = WindowCommitter.VisibleBounds(foreign.Handle);
        committer.Commit(
            [Place(foreign.Handle, new Rect(before.X + 300, before.Y, before.Width, before.Height))],
            static p => (nint)p.Window.Handle);

        // Zero: everything has been waited for long enough.
        Assert.Equal(1, committer.UnveilDue(TimeSpan.Zero));

        Assert.Equal(0, committer.VeiledCount);
        WaitForCloakState(foreign.Handle, Win32Window.CloakState.None);
        Assert.Equal(Win32Window.CloakState.None, Win32Window.GetCloakState(foreign.Handle));
    }

    [Fact]
    public void AWorkspaceSwitchTurnsTheVeilIntoAConcealment()
    {
        // The window's workspace is switched away while it is still arriving. It now
        // has the better reason to be hidden, and the veil's own end - the placement
        // landing, the timeout - must not put it on screen over the other workspace.
        using var foreign = ForeignWindow.Acquire(output);
        if (foreign is null) return;

        var committer = new WindowCommitter();

        Assert.True(committer.Veil(foreign.Handle), "the shell refused to cloak");

        committer.Commit(
            [new Core.Layouts.Placement(new Core.Tree.WindowNode(foreign.Handle, Identity), default, Visible: false)],
            static p => (nint)p.Window.Handle);

        Assert.Equal(0, committer.VeiledCount);
        Assert.True(committer.IsConcealing(foreign.Handle));

        // Nothing for the veil's clock to lift, however long it has been.
        Assert.Equal(0, committer.UnveilDue(TimeSpan.Zero));
        Assert.Equal(Win32Window.CloakState.Shell, Win32Window.GetCloakState(foreign.Handle));

        // The concealment ends the way concealments do.
        Assert.Equal(1, committer.RestoreAll());
        WaitForCloakState(foreign.Handle, Win32Window.CloakState.None);
        Assert.Equal(Win32Window.CloakState.None, Win32Window.GetCloakState(foreign.Handle));
    }

    [Fact]
    public void ReleasingAVeiledWindowLiftsTheVeil()
    {
        // A window closed, or let go of by a rule, while still arriving: Restore is
        // what the daemon calls on the way out, and it has to undo a veil as readily
        // as any other concealment or the window is left cloaked with nothing
        // tracking it.
        using var foreign = ForeignWindow.Acquire(output);
        if (foreign is null) return;

        var committer = new WindowCommitter();

        Assert.True(committer.Veil(foreign.Handle), "the shell refused to cloak");

        Assert.True(committer.Restore(foreign.Handle));

        Assert.Equal(0, committer.VeiledCount);
        Assert.False(committer.IsConcealing(foreign.Handle));
        WaitForCloakState(foreign.Handle, Win32Window.CloakState.None);
        Assert.Equal(Win32Window.CloakState.None, Win32Window.GetCloakState(foreign.Handle));
    }

    [Fact]
    public void ShutdownLiftsAVeil()
    {
        using var foreign = ForeignWindow.Acquire(output);
        if (foreign is null) return;

        var committer = new WindowCommitter();

        Assert.True(committer.Veil(foreign.Handle), "the shell refused to cloak");

        Assert.Equal(1, committer.RestoreAll());

        Assert.Equal(0, committer.VeiledCount);
        WaitForCloakState(foreign.Handle, Win32Window.CloakState.None);
        Assert.Equal(Win32Window.CloakState.None, Win32Window.GetCloakState(foreign.Handle));
    }

    [Fact]
    public void AWindowAlreadyConcealedIsNotVeiled()
    {
        // Two reasons to be hidden would have two ends, and the veil's would come
        // first - putting a window on screen over a workspace that is not displayed.
        using var foreign = ForeignWindow.Acquire(output);
        if (foreign is null) return;

        var committer = new WindowCommitter();

        committer.Conceal(foreign.Handle);

        Assert.False(committer.Veil(foreign.Handle));
        Assert.Equal(0, committer.VeiledCount);

        committer.RestoreAll();
        WaitForCloakState(foreign.Handle, Win32Window.CloakState.None);
    }

    [Fact]
    public void ARefusedVeilIsRememberedAndGivenUpWithTheTimeout()
    {
        // The shell keeps no view for this process's test popups, so the cloak cannot
        // be applied when asked - which is also the state of every brand-new window on
        // the tick that sees it. The wish is kept, so later ticks can try again once
        // the shell has caught up, and dropped once the window would not have been
        // veiled any longer anyway. Nothing is recorded as concealed in the meantime.
        using var window = new TestWindow();
        var committer = new WindowCommitter();

        bool veiled = committer.Veil(window.Handle);

        if (veiled)
        {
            // The shell did have a view after all; then it must be a real veil.
            Assert.Equal(1, committer.VeiledCount);
            Assert.True(committer.IsConcealing(window.Handle));
            committer.RestoreAll();
            return;
        }

        Assert.Equal(1, committer.VeiledCount);
        Assert.False(committer.IsConcealing(window.Handle));

        Assert.Equal(0, committer.UnveilDue(TimeSpan.Zero));
        Assert.Equal(0, committer.VeiledCount);
    }

    private static Core.Layouts.Placement Place(nint handle, Rect rect) =>
        new(new Core.Tree.WindowNode(handle, Identity), rect, Visible: true);

    private static Core.Tree.WindowIdentity Identity => new()
    {
        ProcessName = "test",
        ClassName = "ShubbakNativeTestWindow",
        Title = "Shubbak test window",
    };

    private static void WaitForCloakState(nint handle, Win32Window.CloakState expected)
    {
        long deadline = Environment.TickCount64 + 3000;

        while (Environment.TickCount64 < deadline)
        {
            if (Win32Window.GetCloakState(handle) == expected) return;

            Thread.Sleep(25);
        }
    }
}
