namespace Shubbak.Wm.Tests;

public sealed class FramePacerTests
{
    [Theory]
    [InlineData(60)]
    [InlineData(144)]
    [InlineData(240)]
    public void RepeatedSmallOvershootsDoNotAccumulateDrift(int fps)
    {
        double period = 1000.0 / fps;
        var clock = new FramePacer();
        clock.Commit(0, period);
        for (int i = 1; i <= 600; i++)
        {
            double actual = i * period + 0.3;
            Assert.Equal(0, clock.Remaining(actual, period));
            clock.Commit(actual, period);
            Assert.Equal(period - 0.3, clock.Remaining(actual, period), 6);
        }
    }

    [Fact]
    public void InputWakesDoNotAdvanceOrRestartTheDeadline()
    {
        var clock = new FramePacer();
        clock.Commit(100, 10);
        Assert.Equal(5, clock.Remaining(105, 10));
        Assert.Equal(0.5, clock.Remaining(109.5, 10));
        Assert.Equal(0, clock.Remaining(110, 10));
    }

    [Theory]
    [InlineData(11.1)]
    [InlineData(1000)]
    public void AStallRebasesWithoutACatchUpBurst(double delayed)
    {
        var clock = new FramePacer();
        clock.Commit(0, 10);
        Assert.Equal(0, clock.Remaining(delayed, 10));
        clock.Commit(delayed, 10);
        Assert.Equal(10, clock.Remaining(delayed, 10), 6);
        Assert.True(clock.Remaining(delayed + 1, 10) > 0);
    }

    [Fact]
    public void ARefreshRateChangeRecomputesTheNextDeadline()
    {
        var clock = new FramePacer();
        clock.Commit(100, 20);
        Assert.Equal(5, clock.Remaining(105, 10));
        Assert.Equal(25, clock.Remaining(105, 30));
    }

    [Fact]
    public void TheFirstFrameAfterIdleIsImmediatelyDue()
    {
        var clock = new FramePacer();
        clock.Commit(100, 10);
        clock.Reset();
        Assert.Equal(0, clock.Remaining(1_000_000, 10));
        clock.Commit(1_000_000, 10);
        Assert.Equal(10, clock.Remaining(1_000_000, 10));
    }
}
