using Shubbak.Core.Animation;
using Shubbak.Core.Geometry;
using Shubbak.Native;

namespace Shubbak.Native.Tests;

/// <summary>
/// Whether an animation frame may be posted to its target rather than sent to it.
/// </summary>
/// <remarks>
/// <para>
/// Without <c>SWP_ASYNCWINDOWPOS</c>, <c>EndDeferWindowPos</c> sends to each target
/// window's thread and blocks until that thread answers, so every window being moved
/// has a veto over the frame rate. Measured, that was a median of 3.71 ms to move a
/// median of one window, with a worst case of 138.75 ms - which was also, to within
/// ten microseconds, the worst tick the daemon suffered in that run.
/// </para>
/// <para>
/// The flag cannot simply be applied to everything. It makes the move asynchronous,
/// so the target has not necessarily resized, let alone repainted, when the call
/// returns. On a waypoint that a later frame supersedes that is invisible; on the
/// frame a window comes to rest on it is a grey panel where the content should be,
/// most obvious on a window that has just grown. That is how it was noticed.
/// </para>
/// </remarks>
public sealed class AnimationFrameFlagsTests
{
    private static AnimationFrame Frame(bool isFinal) =>
        new(0x1234, new Rect(0, 0, 100, 100), isFinal);

    [Fact]
    public void AWaypointMayBePosted()
    {
        // Nothing has to be true of a waypoint by the time the call returns: another
        // frame is 7 ms behind it and will overwrite whatever it did.
        Assert.True(WindowCommitter.ShouldSendAsynchronously(Frame(isFinal: false)));
    }

    [Fact]
    public void TheSettlingFrameMustBeSent()
    {
        // The window is at its destination and will not be moved again, so this is the
        // position it actually has to paint. Posting it lets the daemon run ahead of
        // the application and leaves the window showing bare background.
        Assert.False(WindowCommitter.ShouldSendAsynchronously(Frame(isFinal: true)));
    }

    [Fact]
    public void TheRuleIsPerWindowRatherThanPerBatch()
    {
        // DeferWindowPos takes flags per window and tracks do not finish together, so
        // a window that has arrived settles on the frame it arrives even while its
        // neighbours are still moving. Treating the batch as a unit would either
        // block on every frame that contained one finished window, or never block at
        // all until the whole motion ended.
        AnimationFrame arrived = Frame(isFinal: true);
        AnimationFrame stillMoving = Frame(isFinal: false);

        Assert.NotEqual(
            WindowCommitter.ShouldSendAsynchronously(arrived),
            WindowCommitter.ShouldSendAsynchronously(stillMoving));
    }
}
