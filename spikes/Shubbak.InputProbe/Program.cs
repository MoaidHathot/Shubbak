using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shubbak.Native;

namespace Shubbak.InputProbe;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] == "--help")
            {
                Console.WriteLine("shubbak-input-probe receive|hook-host|cpu-load|churn|gpu-load|perf|palette-ui [--samples 20000] [--warmup 1000] [--interval-us 2000] [--burst 1] [--label name] [--raw-output file] [--seconds 60] [--threads 28] [--gc] [--no-hook] [--bound --instance name] [--animate]");
                Console.WriteLine("perf: --kind ipc|layout|resize|query --operations 20000 [--windows 32] [--query name --instance name --target-pid PID]; palette-ui: --target-pid PID [--operations 100]");
                return 0;
            }

            PowerThrottling.OptOut();
            return args[0] switch
            {
                "receive" => InputRun.Run(args),
                "hook-host" => HookHost.Run(args),
                "cpu-load" => Load.Run(args),
                "churn" => Churn.Run(args).GetAwaiter().GetResult(),
                "gpu-load" => GpuLoad.Run(args).GetAwaiter().GetResult(),
                "perf" => PerformanceRun.Run(args).GetAwaiter().GetResult(),
                "palette-ui" => PaletteRun.Run(args).GetAwaiter().GetResult(),
                _ => throw new ArgumentException("Unknown probe mode."),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    internal static int Number(string[] args, string name, int fallback, int minimum, int maximum)
    {
        string? value = Text(args, name);
        int number = value is null ? fallback : int.Parse(value, CultureInfo.InvariantCulture);
        return number >= minimum && number <= maximum
            ? number
            : throw new ArgumentOutOfRangeException(name, $"Expected {minimum}..{maximum}.");
    }

    internal static string? Text(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        if (index < 0) return null;
        return index + 1 < args.Length ? args[index + 1] : throw new ArgumentException($"Missing value for {name}.");
    }

    internal static void Report(ProbeReport report) =>
        Console.WriteLine(JsonSerializer.Serialize(report, ProbeJson.Default.ProbeReport));
}

internal sealed record Distribution(int Count, double P50Ms, double P95Ms, double P99Ms, double P999Ms, double MaxMs,
    int Over1Ms, int Over4Ms, int Over8Ms, int Over16Ms)
{
    internal static Distribution Of(double[] samples)
    {
        Array.Sort(samples);
        double At(double percentile) => samples.Length == 0 ? 0 : samples[Math.Clamp((int)Math.Ceiling(samples.Length * percentile) - 1, 0, samples.Length - 1)];
        return new(samples.Length, At(.5), At(.95), At(.99), At(.999), At(1),
            samples.Count(v => v >= 1), samples.Count(v => v >= 4), samples.Count(v => v >= 8), samples.Count(v => v >= 16));
    }
}

internal sealed record ProbeReport
{
    public string Label { get; init; } = "";
    public string Measurement { get; init; } = "window-message";
    public bool NativeAot { get; init; } = !RuntimeFeature.IsDynamicCodeSupported;
    public long ClockFrequency { get; init; } = System.Diagnostics.Stopwatch.Frequency;
    public double Seconds { get; init; }
    public double EventsPerSecond { get; init; }
    public int Sent { get; init; }
    public int Received { get; init; }
    public int Missing { get; init; }
    public int Duplicates { get; init; }
    public int OutOfOrder { get; init; }
    public int WrongEdges { get; init; }
    public int GeometryChanges { get; init; }
    public int LeakedBoundEvents { get; init; }
    public long AllocatedBytes { get; init; }
    public double CpuMs { get; init; }
    public double PrivateMiB { get; init; }
    public long Gen0 { get; init; }
    public long Gen1 { get; init; }
    public long Gen2 { get; init; }
    public long Dropped { get; init; }
    public long Faults { get; init; }
    public Distribution? Delivery { get; init; }
    public Distribution? SendCall { get; init; }
    public Distribution? Lookup { get; init; }
}

[JsonSerializable(typeof(ProbeReport))]
[JsonSerializable(typeof(PerformanceReport))]
internal sealed partial class ProbeJson : JsonSerializerContext;
