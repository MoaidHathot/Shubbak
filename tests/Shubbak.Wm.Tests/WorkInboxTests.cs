namespace Shubbak.Wm.Tests;

public sealed class WorkInboxTests
{
    [Fact]
    public void ReplenishingTheQueueCannotExtendTheCurrentTurn()
    {
        var inbox = new WorkInbox();
        int ran = 0;
        Action? producer = null;
        producer = () => { if (++ran < 1000) inbox.Enqueue(producer!); };
        inbox.Enqueue(producer);
        (int first, bool more) = inbox.Drain(static ex => throw new Xunit.Sdk.XunitException(ex.ToString()));
        Assert.Equal(1, first);
        Assert.True(more);
        int frameOpportunities = 1;
        while (more)
        {
            (_, more) = inbox.Drain(static ex => throw new Xunit.Sdk.XunitException(ex.ToString()));
            frameOpportunities++;
        }
        Assert.Equal(1000, ran);
        Assert.Equal(1000, frameOpportunities);
    }

    [Fact]
    public void BatchesPreserveFifoAndContinueAfterAFailedItem()
    {
        var inbox = new WorkInbox();
        var order = new List<int>();
        for (int i = 0; i < 100; i++)
        {
            int value = i;
            inbox.Enqueue(() => { order.Add(value); if (value == 10) throw new InvalidOperationException("deliberate"); });
        }
        int faults = 0;
        (int ran, bool more) = inbox.Drain(_ => faults++);
        Assert.InRange(ran, 1, WorkInbox.MaximumBatch);
        Assert.True(more);
        while (more) (_, more) = inbox.Drain(_ => faults++);
        Assert.Equal(1, faults);
        Assert.Equal(Enumerable.Range(0, 100), order);
    }

    [Fact]
    public void ASmallNumberOfSlowItemsAlsoYieldsToInputAndFrames()
    {
        var inbox = new WorkInbox();
        int ran = 0;
        inbox.Enqueue(() => { Thread.Sleep(10); ran++; });
        inbox.Enqueue(() => ran++);
        (int count, bool more) = inbox.Drain(static ex => throw new Xunit.Sdk.XunitException(ex.ToString()));
        Assert.Equal(1, count);
        Assert.Equal(1, ran);
        Assert.True(more);
        Assert.Equal((1, false), inbox.Drain(static ex => throw new Xunit.Sdk.XunitException(ex.ToString())));
    }
}
