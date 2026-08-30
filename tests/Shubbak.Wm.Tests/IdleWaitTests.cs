using Shubbak.Wm;

namespace Shubbak.Wm.Tests;

/// <summary>
/// How long the loop waits when nothing is moving.
/// </summary>
/// <remarks>
/// <para>
/// The interesting case is suspension, which waits forever. That is safe only
/// because every path that can end a suspension signals the pump - an IPC request
/// through <c>InvokeAsync</c>, which wakes it explicitly, and the resume hotkey,
/// which arrives as a thread message the wait is already watching for. The wake
/// handle is an <c>AutoResetEvent</c>, so a signal raised while a pass is running is
/// remembered rather than lost.
/// </para>
/// <para>
/// These are stated here rather than left to a running daemon because "waits
/// forever" is the kind of decision that is invisible until it is wrong, and the
/// symptom would be a window manager that appears to have hung.
/// </para>
/// </remarks>
public sealed class IdleWaitTests
{
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(17);
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan Idle = TimeSpan.FromMilliseconds(250);

    private static TimeSpan Wait(bool suspended, bool layoutDirty = false, bool settling = false) =>
        WmDaemon.IdleWait(suspended, layoutDirty, settling, Frame, Settle, Idle);

    [Fact]
    public void ASuspendedLoopWaitsForever()
    {
        // Nothing is watching the desktop, so there is nothing a timeout could
        // discover. Only a message or a signal can matter, and both interrupt a wait
        // of any length.
        Assert.Equal(Timeout.InfiniteTimeSpan, Wait(suspended: true));
    }

    [Fact]
    public void APendingPassBeatsSuspension()
    {
        // A suspended daemon still lays out when a command tells it to - `wm-redraw`
        // over the pipe does exactly that, and it was measured doing so. Whatever set
        // the flag woke the pump on its way past, so this cannot strand in practice;
        // it is checked first anyway, because a wait with no end is the wrong place to
        // depend on that.
        Assert.Equal(Frame, Wait(suspended: true, layoutDirty: true));
    }

    [Fact]
    public void APendingPassRunsPromptlyWhenNotSuspended()
    {
        Assert.Equal(Frame, Wait(suspended: false, layoutDirty: true));
    }

    [Fact]
    public void ASettlingWindowShortensTheWait()
    {
        // Without this the pump would sleep out the idle interval and the check would
        // happen whenever something else next woke it, which on a still desktop is a
        // long time - and a still desktop is exactly when a window that moved itself
        // needs looking at.
        Assert.Equal(Settle, Wait(suspended: false, settling: true));
    }

    [Fact]
    public void AStillDesktopWaitsTheIdleInterval()
    {
        Assert.Equal(Idle, Wait(suspended: false));
    }

    [Fact]
    public void SuspensionBeatsASettlingWindow()
    {
        // Suspending clears the settle list, so this should not arise. If it ever
        // does, the suspension is the stronger statement: nothing is being adopted
        // while the hooks are released, so there is nothing for the look to find.
        Assert.Equal(Timeout.InfiniteTimeSpan, Wait(suspended: true, settling: true));
    }
}
