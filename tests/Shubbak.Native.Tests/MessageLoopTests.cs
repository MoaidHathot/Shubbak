using System.Diagnostics;
using Shubbak.Core.Diagnostics;

namespace Shubbak.Native.Tests;

/// <summary>
/// How the pump decides when to run again.
/// </summary>
/// <remarks>
/// It used to sleep, and a sleeping loop cannot do better than the system timer:
/// asking for 8 ms measured at p50 15.50 ms, about 65 passes a second, with the pass
/// itself taking 0.00 ms. Everything downstream inherited that floor - the animation
/// ran at half its designed rate and a keystroke waited on a timer before anything
/// looked at it.
/// </remarks>
public sealed class MessageLoopTests
{
    private static Thread RunOn(MessageLoop loop, TimeSpan interval)
    {
        var thread = new Thread(() => loop.Run(interval)) { IsBackground = true };

        thread.Start();

        // The loop records its thread id first, so Stop has something to post to.
        SpinWait.SpinUntil(() => loop.IsRunning, TimeSpan.FromSeconds(2));

        return thread;
    }

    [Fact]
    public void WakingRunsAPassAlmostImmediately()
    {
        // The property that matters for input: a keystroke queued from the hook must
        // not wait for a timer before anything looks at it.
        using var loop = new MessageLoop();

        var ran = new ManualResetEventSlim(false);
        int passes = 0;

        loop.NextTimeout = () => Timeout.InfiniteTimeSpan;
        loop.Tick += () => { if (Interlocked.Increment(ref passes) > 1) ran.Set(); };

        Thread thread = RunOn(loop, TimeSpan.FromMilliseconds(8));

        try
        {
            // Let it settle into the indefinite wait before signalling.
            Thread.Sleep(100);

            var watch = Stopwatch.StartNew();
            loop.Wake();

            Assert.True(ran.Wait(TimeSpan.FromSeconds(2)), "waking did not run a pass");
            Assert.True(
                watch.ElapsedMilliseconds < 100,
                $"waking took {watch.ElapsedMilliseconds} ms, which is a timer rather than a signal");
        }
        finally
        {
            loop.Stop();
            thread.Join(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public void AnIdleLoopDoesNotSpin()
    {
        // The other half. Waiting indefinitely when there is nothing to do is what
        // takes an idle desktop from sixty-odd wakeups a second to none.
        using var loop = new MessageLoop();

        int passes = 0;

        loop.NextTimeout = () => Timeout.InfiniteTimeSpan;
        loop.Tick += () => Interlocked.Increment(ref passes);

        Thread thread = RunOn(loop, TimeSpan.FromMilliseconds(8));

        try
        {
            Thread.Sleep(400);

            // A handful from startup and from stray messages is expected; sixty a
            // second is the behaviour being replaced.
            Assert.True(passes < 20, $"{passes} passes in 400 ms while idle");
        }
        finally
        {
            loop.Stop();
            thread.Join(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public void AShortTimeoutStillRunsWithoutBeingWoken()
    {
        // What the animation path asks for. Nothing signals between frames, so the
        // timeout is the only thing bringing the loop back.
        using var timer = new TimerResolution(1);
        using var loop = new MessageLoop();

        timer.Acquire();

        int passes = 0;

        loop.NextTimeout = () => TimeSpan.FromMilliseconds(7);
        loop.Tick += () => Interlocked.Increment(ref passes);

        Thread thread = RunOn(loop, TimeSpan.FromMilliseconds(8));

        try
        {
            Thread.Sleep(300);

            // 300 ms at 7 ms is about 40. Well under that means the wait is being
            // rounded up to the system timer, which is the fault being fixed.
            Assert.True(passes > 20, $"only {passes} passes in 300 ms at a 7 ms timeout");
        }
        finally
        {
            loop.Stop();
            thread.Join(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public void AWaitThatRunsOutIsToldApartFromOneThatIsCutShort()
    {
        // The wait's return value says why it ended and used to be discarded. Without
        // it a loop running late and a loop being woken early look identical from
        // outside, and they want opposite fixes - one is the clock, the other is the
        // traffic.
        using var timer = new TimerResolution(1);
        using var loop = new MessageLoop();

        timer.Acquire();

        loop.NextTimeout = () => TimeSpan.FromMilliseconds(10);

        Thread thread = RunOn(loop, TimeSpan.FromMilliseconds(10));

        try
        {
            Thread.Sleep(200);

            long timedOut = loop.WaitsTimedOut;

            Assert.True(timedOut > 0, "no wait ran to its timeout in 200 ms at a 10 ms interval");

            // Now interrupt it repeatedly and watch the other counter, not this one.
            for (int i = 0; i < 20; i++)
            {
                loop.Wake();
                Thread.Sleep(2);
            }

            Assert.True(
                loop.WaitsInterrupted > 0,
                "waking the loop twenty times recorded no interrupted wait");
        }
        finally
        {
            loop.Stop();
            thread.Join(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public void TheFineTimerKeepsWaitsCloseToWhatWasAskedFor()
    {
        // Windows' default timer granularity is 15.625 ms, so without the fine
        // resolution a 10 ms wait comes back at about 15.6 - an overshoot of 5.6 ms
        // that would cap any frame rate above about 64 Hz regardless of what the
        // daemon asked for, and would look exactly like a bug in its arithmetic.
        //
        // A timing assertion, so the threshold is set to tell 1 ms granularity from
        // 15.625 ms rather than to measure the scheduler: the two produce medians
        // about five milliseconds apart and this sits between them.
        using var timer = new TimerResolution(1);
        using var loop = new MessageLoop();

        timer.Acquire();

        Assert.True(timer.IsHeld, "timeBeginPeriod(1) was refused, so this measures nothing");

        loop.NextTimeout = () => TimeSpan.FromMilliseconds(10);
        loop.IsPacingFrames = true;

        Thread thread = RunOn(loop, TimeSpan.FromMilliseconds(10));

        try
        {
            Thread.Sleep(400);

            Assert.True(
                loop.WakeOvershootPacing.Count > 5,
                $"only {loop.WakeOvershootPacing.Count} samples");

            // A low percentile rather than the median. Transient load on the machine
            // pushes the upper tail out and can move a median by several milliseconds,
            // which made this flake about one run in ten. It cannot pull the best
            // waits in: 15.625 ms granularity overshoots a 10 ms request by 5.6 ms on
            // every single wait, including the luckiest. So the question this asks -
            // "did any wait come back near when it was asked to?" - separates the two
            // granularities without depending on the machine being quiet.
            double best = loop.WakeOvershootPacing.Percentile(0.1);

            Assert.True(best < 4, $"even the promptest waits overshot 10 ms by {best:F2} ms");
        }
        finally
        {
            loop.Stop();
            thread.Join(TimeSpan.FromSeconds(2));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OvershootGoesToTheBucketTheCallerAskedFor(bool pacing)
    {
        // The whole point of the split, and the thing that fails silently if the flag
        // is wired backwards or never set: everything lands in one bucket and the
        // figure is contaminated exactly as it was before, while still looking like a
        // measurement. That is how the first version of this instrument reported a
        // 12 ms overshoot on animation frames that were never measured.
        using var timer = new TimerResolution(1);
        using var loop = new MessageLoop();

        timer.Acquire();

        loop.NextTimeout = () => TimeSpan.FromMilliseconds(10);
        loop.IsPacingFrames = pacing;

        Thread thread = RunOn(loop, TimeSpan.FromMilliseconds(10));

        try
        {
            Thread.Sleep(200);

            LatencyStats used = pacing ? loop.WakeOvershootPacing : loop.WakeOvershootIdle;
            LatencyStats unused = pacing ? loop.WakeOvershootIdle : loop.WakeOvershootPacing;

            Assert.True(used.Count > 0, $"pacing={pacing} recorded nothing in the bucket it asked for");
            Assert.Equal(0, unused.Count);
        }
        finally
        {
            loop.Stop();
            thread.Join(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public void StoppingEndsTheLoop()
    {
        using var loop = new MessageLoop();

        loop.NextTimeout = () => Timeout.InfiniteTimeSpan;

        Thread thread = RunOn(loop, TimeSpan.FromMilliseconds(8));

        loop.Stop();

        Assert.True(thread.Join(TimeSpan.FromSeconds(2)), "the loop did not exit");
        Assert.False(loop.IsRunning);
    }

    [Fact]
    public void TimerResolutionIsIdempotentAndReleasable()
    {
        // Process-wide, so a stray acquire that is never released outlives its reason
        // and defeats timer coalescing for everything else running.
        using var timer = new TimerResolution(1);

        timer.Acquire();
        timer.Acquire();

        Assert.True(timer.IsHeld);

        timer.Release();
        Assert.False(timer.IsHeld);

        timer.Release();
        Assert.False(timer.IsHeld);
    }
}
