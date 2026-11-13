using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Taj.Core.Sources;

namespace Shubbak.InputProbe;

/// <summary>Measures the real source reader, with identical child binaries across runtimes.</summary>
internal static class ProcessSourcesRun
{
    internal static int Child(string[] args)
    {
        int seconds = Program.Number(args, "--seconds", 90, 2, 180);
        Console.WriteLine(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        Console.Out.Flush();
        // Quiet output is the revealing case: an async-over-sync read pins a worker
        // for the child's lifetime, even though no managed work is ready to run.
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        return 0;
    }

    internal static async Task<int> Run(string[] args)
    {
        int count = Program.Number(args, "--sources", 16, 1, 32);
        int seconds = Program.Number(args, "--seconds", 15, 2, 120);
        string child = Program.Text(args, "--child") ?? throw new ArgumentException("Supply a frozen --child binary.");
        if (!File.Exists(child)) throw new FileNotFoundException("Child binary is missing.", child);
        using Process process = Process.GetCurrentProcess();
        var sources = new ProcessSource[count];
        var ready = new TaskCompletionSource<int>[count];
        var pids = new int[count];
        var before = ProcessSourceSnapshot.Take(process);
        ProcessSourceSnapshot idle = before, after = before;
        double startupMs = 0, idleSeconds = 0, idleCpuMs = 0, disposeMs = 0;
        long idleAllocated = 0;
        try
        {
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < count; i++)
            {
                var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                ready[i] = completion;
                sources[i] = new ProcessSource($"probe-{i}", $"\"{child}\" source-child --seconds {seconds + 60}");
                sources[i].Changed += source =>
                {
                    if (int.TryParse(source.Value, CultureInfo.InvariantCulture, out int pid)) completion.TrySetResult(pid);
                };
                sources[i].Start();
            }
            int[] reported = await Task.WhenAll(ready.Select(t => t.Task)).WaitAsync(TimeSpan.FromSeconds(45)).ConfigureAwait(false);
            reported.CopyTo(pids, 0);
            if (pids.Distinct().Count() != count) throw new InvalidOperationException("Sources did not report distinct children.");
            startupMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            var settled = ProcessSourceSnapshot.Take(process);
            start = Stopwatch.GetTimestamp();
            await Task.Delay(TimeSpan.FromSeconds(seconds)).ConfigureAwait(false);
            idleSeconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
            idle = ProcessSourceSnapshot.Take(process);
            idleCpuMs = idle.CpuMs - settled.CpuMs;
            idleAllocated = idle.AllocatedBytes - settled.AllocatedBytes;
        }
        finally
        {
            long start = Stopwatch.GetTimestamp();
            foreach (ProcessSource? source in sources) source?.Dispose();
            disposeMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            // Include children that became ready just before a startup failure.
            for (int i = 0; i < count; i++)
                if (ready[i]?.Task.IsCompletedSuccessfully == true) pids[i] = ready[i].Task.Result;
            long deadline = Stopwatch.GetTimestamp();
            while (pids.Any(IsAlive) && Stopwatch.GetElapsedTime(deadline).TotalSeconds < 5)
                await Task.Delay(20).ConfigureAwait(false);
            after = ProcessSourceSnapshot.Take(process);
        }
        int remaining = pids.Count(IsAlive);
        var report = new ProcessSourcesReport(!RuntimeFeature.IsDynamicCodeSupported, Environment.Version.ToString(),
            count, pids.Count(p => p > 0), remaining, startupMs, idleSeconds, idleCpuMs, idleAllocated, disposeMs, before, idle, after);
        Console.WriteLine(JsonSerializer.Serialize(report, ProbeJson.Default.ProcessSourcesReport));
        return remaining == 0 ? 0 : 1;
    }

    private static bool IsAlive(int pid)
    {
        if (pid == 0) return false;
        try { using Process child = Process.GetProcessById(pid); return !child.HasExited; }
        catch (ArgumentException) { return false; }
    }
}

internal sealed record ProcessSourceSnapshot(double CpuMs, long PrivateBytes, long WorkingSetBytes,
    int Threads, int Handles, long HeapBytes, long AllocatedBytes)
{
    internal static ProcessSourceSnapshot Take(Process process)
    {
        process.Refresh();
        return new(process.TotalProcessorTime.TotalMilliseconds, process.PrivateMemorySize64, process.WorkingSet64,
            process.Threads.Count, process.HandleCount, GC.GetTotalMemory(false), GC.GetTotalAllocatedBytes(true));
    }
}

internal sealed record ProcessSourcesReport(bool NativeAot, string Runtime, int Sources, int Ready, int ChildrenRemaining,
    double StartupMs, double IdleSeconds, double IdleCpuMs, long IdleAllocatedBytes, double DisposeMs,
    ProcessSourceSnapshot Before, ProcessSourceSnapshot Idle, ProcessSourceSnapshot After);
