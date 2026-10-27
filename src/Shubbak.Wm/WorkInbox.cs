using System.Diagnostics;

namespace Shubbak.Wm;

/// <summary>FIFO work for the daemon, with a bounded turn so producers cannot starve input/frames.</summary>
internal sealed class WorkInbox
{
    private readonly Queue<Action> _queue = new();
    private readonly Lock _gate = new();
    internal const int MaximumBatch = 32;
    private static readonly long BudgetTicks = Stopwatch.Frequency / 500; // two milliseconds

    internal void Enqueue(Action work)
    {
        lock (_gate) _queue.Enqueue(work);
    }

    internal (int Ran, bool More) Drain(Action<Exception> faulted)
    {
        int budget;
        lock (_gate) budget = Math.Min(_queue.Count, MaximumBatch);
        if (budget == 0) return (0, false);
        long start = Stopwatch.GetTimestamp();
        int ran = 0;
        while (ran < budget && (ran == 0 || Stopwatch.GetTimestamp() - start < BudgetTicks))
        {
            Action work;
            lock (_gate) work = _queue.Dequeue();
            ran++;
            try { work(); }
            catch (Exception ex) { faulted(ex); }
        }
        lock (_gate) return (ran, _queue.Count != 0);
    }
}
