using Taj.Core.Sources;

namespace Taj.Core.Tests;

public sealed class ClockSchedulingTests
{
    [Fact]
    public void MinuteClocksWakeAtTheNextDisplayedBoundary()
    {
        var time = new ManualTime();
        using var clock = new ClockSource("clock", "HH:mm", TimeSpan.FromMilliseconds(500), timeProvider: time);
        clock.Start();
        Assert.Equal("12:00", clock.Value);
        Assert.Equal(TimeSpan.FromSeconds(50), time.Timer!.Next - time.GetUtcNow());
        time.Advance(TimeSpan.FromSeconds(49));
        Assert.Equal(0, time.Timer.Fired);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("12:01", clock.Value);
        Assert.Equal(1, time.Timer.Fired);
    }

    [Theory]
    [InlineData("HH:mm:ss", 1000)]
    [InlineData("HH:mm:ss.fff", 500)]
    [InlineData("HH:mm 'seconds'", 50000)]
    [InlineData("HH:mm \\s", 50000)]
    public void SecondsFractionsAndQuotedLiteralsKeepTheirPrecision(string format, int dueMs)
    {
        var time = new ManualTime();
        using var clock = new ClockSource("clock", format, TimeSpan.FromMilliseconds(500), timeProvider: time);
        clock.Start();
        Assert.Equal(TimeSpan.FromMilliseconds(dueMs), time.Timer!.Next - time.GetUtcNow());
    }

    [Fact]
    public void TimeChangesAndResumeRefreshImmediatelyWithoutRestartingPausedSources()
    {
        var time = new ManualTime();
        using var clock = new ClockSource("clock", "HH:mm", TimeSpan.FromMilliseconds(500), timeProvider: time);
        clock.Start();
        clock.StandDown();
        time.Jump(TimeSpan.FromHours(2));
        clock.RefreshSystemTime();
        time.Advance(TimeSpan.Zero);
        Assert.Equal("12:00", clock.Value);
        clock.StandUp();
        time.Advance(TimeSpan.Zero);
        Assert.Equal("14:00", clock.Value);
        time.Jump(TimeSpan.FromHours(1));
        clock.RefreshSystemTime();
        time.Advance(TimeSpan.Zero);
        Assert.Equal("15:00", clock.Value);
    }

    [Fact]
    public void SamplingObserversKeepTheirCadenceAndUnchangedSamplesReuseTheString()
    {
        var time = new ManualTime();
        using var clock = new ClockSource("clock", "HH:mm", TimeSpan.FromMilliseconds(500), timeProvider: time);
        clock.Start();
        string? value = clock.Value;
        int samples = 0;
        void Sample(ISource _, string? text) { samples++; Assert.Same(value, text); }
        clock.Sampled += Sample;
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(3, samples); // immediate adoption, then the two configured samples
        clock.Sampled -= Sample;
        time.Advance(TimeSpan.Zero);
        Assert.True(time.Timer!.Next - time.GetUtcNow() > TimeSpan.FromSeconds(40));
    }

    [Fact]
    public void WarmingRepeatedUnchangedSamplesLeavesNoFormattingAllocation()
    {
        var time = new ManualTime();
        using var clock = new ClockSource("clock", "yyyy", TimeSpan.FromMilliseconds(100), timeProvider: time);
        clock.Sampled += static (_, _) => { };
        clock.Start();
        for (int i = 0; i < 200; i++) time.Advance(TimeSpan.FromMilliseconds(100));
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 28, 12, 0, 10, TimeSpan.Zero);
        internal ManualTimer? Timer { get; private set; }
        public override DateTimeOffset GetUtcNow() => _now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Timer = new ManualTimer(this, callback, state);
            Timer.Change(dueTime, period);
            return Timer;
        }
        internal void Jump(TimeSpan delta) => _now += delta;
        internal void Advance(TimeSpan delta)
        {
            DateTimeOffset target = _now + delta;
            while (Timer is { } timer && timer.Next <= target)
            {
                _now = timer.Next;
                timer.Fire();
            }
            _now = target;
        }
    }

    private sealed class ManualTimer(ManualTime time, TimerCallback callback, object? state) : ITimer
    {
        internal DateTimeOffset Next { get; private set; } = DateTimeOffset.MaxValue;
        internal int Fired { get; private set; }
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (period != Timeout.InfiniteTimeSpan) throw new InvalidOperationException("Expected a one-shot timer.");
            Next = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : time.GetUtcNow() + dueTime;
            return true;
        }
        internal void Fire()
        {
            Next = DateTimeOffset.MaxValue;
            Fired++;
            callback(state);
        }
        public void Dispose() => Next = DateTimeOffset.MaxValue;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
