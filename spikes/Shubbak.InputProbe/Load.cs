namespace Shubbak.InputProbe;

/// <summary>Bounded synthetic loads, confined to the benchmark process.</summary>
internal sealed class Load : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Thread> _threads = [];
    private bool _disposed;

    internal Load(int cpuThreads, bool gc)
    {
        for (int i = 0; i < cpuThreads; i++) Start(Cpu, $"input-probe-cpu-{i}");
        if (gc)
        {
            Start(Allocate, "input-probe-allocator");
            Start(Collect, "input-probe-collector");
        }
    }

    private void Start(ThreadStart work, string name)
    {
        var thread = new Thread(work) { IsBackground = true, Name = name };
        _threads.Add(thread);
        thread.Start();
    }

    private void Cpu()
    {
        double value = 1;
        while (!_stop.IsCancellationRequested)
            for (int i = 0; i < 10_000; i++) value = Math.Sqrt(value + 1.234567);
        GC.KeepAlive(value);
    }

    private void Allocate()
    {
        // A bounded live heap (~16 MiB) with short-lived churn beside it.
        var retained = new byte[128][];
        int slot = 0;
        while (!_stop.IsCancellationRequested)
        {
            for (int i = 0; i < 128; i++)
            {
                var scratch = new byte[8192];
                scratch[0] = 1;
                GC.KeepAlive(scratch);
            }
            retained[slot++ & 127] = new byte[128 * 1024];
            if (_stop.Token.WaitHandle.WaitOne(10)) break;
        }
        GC.KeepAlive(retained);
    }

    private void Collect()
    {
        while (!_stop.Token.WaitHandle.WaitOne(200))
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    internal static void WaitForStop(int seconds)
    {
        // EOF from the owning controller ends a run promptly. The timer also bounds
        // a child whose controller dies without closing its pipe normally.
        _ = Task.Run(Console.ReadLine).Wait(TimeSpan.FromSeconds(seconds));
    }

    internal static int Run(string[] args)
    {
        int threads = Program.Number(args, "--threads", Math.Max(1, Environment.ProcessorCount - 4), 1, 128);
        int seconds = Program.Number(args, "--seconds", 120, 1, 3600);
        using var load = new Load(threads, gc: false);
        Console.WriteLine("ready");
        WaitForStop(seconds);
        return 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        foreach (Thread thread in _threads)
            if (!thread.Join(TimeSpan.FromSeconds(5))) throw new TimeoutException($"Load thread {thread.Name} did not stop.");
        _stop.Dispose();
    }
}
