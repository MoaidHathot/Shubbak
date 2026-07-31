

namespace Shubbak.Spike;

/// <summary>
/// Shubbak P0 de-risking spike.
///
/// Purpose: produce measurements that decide whether .NET 10 is a viable
/// implementation language for a Windows tiling window manager, specifically on
/// the two hot paths where managed code could plausibly fail:
///
///   S1  WH_KEYBOARD_LL callback latency under GC pressure
///   S2  Animation frame pacing at 144 Hz with batched window moves
///   S3  NativeAOT binary size / startup / memory
///   S4  WinEvent hook fidelity (live window titles) and event volume
///
/// This is throwaway code. Its only output is docs/adr/0001-language-choice.md.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {


        if (args.Length == 0 || ArgUtil.HasFlag(args, "--help") || ArgUtil.HasFlag(args, "-h"))
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        string spike = args[0].ToLowerInvariant();

        // Minimal-work mode used by tools/run-p0.ps1 to measure process startup
        // EXTERNALLY. Self-measuring startup from inside the process is unreliable:
        // touching Process.GetCurrentProcess().StartTime drags in the diagnostics
        // stack, and its cost lands inside the number being reported.
        if (spike == "ping") return 0;

        try
        {
            return spike switch
            {
                "s1" => S1KeyboardHook.Run(args),
                "s2" => S2AnimationTiming.Run(args),
                "s3" => S3AotReport.Run(args),
                "s4" => S4WinEventFidelity.Run(args),
                _ => Unknown(spike),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"FATAL in {spike}: {ex}");
            return 2;
        }
    }

    private static int Unknown(string spike)
    {
        Console.Error.WriteLine($"Unknown spike '{spike}'.");
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Shubbak P0 de-risking spike

            USAGE
              shubbak-spike <spike> [options]

            SPIKES
              s1    WH_KEYBOARD_LL callback latency under GC pressure
                    --events N          number of key events to inject   (default 1000000)
                    --alloc-threads N   GC pressure allocator threads     (default 2)
                    --no-gc-pressure    run without GC load (control group)
                    --interactive       measure real typing instead of injection

              s2    Animation frame timing
                    --windows N         windows to animate per frame      (default 20)
                    --hz N              target frame rate                 (default 144)
                    --seconds N         run duration                      (default 60)
                    --no-batch          use individual SetWindowPos (control group)
                    --no-gc-pressure    run without GC load

              s3    NativeAOT viability self-report (size / startup / memory)

              s4    WinEvent hook fidelity and volume  [interactive]
                    --seconds N         run duration                      (default 60)

            EXAMPLES
              shubbak-spike s1 --events 1000000
              shubbak-spike s1 --events 1000000 --no-gc-pressure
              shubbak-spike s2 --windows 20 --hz 144 --seconds 60
              shubbak-spike s2 --windows 20 --hz 144 --seconds 60 --no-batch
              shubbak-spike s4 --seconds 90

            Run everything and capture results with:
              pwsh tools/run-p0.ps1
            """);
    }
}
