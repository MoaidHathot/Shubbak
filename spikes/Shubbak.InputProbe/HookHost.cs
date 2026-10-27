using System.Diagnostics;
using System.Runtime;
using Shubbak.Config;
using Shubbak.Core.Commands;
using Shubbak.Native;
using Shubbak.Wm;

namespace Shubbak.InputProbe;

/// <summary>
/// Runs the production hook and lookup in a separate process. The probe observes
/// hook-thread allocations across calls, and lookup cost; the external receiver
/// measures delivery including pauses before this process can enter its callback.
/// </summary>
internal sealed class HookHost
{
    private readonly BindingTable _bindings = new();
    private readonly double[] _lookup = new double[1_000_000];
    private int _seen, _count;
    private long _allocatedBefore, _allocatedAfter;

    private HookHost()
    {
        var bindings = new List<Keybinding>();
        for (int i = 0; i < 100; i++)
            bindings.Add(new Keybinding(new KeyBinding((int)KeyModifiers.Alt, (ushort)(i + 8), $"probe-{i}"), [], default));
        _bindings.Load(new ShubbakConfig { Keybindings = bindings });
    }

    private bool Probe(ushort key, KeyModifiers modifiers, bool down)
    {
        if (key != 0x87 || !down) return _bindings.IsBound(key, modifiers, down);

        long start = Stopwatch.GetTimestamp();
        bool result = _bindings.IsBound(key, modifiers, down);
        long end = Stopwatch.GetTimestamp();
        int seen = ++_seen;
        if (seen == 500) _allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        if (seen > 500)
        {
            if (_count < _lookup.Length) _lookup[_count++] = (end - start) * 1000.0 / Stopwatch.Frequency;
            _allocatedAfter = GC.GetAllocatedBytesForCurrentThread();
        }
        return result;
    }

    internal static int Run(string[] args)
    {
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        int seconds = Program.Number(args, "--seconds", 120, 1, 3600);
        var host = new HookHost();
        using var source = new KeyboardSource();
        bool hooked = !args.Contains("--no-hook", StringComparer.Ordinal);
        if (hooked) source.Start(host.Probe);
        using var load = new Load(cpuThreads: 0, gc: args.Contains("--gc", StringComparer.Ordinal));
        int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
        Console.WriteLine("ready");
        Load.WaitForStop(seconds);
        load.Dispose();
        source.Dispose();
        Program.Report(new ProbeReport
        {
            Label = "production-hook-host", Received = host._seen,
            AllocatedBytes = host._allocatedAfter - host._allocatedBefore,
            Gen0 = GC.CollectionCount(0) - gen0, Gen1 = GC.CollectionCount(1) - gen1, Gen2 = GC.CollectionCount(2) - gen2,
            Dropped = source.Dropped, Faults = source.Faults,
            Lookup = Distribution.Of(host._lookup.AsSpan(0, host._count).ToArray()),
        });
        return source.Faults == 0 && source.Dropped == 0 ? 0 : 2;
    }
}
