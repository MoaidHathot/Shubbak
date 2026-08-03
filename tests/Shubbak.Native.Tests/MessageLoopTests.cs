using System.Diagnostics;

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
