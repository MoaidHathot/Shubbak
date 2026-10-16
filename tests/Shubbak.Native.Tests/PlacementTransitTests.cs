using Shubbak.Core.Animation;
using Shubbak.Core.Geometry;

namespace Shubbak.Native.Tests;

/// <summary>
/// Tests for what the committer believes about a window between posting its
/// rectangle and the window's thread applying it.
/// </summary>
/// <remarks>
/// <para>
/// Every placement is posted with <c>SWP_ASYNCWINDOWPOS</c>, so <c>GetWindowRect</c>
/// goes on reporting the old rectangle until the target thread gets to the request.
/// For a window that has just opened and is busy building its contents that was
/// measured at 69-108 ms, and anything that asked where the window was in that gap -
/// the layout pass choosing where an animation starts, above all - got the old
/// answer and acted on it. The symptom was a new window shrinking into its tile,
/// growing, shrinking, growing and shrinking: one journey per layout pass that
/// happened to run before the first had been applied.
/// </para>
/// <para>
/// The rule is small enough to state exactly, which is what the first half does; the
/// second half posts to a real window on another thread, which is the only way to
/// have a genuinely outstanding placement to look at.
/// </para>
/// </remarks>
public sealed class PlacementTransitTests
{
    private static readonly Rect Applied = new(293, 200, 654, 487);

    /// <summary>Where the window was when the placement was posted.</summary>
    private static readonly Rect Origin = new(100, 100, 320, 240);

    private static readonly TimeSpan Recent = TimeSpan.FromMilliseconds(100);

    [Fact]
    public void AWindowAtItsRectangleHasArrived()
    {
        Assert.Equal(
            WindowCommitter.TransitVerdict.Arrived,
            WindowCommitter.JudgeTransit(Applied, Applied, Origin, TimeSpan.Zero));
    }

    [Theory]
    [InlineData(4, 0, 0, 0)]
    [InlineData(0, -4, 0, 0)]
    [InlineData(0, 0, 4, 0)]
    [InlineData(0, 0, 0, -4)]
    [InlineData(2, 2, -3, 3)]
    public void RoundingWithinTheToleranceStillCountsAsArrived(int dx, int dy, int dw, int dh)
    {
        // A terminal snapping to a character cell, a window with a fixed aspect ratio:
        // applications adjust what they are given by a few pixels, and a window that
        // did so has still processed the request. The tolerance is below the distance
        // the animation engine moves a window for, so being this far out costs nothing
        // further either.
        var actual = new Rect(Applied.X + dx, Applied.Y + dy, Applied.Width + dw, Applied.Height + dh);

        Assert.Equal(
            WindowCommitter.TransitVerdict.Arrived,
            WindowCommitter.JudgeTransit(actual, Applied, Origin, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void AWindowStillWhereItWasIsInTransitWhileRecentlyPosted()
    {
        Assert.Equal(
            WindowCommitter.TransitVerdict.InTransit,
            WindowCommitter.JudgeTransit(Origin, Applied, Origin, Recent));
    }

    [Fact]
    public void ASizeOnlyChangeIsNotArrivedUntilTheSizeHasChanged()
    {
        // The case the drift watch's position-only tolerance gets wrong, and the
        // reason this is its own rule. Halving a window in place changes no position,
        // so judged on position alone it has arrived the instant it is posted - and
        // the layout pass then animates the resize a second time from the size the
        // window still has.
        var halvedInPlace = new Rect(Applied.X, Applied.Y, Applied.Width / 2, Applied.Height);

        Assert.Equal(
            WindowCommitter.TransitVerdict.InTransit,
            WindowCommitter.JudgeTransit(Applied, halvedInPlace, Applied, Recent));
    }

    [Fact]
    public void ALonePlacementWhoseWindowHasMovedElsewhereIsAbandonedAtOnce()
    {
        // The window was at Origin when asked to go to Applied, and is now somewhere
        // that is neither. Its thread has acted, and ours was the only request it had
        // from us, so what it acted on was its own: the application restoring the
        // geometry it remembered. The drift watch wants that corrected promptly, not
        // after a second of being told the window is on its way.
        var elsewhere = new Rect(Origin.X + 900, Origin.Y + 700, Origin.Width, Origin.Height);

        Assert.Equal(
            WindowCommitter.TransitVerdict.Abandoned,
            WindowCommitter.JudgeTransit(elsewhere, Applied, Origin, Recent));
    }

    [Fact]
    public void AFinalFrameWhoseWindowHasMovedIsStillInTransit()
    {
        // Behind a motion's final frame the window may be working through the frames
        // before it, so moving from one waypoint to the next is progress rather than
        // departure. With no origin to compare against, only the clock can give up.
        var waypoint = new Rect(Applied.X - 300, Applied.Y, Applied.Width + 200, Applied.Height);

        Assert.Equal(
            WindowCommitter.TransitVerdict.InTransit,
            WindowCommitter.JudgeTransit(waypoint, Applied, origin: null, Recent));
    }

    [Fact]
    public void TheGraceLapsesIntoAbandonment()
    {
        Assert.Equal(
            WindowCommitter.TransitVerdict.InTransit,
            WindowCommitter.JudgeTransit(Origin, Applied, Origin, WindowCommitter.TransitGrace - TimeSpan.FromMilliseconds(1)));

        Assert.Equal(
            WindowCommitter.TransitVerdict.Abandoned,
            WindowCommitter.JudgeTransit(Origin, Applied, Origin, WindowCommitter.TransitGrace));

        Assert.Equal(
            WindowCommitter.TransitVerdict.Abandoned,
            WindowCommitter.JudgeTransit(Origin, Applied, origin: null, WindowCommitter.TransitGrace));
    }

    [Fact]
    public void ArrivalIsNotTimedOut()
    {
        // A window that took longer than the grace and then arrived has arrived. The
        // clock only decides what to believe about a window that is not there.
        Assert.Equal(
            WindowCommitter.TransitVerdict.Arrived,
            WindowCommitter.JudgeTransit(Applied, Applied, Origin, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void TheToleranceIsBelowWhatTheEngineAnimates()
    {
        // The property that makes a too-tight tolerance harmless, stated so a change
        // to either constant has to come here and argue. A window read as arrived is
        // read at its real rectangle, and whatever separates that from the target must
        // be too little for the engine to move it for - otherwise every window that
        // rounds its own size would be animated by the rounding on each layout pass.
        Assert.True(
            WindowCommitter.ArrivalTolerance < AnimationOptions.Default.MinimumAnimatedDistance,
            "a window that has arrived within the tolerance would be animated by the difference");
    }

    // ---- withholding frames from a window that is behind ------------------

    private static readonly TimeSpan OneFrame = TimeSpan.FromMilliseconds(16);

    [Fact]
    public void AWindowUnchangedAcrossTwoPostsIsNotSentAThird()
    {
        // The window has not moved since two frames ago, so it has processed neither
        // of them. Posting another would only lengthen the queue it will later work
        // through in a burst, each entry for a rectangle already stale.
        Assert.True(WindowCommitter.ShouldWithhold(Origin, Origin, Origin, OneFrame));
    }

    [Fact]
    public void OneFrameBehindIsTheOrdinaryLagAndIsSentTheNext()
    {
        // It moved between the post before last and the last one, so it is applying
        // frames - about one frame late, which is what an application that takes a
        // frame to resize looks like. A steady lag is not a growing queue.
        var earlier = new Rect(Origin.X - 40, Origin.Y, Origin.Width, Origin.Height);

        Assert.False(WindowCommitter.ShouldWithhold(Origin, Origin, earlier, OneFrame));
    }

    [Fact]
    public void AWindowThatHasMovedSinceTheLastPostIsSentTheNext()
    {
        // Moved at all: an application that adjusts the sizes it is given lands near a
        // frame rather than on it, and that still means it processed the frame.
        var nearTheFrame = new Rect(Applied.X, Applied.Y, Applied.Width + 6, Applied.Height);

        Assert.False(WindowCommitter.ShouldWithhold(Applied, Origin, Origin, OneFrame));
        Assert.False(WindowCommitter.ShouldWithhold(nearTheFrame, Origin, Origin, OneFrame));
    }

    [Fact]
    public void TheFirstTwoFramesOfAMotionAreAlwaysSent()
    {
        // Before the second post there is no "post before last" to compare against;
        // the committer passes the empty rectangle, which no window reports.
        Assert.False(WindowCommitter.ShouldWithhold(Origin, Origin, default, OneFrame));
    }

    [Fact]
    public void AStaleRecordDoesNotWithhold()
    {
        // The guard against a motion that ended without a final frame leaving its last
        // record behind: frames arrive every few milliseconds while a window is
        // genuinely behind, so a record this old is about some earlier motion.
        Assert.True(WindowCommitter.ShouldWithhold(Origin, Origin, Origin, WindowCommitter.WithholdingLapses - TimeSpan.FromMilliseconds(1)));
        Assert.False(WindowCommitter.ShouldWithhold(Origin, Origin, Origin, WindowCommitter.WithholdingLapses));
    }

    [Fact]
    public void AResponsiveWindowReceivesEveryFrameAndTheFinalOneIsAlwaysSent()
    {
        // A real window on its own pumping thread applies each frame long before the
        // next is due, so nothing is withheld from it and it ends exactly where the
        // final frame says.
        using var window = new TestWindow();
        var committer = new WindowCommitter();

        Rect before = WindowCommitter.VisibleBounds(window.Handle);
        long withheldBefore = committer.FramesWithheld;

        Rect[] waypoints =
        [
            new(before.X + 40, before.Y + 20, before.Width + 30, before.Height),
            new(before.X + 80, before.Y + 40, before.Width + 60, before.Height),
            new(before.X + 120, before.Y + 60, before.Width + 90, before.Height),
        ];

        for (int i = 0; i < waypoints.Length; i++)
        {
            bool final = i == waypoints.Length - 1;
            committer.CommitFrame([new AnimationFrame(window.Handle, waypoints[i], final, SizeUnchanged: false)]);

            TestWindow.PumpUntil(() => WindowCommitter.VisibleBounds(window.Handle) == waypoints[i]);
            Assert.Equal(waypoints[i], WindowCommitter.VisibleBounds(window.Handle));
        }

        Assert.Equal(withheldBefore, committer.FramesWithheld);
    }

    [Fact]
    public async Task AFrameIsWithheldFromAWindowTwoFramesBehind()
    {
        // The window's thread is made busy - an application in the middle of its own
        // work, as a browser laying out a page or a terminal reflowing is - so the
        // frames posted to it sit in its queue. The first two are sent regardless; the
        // third finds the window exactly where it was across both, and is kept back.
        // The final frame is never kept back, whatever the window has or has not done,
        // and once the thread is free the window ends where that frame says.
        using var window = new TestWindow();
        var committer = new WindowCommitter();

        Rect before = WindowCommitter.VisibleBounds(window.Handle);
        long withheldBefore = committer.FramesWithheld;

        var first = new Rect(before.X + 40, before.Y, before.Width, before.Height);
        var second = new Rect(before.X + 80, before.Y, before.Width, before.Height);
        var third = new Rect(before.X + 100, before.Y, before.Width, before.Height);
        var last = new Rect(before.X + 120, before.Y, before.Width, before.Height);

        using var busy = new ManualResetEventSlim(false);
        using var released = new ManualResetEventSlim(false);

        // Invoke waits for the action, so it is issued from another thread; the
        // window's thread is then inside the action and pumping nothing until it is
        // released.
        Task occupied = Task.Run(() => window.Invoke(() => { busy.Set(); released.Wait(TimeSpan.FromSeconds(5)); }));
        Assert.True(busy.Wait(TimeSpan.FromSeconds(5)), "the window's thread never became busy");

        committer.CommitFrame([new AnimationFrame(window.Handle, first, IsFinal: false, SizeUnchanged: true)]);
        committer.CommitFrame([new AnimationFrame(window.Handle, second, IsFinal: false, SizeUnchanged: true)]);

        Assert.Equal(withheldBefore, committer.FramesWithheld);

        committer.CommitFrame([new AnimationFrame(window.Handle, third, IsFinal: false, SizeUnchanged: true)]);

        Assert.Equal(withheldBefore + 1, committer.FramesWithheld);
        Assert.Equal(before, WindowCommitter.VisibleBounds(window.Handle));

        committer.CommitFrame([new AnimationFrame(window.Handle, last, IsFinal: true, SizeUnchanged: false)]);

        released.Set();
        await occupied;

        TestWindow.PumpUntil(() => WindowCommitter.VisibleBounds(window.Handle) == last);

        Assert.Equal(last, WindowCommitter.VisibleBounds(window.Handle));
        Assert.Equal(withheldBefore + 1, committer.FramesWithheld);
    }

    [Fact]
    public void APostedPlacementIsBelievedUntilTheWindowIsSeenThere()
    {
        // A real window on its own pumping thread, so the post is genuinely
        // asynchronous: between SetWindowPos returning and that thread running,
        // GetWindowRect reports the old rectangle.
        using var window = new TestWindow();
        var committer = new WindowCommitter();

        Rect before = WindowCommitter.VisibleBounds(window.Handle);
        var wanted = new Rect(before.X + 400, before.Y + 160, before.Width + 120, before.Height + 60);

        committer.Commit([Place(window, wanted)], static p => (nint)p.Window.Handle);

        // Whatever the window reports at this instant, the committer answers with what
        // it asked for - which is what a layout pass that ran on this same tick must
        // be told, or it would start a second journey from the old rectangle.
        Assert.Equal(wanted, committer.CurrentBounds(window.Handle));

        TestWindow.PumpUntil(() => WindowCommitter.VisibleBounds(window.Handle) == wanted);

        // Seen there, so no longer in transit and no longer in the table.
        Assert.False(committer.IsInTransit(window.Handle));
        Assert.Equal(0, committer.InTransitCount);
        Assert.Equal(wanted, committer.CurrentBounds(window.Handle));
    }

    [Fact]
    public void AWindowThatWentSomewhereElseIsBelievedWhereItIs()
    {
        // The drift case, with nothing having looked in between: posted, applied, and
        // then the window takes itself elsewhere before anyone asks. The first look
        // must say where it really is, not where it was sent.
        using var window = new TestWindow();
        var committer = new WindowCommitter();

        Rect before = WindowCommitter.VisibleBounds(window.Handle);
        var wanted = new Rect(before.X + 400, before.Y + 160, before.Width, before.Height);

        committer.Commit([Place(window, wanted)], static p => (nint)p.Window.Handle);
        TestWindow.PumpUntil(() => WindowCommitter.VisibleBounds(window.Handle) == wanted);

        // The window moves its own rectangle, shadow included; the committer answers in
        // visible frames. Where it ended up is therefore read back, not assumed from the
        // coordinates it was sent to. The two differ by the shadow's inset wherever the
        // host can measure one - seven pixels on the 100% runner, where the assumption
        // failed - and the test is about which rectangle the committer believes, not
        // about the shadow.
        window.MoveTo(before.X + 900, before.Y + 500);
        TestWindow.PumpUntil(() => WindowCommitter.VisibleBounds(window.Handle) != wanted);

        Rect elsewhere = WindowCommitter.VisibleBounds(window.Handle);
        Assert.NotEqual(wanted, elsewhere);

        Assert.False(committer.IsInTransit(window.Handle));
        Assert.Equal(elsewhere, committer.CurrentBounds(window.Handle));
        Assert.Equal(0, committer.InTransitCount);
    }

    private static Core.Layouts.Placement Place(TestWindow window, Rect rect) =>
        new(new Core.Tree.WindowNode(window.Handle, Identity), rect, Visible: true);

    private static Core.Tree.WindowIdentity Identity => new()
    {
        ProcessName = "test",
        ClassName = "ShubbakNativeTestWindow",
        Title = "Shubbak test window",
    };
}
