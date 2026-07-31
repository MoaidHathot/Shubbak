using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Shubbak.Spike;

/// <summary>
/// S1 - Low-level keyboard hook latency under GC pressure.
///
/// This is the single biggest risk in choosing .NET for Shubbak. If a WH_KEYBOARD_LL
/// callback does not return within LowLevelHooksTimeout (default 300 ms), Windows
/// silently unhooks us and every keybinding stops working until restart.
///
/// The design under test is the one the real WM will use:
///   - the callback allocates NOTHING; it writes a 24-byte struct into a
///     pre-allocated ring buffer and returns immediately
///   - the delegate is pinned via a static field + GCHandle so it is never collected
///     or moved while native code holds a pointer to it
///   - a separate worker thread drains the buffer and does the real work
///
/// Measured: callback entry -> exit, while a hostile GC load (including forced
/// blocking compacting Gen2 collections) runs concurrently.
///
/// PASS GATE: p99.9 &lt; 5 ms AND max &lt; 50 ms.
/// </summary>
internal static class S1KeyboardHook
{
    // The event we hand to the worker. Unmanaged struct: no GC involvement.
    private struct KeyEvent
    {
        public long TimestampTicks;
        public uint VirtualKey;
        public uint Flags;
        public bool IsKeyDown;
    }

    private const int RingCapacity = 1 << 16;

    // NOTE: CsWin32 with allowMarshaling=false gives us
    // `delegate* unmanaged[Stdcall]<...>` rather than a managed delegate. Combined
    // with [UnmanagedCallersOnly] this means there is NO marshalling stub and NO
    // delegate object for the GC to move or collect - so the classic
    // "keep the delegate alive in a static field" hazard does not exist here.
    // This is strictly better than what a naive P/Invoke binding would give us,
    // and it is what the real WM should use.
    private static RingBuffer<KeyEvent> s_ring = null!;
    private static LatencyStats s_callbackLatency = null!;
    private static long s_eventsSeen;
    private static uint s_suppressedVk;

    public static int Run(string[] args)
    {
        int targetEvents = ArgUtil.GetInt(args, "--events", 1_000_000);
        int allocThreads = ArgUtil.GetInt(args, "--alloc-threads", 2);
        bool noGcPressure = ArgUtil.HasFlag(args, "--no-gc-pressure");
        bool interactive = ArgUtil.HasFlag(args, "--interactive");

        Console.WriteLine("=== S1: WH_KEYBOARD_LL callback latency under GC pressure ===");
        Console.WriteLine($"Runtime         : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"AOT             : {(RuntimeFeature.IsDynamicCodeSupported ? "no (JIT)" : "yes (NativeAOT)")}");
        Console.WriteLine($"GC mode         : {(System.Runtime.GCSettings.IsServerGC ? "server" : "workstation")}, " +
                          $"latency={System.Runtime.GCSettings.LatencyMode}");
        Console.WriteLine($"Target events   : {targetEvents:N0}");
        Console.WriteLine($"GC pressure     : {(noGcPressure ? "disabled" : $"enabled ({allocThreads} alloc threads + forced blocking gen2)")}");
        Console.WriteLine();

        s_ring = new RingBuffer<KeyEvent>(RingCapacity);
        s_callbackLatency = new LatencyStats(targetEvents + 1024) { Name = "hook-callback" };

        // VK_F24 is chosen because nothing binds it. The hook returns 1 for it, so
        // it never reaches any application - exactly how a real bound key behaves.
        s_suppressedVk = (uint)VIRTUAL_KEY.VK_F24;

        using var pressure = new GcPressure();
        var workerCts = new CancellationTokenSource();
        var workerDone = new ManualResetEventSlim();
        long workerProcessed = 0;

        // Consumer: this is where a real WM would match the key against bindings and
        // dispatch a command. It may allocate freely - it is off the hook thread.
        var worker = new Thread(() =>
        {
            var spin = new SpinWait();
            while (!workerCts.IsCancellationRequested)
            {
                if (s_ring.TryDequeue(out KeyEvent ev))
                {
                    workerProcessed++;
                    // Simulate binding lookup allocation, to prove it does not
                    // feed back into the hook thread's latency.
                    if ((workerProcessed & 1023) == 0) _ = new string('x', 64);
                    spin.Reset();
                }
                else spin.SpinOnce();
            }
            workerDone.Set();
        })
        { IsBackground = true, Name = "s1-worker" };
        worker.Start();

        // The hook must live on a thread with a message pump.
        var hookReady = new ManualResetEventSlim();
        uint hookThreadId = 0;
        Exception? hookError = null;

        var hookThread = new Thread(() =>
        {
            try
            {
                hookThreadId = PInvoke.GetCurrentThreadId();

                UnhookWindowsHookExSafeHandle hook;
                unsafe
                {
                    hook = PInvoke.SetWindowsHookEx(
                        WINDOWS_HOOK_ID.WH_KEYBOARD_LL, &HookCallback, (SafeHandle?)null, 0);
                }

                using (hook)
                {
                    if (hook.IsInvalid)
                    {
                        hookError = new InvalidOperationException(
                            $"SetWindowsHookEx failed, GetLastError={Marshal.GetLastWin32Error()}");
                        hookReady.Set();
                        return;
                    }

                    hookReady.Set();

                    // Standard message pump. GetMessage blocks; the hook is invoked by
                    // the OS on this thread while we sit here.
                    while (PInvoke.GetMessage(out MSG msg, default, 0, 0).Value > 0)
                    {
                        PInvoke.TranslateMessage(in msg);
                        PInvoke.DispatchMessage(in msg);
                    }
                }
            }
            catch (Exception ex)
            {
                hookError = ex;
                hookReady.Set();
            }
        })
        { IsBackground = true, Name = "s1-hook" };

        hookThread.SetApartmentState(ApartmentState.STA);
        hookThread.Start();
        hookReady.Wait();

        if (hookError is not null)
        {
            Console.Error.WriteLine($"FATAL: {hookError.Message}");
            return 2;
        }

        Console.WriteLine("Hook installed.");

        if (!noGcPressure) pressure.Start(allocThreads, forceBlockingGen2: true);

        long wallStart = Qpc.Now();

        if (interactive)
        {
            Console.WriteLine("Interactive mode: type normally. Press ESC in this console to stop.");
            while (Console.ReadKey(true).Key != ConsoleKey.Escape) { }
        }
        else
        {
            Console.WriteLine("Injecting synthetic keystrokes (VK_F24, suppressed by the hook)...");
            InjectKeystrokes(targetEvents);
        }

        double wallMs = Qpc.TicksToMs(Qpc.Now() - wallStart);

        // Drain, then tear down.
        Thread.Sleep(250);
        workerCts.Cancel();
        workerDone.Wait(TimeSpan.FromSeconds(5));
        PInvoke.PostThreadMessage(hookThreadId, PInvoke.WM_QUIT, default, default);
        hookThread.Join(TimeSpan.FromSeconds(5));

        var gc = noGcPressure ? default : pressure.Stop();

        // ---- Report -----------------------------------------------------------
        var report = s_callbackLatency.Compute();

        Console.WriteLine();
        Console.WriteLine("--- Results -------------------------------------------------");
        Console.WriteLine($"Events observed : {Interlocked.Read(ref s_eventsSeen):N0}");
        Console.WriteLine($"Worker processed: {workerProcessed:N0}");
        Console.WriteLine($"Ring drops      : {s_ring.Dropped:N0}");
        Console.WriteLine($"Wall time       : {wallMs:F0} ms");
        if (!noGcPressure) Console.WriteLine($"GC activity     : {gc}");
        Console.WriteLine();
        Console.WriteLine($"Callback latency: {report}");
        Console.WriteLine();
        Console.WriteLine($"  >=  1 ms      : {s_callbackLatency.CountAtOrAbove(1.0):N0}");
        Console.WriteLine($"  >=  5 ms      : {s_callbackLatency.CountAtOrAbove(5.0):N0}");
        Console.WriteLine($"  >= 50 ms      : {s_callbackLatency.CountAtOrAbove(50.0):N0}");
        Console.WriteLine($"  >= 300 ms     : {s_callbackLatency.CountAtOrAbove(300.0):N0}   <- unhook threshold");
        Console.WriteLine();

        bool passP999 = report.P999 < 5.0;
        bool passMax = report.Max < 50.0;
        bool passNoUnhook = s_callbackLatency.CountAtOrAbove(300.0) == 0;
        bool pass = passP999 && passMax && passNoUnhook && report.Count > 0;

        Console.WriteLine($"GATE p99.9 < 5 ms   : {(passP999 ? "PASS" : "FAIL")}  ({report.P999:F4} ms)");
        Console.WriteLine($"GATE max   < 50 ms  : {(passMax ? "PASS" : "FAIL")}  ({report.Max:F4} ms)");
        Console.WriteLine($"GATE no 300 ms event: {(passNoUnhook ? "PASS" : "FAIL")}");
        Console.WriteLine();
        Console.WriteLine($"S1: {(pass ? "PASS" : "FAIL")}");

        return pass ? 0 : 1;
    }

    /// <summary>
    /// THE HOT PATH. Must not allocate, lock, or call anything that can block.
    /// Everything here is either a struct operation or a write into a
    /// pre-allocated array.
    ///
    /// [UnmanagedCallersOnly] means the OS calls straight into managed code with
    /// no marshalling stub. It also means an escaping exception would tear down
    /// the process, so nothing here may throw.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe LRESULT HookCallback(int nCode, WPARAM wParam, LPARAM lParam)
    {
        long entry = Qpc.Now();

        if (nCode < 0)
            return PInvoke.CallNextHookEx(HHOOK.Null, nCode, wParam, lParam);

        var kb = (KBDLLHOOKSTRUCT*)lParam.Value;

        uint msg = (uint)wParam.Value;
        bool isDown = msg is PInvoke.WM_KEYDOWN or PInvoke.WM_SYSKEYDOWN;

        var ev = new KeyEvent
        {
            TimestampTicks = entry,
            VirtualKey = kb->vkCode,
            Flags = (uint)kb->flags,
            IsKeyDown = isDown,
        };

        s_ring.TryEnqueue(in ev);

        // A real WM returns 1 here for a bound key to swallow it.
        bool suppress = kb->vkCode == s_suppressedVk;

        s_eventsSeen++;

        long exit = Qpc.Now();
        s_callbackLatency.Record(Qpc.TicksToMs(exit - entry));

        return suppress ? new LRESULT(1) : PInvoke.CallNextHookEx(HHOOK.Null, nCode, wParam, lParam);
    }

    private static void InjectKeystrokes(int count)
    {
        // Each SendInput of a down+up pair produces two hook callbacks.
        int pairs = Math.Max(1, count / 2);

        Span<INPUT> inputs = stackalloc INPUT[2];
        inputs[0].type = INPUT_TYPE.INPUT_KEYBOARD;
        inputs[0].Anonymous.ki.wVk = VIRTUAL_KEY.VK_F24;
        inputs[0].Anonymous.ki.dwFlags = 0;
        inputs[1].type = INPUT_TYPE.INPUT_KEYBOARD;
        inputs[1].Anonymous.ki.wVk = VIRTUAL_KEY.VK_F24;
        inputs[1].Anonymous.ki.dwFlags = KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP;

        int reportEvery = Math.Max(1, pairs / 20);

        for (int i = 0; i < pairs; i++)
        {
            PInvoke.SendInput(inputs, Marshal.SizeOf<INPUT>());

            if (i % reportEvery == 0)
            {
                Console.Write($"\r  {(double)i / pairs * 100,5:F1}%  ({i * 2:N0} events)");
            }

            // Without a yield, SendInput floods the input queue and we end up
            // measuring queue backpressure rather than callback cost.
            if ((i & 15) == 0) Thread.Sleep(0);
        }

        Console.WriteLine($"\r  100.0%  ({pairs * 2:N0} events)          ");
    }
}
