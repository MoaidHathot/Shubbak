using System.Diagnostics;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Shubbak.Spike;

/// <summary>
/// S3 - NativeAOT viability self-report.
///
/// Prints the numbers that matter for shipping Shubbak as a background daemon:
/// binary size, cold start, working set, and whether we are actually running AOT.
/// The comparison between JIT and AOT runs is produced by tools/run-p0.ps1, which
/// invokes this in both configurations.
/// </summary>
internal static class S3AotReport
{
    public static int Run(string[] args)
    {
        var proc = Process.GetCurrentProcess();
        bool isAot = !RuntimeFeature.IsDynamicCodeSupported;

        string exePath = Environment.ProcessPath ?? "";
        long exeSize = exePath.Length > 0 && File.Exists(exePath) ? new FileInfo(exePath).Length : 0;

        // Total on-disk footprint of what we would actually ship.
        long dirSize = 0;
        int fileCount = 0;
        if (exePath.Length > 0)
        {
            var dir = Path.GetDirectoryName(exePath);
            if (dir is not null && Directory.Exists(dir))
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    dirSize += new FileInfo(f).Length;
                    fileCount++;
                }
            }
        }

        Console.WriteLine("=== S3: NativeAOT viability ===");
        Console.WriteLine();
        Console.WriteLine($"Runtime            : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"Compilation        : {(isAot ? "NativeAOT (no JIT)" : "JIT")}");
        Console.WriteLine($"Architecture       : {RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine($"OS                 : {RuntimeInformation.OSDescription}");
        Console.WriteLine();
        Console.WriteLine($"Executable         : {exePath}");
        Console.WriteLine($"Executable size    : {exeSize / 1024.0 / 1024.0,8:F2} MB");
        Console.WriteLine($"Deploy dir size    : {dirSize / 1024.0 / 1024.0,8:F2} MB  ({fileCount} files)");
        Console.WriteLine();
        Console.WriteLine("Startup            : measured externally by tools/run-p0.ps1 " +
                          "(`shubbak-spike ping`), since self-measurement drags in the " +
                          "diagnostics stack and pollutes the number.");
        Console.WriteLine($"Working set        : {proc.WorkingSet64 / 1024.0 / 1024.0,8:F2} MB");
        Console.WriteLine($"Private memory     : {proc.PrivateMemorySize64 / 1024.0 / 1024.0,8:F2} MB");
        Console.WriteLine($"GC heap            : {GC.GetTotalMemory(false) / 1024.0 / 1024.0,8:F2} MB");
        Console.WriteLine();
        Console.WriteLine($"GC flavour         : {(GCSettings.IsServerGC ? "server" : "workstation")}");
        Console.WriteLine($"GC latency mode    : {GCSettings.LatencyMode}");
        Console.WriteLine($"Processor count    : {Environment.ProcessorCount}");
        Console.WriteLine();

        // A daemon idles for days; steady-state memory matters more than startup.
        Console.WriteLine("Settling for 2s to sample steady-state working set...");
        Thread.Sleep(2000);
        proc.Refresh();
        Console.WriteLine($"Working set (idle) : {proc.WorkingSet64 / 1024.0 / 1024.0,8:F2} MB");
        Console.WriteLine();

        // Budgets we would want the real daemon to live inside.
        bool sizeOk = exeSize == 0 || exeSize < 30 * 1024 * 1024;
        bool memOk = proc.WorkingSet64 < 60L * 1024 * 1024;

        Console.WriteLine($"BUDGET exe < 30 MB      : {(sizeOk ? "PASS" : "FAIL")}");
        Console.WriteLine($"BUDGET RSS < 60 MB      : {(memOk ? "PASS" : "FAIL")}");
        Console.WriteLine();
        Console.WriteLine($"S3: {(sizeOk && memOk ? "PASS" : "REVIEW")}  " +
                          $"(mode={(isAot ? "AOT" : "JIT")})");

        return 0;
    }
}
