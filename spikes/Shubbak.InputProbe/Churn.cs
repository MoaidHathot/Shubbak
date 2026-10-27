using System.Diagnostics;
using Shubbak.Ipc;

namespace Shubbak.InputProbe;

internal static class Churn
{
    internal static async Task<int> Run(string[] args)
    {
        string instance = Program.Text(args, "--instance") ?? throw new ArgumentException("Churn needs --instance.");
        int seconds = Program.Number(args, "--seconds", 120, 1, 3600);
        bool animate = args.Contains("--animate", StringComparer.Ordinal);
        int intervalMs = Program.Number(args, "--interval-ms", animate ? 80 : 0, 0, 10_000);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        _ = Task.Run(() =>
        {
            Console.ReadLine();
            try { stop.Cancel(); }
            catch (ObjectDisposedException) { }
        });
        await using var client = new IpcClient { PipeName = IpcProtocol.PipeNameForInstance(instance) };
        await client.ConnectAsync(TimeSpan.FromSeconds(5), stop.Token).ConfigureAwait(false);
        Console.WriteLine("ready");
        int count = 0;
        long start = Stopwatch.GetTimestamp();
        try
        {
            while (!stop.IsCancellationRequested)
            {
                IpcResponse reply = await client.SendAsync(
                    animate ? "command" : "query",
                    animate ? (count % 2 == 0 ? "gaps --outer 4" : "gaps --outer 80") : "state",
                    stop.Token).ConfigureAwait(false);
                if (!reply.Ok) throw new InvalidOperationException(reply.Error);
                count++;
                if (intervalMs > 0) await Task.Delay(intervalMs, stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        Program.Report(new ProbeReport { Label = animate ? "animation-load" : "ipc-load", Received = count, Seconds = Stopwatch.GetElapsedTime(start).TotalSeconds });
        return 0;
    }
}
