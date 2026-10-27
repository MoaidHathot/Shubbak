using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Shubbak.Core.Geometry;
using Shubbak.Core.Layouts;
using Shubbak.Core.Tree;
using Shubbak.Ipc;

namespace Shubbak.InputProbe;

/// <summary>Equal-work, warmed native measurements; no real desktop input is needed.</summary>
internal static class PerformanceRun
{
    internal static async Task<int> Run(string[] args)
    {
        string kind = Program.Text(args, "--kind") ?? "layout";
        int count = Program.Number(args, "--operations", 20_000, 1, 1_000_000);
        int windows = Program.Number(args, "--windows", 32, 2, 1024);
        if (kind == "query") return await Queries(args, count).ConfigureAwait(false);
        if (kind == "ipc") await Ipc(count, Program.Number(args, "--payload-size", 4096, 0, 100_000)).ConfigureAwait(false);
        else Layout(kind, count, windows);
        return 0;
    }

    private static void Layout(string kind, int count, int windows)
    {
        if (kind is not ("layout" or "resize")) throw new ArgumentException("Expected layout, resize or ipc.");
        var root = new RootNode();
        var monitor = new MonitorNode("probe", new Rect(0, 0, 3840, 2160), new Rect(0, 32, 3840, 2128));
        var workspace = new WorkspaceNode("probe");
        root.AddMonitor(monitor);
        monitor.AddWorkspace(workspace);
        var containers = new List<ContainerNode> { workspace };
        ContainerNode parent = workspace;
        WindowNode? first = null;
        for (int i = 0; i < windows; i++)
        {
            if (i % 8 == 0)
            {
                parent = new ContainerNode(FibonacciLayout.Horizontal);
                workspace.Add(parent);
                containers.Add(parent);
            }
            var window = new WindowNode(i + 1, new WindowIdentity { Title = "probe", ClassName = "probe", ProcessName = "probe" });
            if (i % 5 == 4) window.State = WindowState.Floating;
            parent.Add(window);
            first ??= window;
        }
        ILayout[] layouts = [.. LayoutRegistry.All];
        var engine = new LayoutEngine();
        var options = new ArrangeOptions(Gaps.All(8), 6, Focused: first);
        long checksum = 0;
        void Operation(int i)
        {
            foreach (ContainerNode container in containers) container.Layout = layouts[i % layouts.Length];
            IReadOnlyList<Placement> placements = engine.Arrange(root, options);
            if (placements.Count != windows) throw new InvalidOperationException("Missing layout placements.");
            // Stable geometry checksum, independent of node ids and process hash seeds.
            for (int j = 0; j < placements.Count; j++)
            {
                Rect r = placements[j].Rect;
                checksum = unchecked(checksum * 31 + r.X + r.Y * 7L + r.Width * 17L + r.Height * 37L);
            }
            if (kind == "resize")
                _ = LayoutEngine.RatioForExtent((ContainerNode)first!.Parent!, first, Axis.Horizontal, 200 + i % 200, new LayoutOptions(6));
        }
        for (int i = 0; i < 1000; i++) Operation(i);
        checksum = 0;
        using Process process = Process.GetCurrentProcess();
        var before = Sample.Take(process);
        for (int i = 0; i < count; i++) Operation(i);
        Report(kind, count, checksum, before, process);
    }

    private static async Task Ipc(int count, int payloadSize)
    {
        string pipe = $"shubbak-perf-{Guid.NewGuid():N}";
        string payload = new('x', payloadSize);
        string answer = JsonSerializer.Serialize(payload, IpcJsonContext.Default.String);
        await using var server = new IpcServer { PipeName = pipe };
        server.Start(request => Task.FromResult(new IpcResponse(request.Id, true, answer)));
        await using var client = new IpcClient { PipeName = pipe };
        using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await client.ConnectAsync(TimeSpan.FromSeconds(5), limit.Token).ConfigureAwait(false);
        for (int i = 0; i < 1000; i++) await client.SendAsync("query", payload, limit.Token).ConfigureAwait(false);
        using Process process = Process.GetCurrentProcess();
        var before = Sample.Take(process);
        for (int i = 0; i < count; i++)
        {
            IpcResponse response = await client.SendAsync("query", payload, limit.Token).ConfigureAwait(false);
            if (!response.Ok || response.Data != answer) throw new InvalidOperationException("IPC response mismatch.");
        }
        Report("ipc", count, (long)count * payloadSize, before, process);
    }

    private readonly record struct Sample(long Time, long Allocated, double Cpu, int Gen0, int Gen1, int Gen2)
    {
        internal static Sample Take(Process p) => new(Stopwatch.GetTimestamp(), GC.GetTotalAllocatedBytes(true),
            p.TotalProcessorTime.TotalMilliseconds, GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
    }

    private static void Report(string kind, int count, long checksum, Sample before, Process process,
        double? targetCpu = null, long? targetPrivate = null, Distribution? latency = null)
    {
        long allocated = GC.GetTotalAllocatedBytes(true) - before.Allocated;
        double seconds = Stopwatch.GetElapsedTime(before.Time).TotalSeconds;
        process.Refresh();
        double cpu = process.TotalProcessorTime.TotalMilliseconds - before.Cpu;
        var report = new PerformanceReport(kind, !RuntimeFeature.IsDynamicCodeSupported, count, seconds,
            allocated, (double)allocated / count, cpu, cpu / count, process.PrivateMemorySize64,
            GC.GetTotalMemory(false), GC.CollectionCount(0) - before.Gen0,
            GC.CollectionCount(1) - before.Gen1, GC.CollectionCount(2) - before.Gen2, checksum,
            targetCpu, targetPrivate, latency);
        Console.WriteLine(JsonSerializer.Serialize(report, ProbeJson.Default.PerformanceReport));
    }

    private static async Task<int> Queries(string[] args, int count)
    {
        string instance = Program.Text(args, "--instance") ?? throw new ArgumentException("Query benchmark needs --instance.");
        string query = Program.Text(args, "--query") ?? "state";
        string[] queries = query == "legacy-palette"
            ? ["all-windows", "commands", "workspaces", "layouts", "bindings", "monitors", "state", "contexts", "arrangements"]
            : [query];
        int pid = Program.Number(args, "--target-pid", 0, 1, int.MaxValue);
        using Process target = Process.GetProcessById(pid);
        using Process process = Process.GetCurrentProcess();
        using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var client = new IpcClient { PipeName = IpcProtocol.PipeNameForInstance(instance) };
        await client.ConnectAsync(TimeSpan.FromSeconds(5), limit.Token).ConfigureAwait(false);
        var samples = new double[count];
        async Task Read()
        {
            foreach (string what in queries)
            {
                IpcResponse reply = await client.SendAsync("query", what, limit.Token).ConfigureAwait(false);
                if (!reply.Ok || reply.Data is null) throw new InvalidOperationException(reply.Error ?? "Empty query response.");
            }
        }
        for (int i = 0; i < 10; i++) await Read().ConfigureAwait(false);
        target.Refresh();
        double targetBefore = target.TotalProcessorTime.TotalMilliseconds;
        var before = Sample.Take(process);
        for (int i = 0; i < count; i++)
        {
            long start = Stopwatch.GetTimestamp();
            await Read().ConfigureAwait(false);
            samples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        target.Refresh();
        double cpu = target.TotalProcessorTime.TotalMilliseconds - targetBefore;
        Report(query, count, 0, before, process, cpu, target.PrivateMemorySize64, Distribution.Of(samples));
        return 0;
    }
}

internal sealed record PerformanceReport(string Kind, bool NativeAot, int Operations, double Seconds,
    long AllocatedBytes, double BytesPerOperation, double CpuMs, double CpuMsPerOperation,
    long PrivateBytes, long HeapBytes, int Gen0, int Gen1, int Gen2, long Checksum,
    double? TargetCpuMs = null, long? TargetPrivateBytes = null, Distribution? Latency = null);
