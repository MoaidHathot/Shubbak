namespace Shubbak.Spike;

/// <summary>
/// Deliberately hostile GC load generator. S1 and S2 are only meaningful if the
/// hot path is measured while the GC is actually working, including blocking
/// Gen2 compactions - the worst case a real WM would ever see.
/// </summary>
internal sealed class GcPressure : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Thread> _threads = [];
    private readonly List<byte[]> _survivors = [];

    private long _gen0Start, _gen1Start, _gen2Start;
    private long _allocatedBytes;

    public void Start(int allocatorThreads = 2, bool forceBlockingGen2 = true)
    {
        _gen0Start = GC.CollectionCount(0);
        _gen1Start = GC.CollectionCount(1);
        _gen2Start = GC.CollectionCount(2);

        for (int i = 0; i < allocatorThreads; i++)
        {
            var t = new Thread(AllocateLoop) { IsBackground = true, Name = $"gc-pressure-{i}" };
            t.Start();
            _threads.Add(t);
        }

        if (forceBlockingGen2)
        {
            var t = new Thread(BlockingGen2Loop) { IsBackground = true, Name = "gc-gen2" };
            t.Start();
            _threads.Add(t);
        }
    }

    private void AllocateLoop()
    {
        var rng = new Random(Environment.CurrentManagedThreadId);
        var token = _cts.Token;

        while (!token.IsCancellationRequested)
        {
            // Mixed sizes: SOH churn plus periodic LOH allocations, which are the
            // ones most likely to trigger a long pause.
            for (int i = 0; i < 512 && !token.IsCancellationRequested; i++)
            {
                int size = rng.Next(64, 8 * 1024);
                var block = new byte[size];
                block[0] = 1;
                Interlocked.Add(ref _allocatedBytes, size);

                // Keep ~1 in 64 alive so objects get promoted rather than dying in Gen0.
                if ((i & 63) == 0)
                {
                    lock (_survivors)
                    {
                        _survivors.Add(block);
                        if (_survivors.Count > 4096) _survivors.RemoveRange(0, 2048);
                    }
                }
            }

            // Large Object Heap allocation.
            var loh = new byte[120 * 1024];
            loh[0] = 1;
            Interlocked.Add(ref _allocatedBytes, loh.Length);
        }
    }

    private void BlockingGen2Loop()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            // Blocking, compacting, full collection: the single worst pause a
            // managed hook callback can be interrupted by.
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            try { Task.Delay(250, token).Wait(token); } catch (OperationCanceledException) { return; }
        }
    }

    public GcReport Stop()
    {
        _cts.Cancel();
        foreach (var t in _threads) t.Join(TimeSpan.FromSeconds(5));

        return new GcReport
        {
            Gen0 = GC.CollectionCount(0) - _gen0Start,
            Gen1 = GC.CollectionCount(1) - _gen1Start,
            Gen2 = GC.CollectionCount(2) - _gen2Start,
            AllocatedMb = Interlocked.Read(ref _allocatedBytes) / (1024.0 * 1024.0),
        };
    }

    public void Dispose() => _cts.Dispose();

    public struct GcReport
    {
        public long Gen0, Gen1, Gen2;
        public double AllocatedMb;

        public override string ToString() =>
            $"gen0={Gen0} gen1={Gen1} gen2={Gen2} allocated={AllocatedMb:F0} MB";
    }
}
