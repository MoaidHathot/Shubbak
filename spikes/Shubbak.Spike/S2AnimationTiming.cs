using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.System.Threading;

namespace Shubbak.Spike;

/// <summary>
/// S2 - Animation frame timing.
///
/// Question under test: can a managed tick loop move N windows per frame at
/// 144 Hz (6.94 ms budget) without dropping frames?
///
/// The design under test is the one the real animation engine will use:
///   - a high-resolution waitable timer (CREATE_WAITABLE_TIMER_HIGH_RESOLUTION)
///     rather than Thread.Sleep, whose granularity is far too coarse
///   - all window moves for a frame batched into ONE
///     BeginDeferWindowPos / DeferWindowPos* / EndDeferWindowPos transaction,
///     so the frame lands atomically instead of tearing between tiles
///   - zero allocation inside the tick loop
///
/// It also breaks the frame down into (a) time spent in our managed interpolation
/// math and (b) time spent inside the Win32 batch call. If (b) dominates, the
/// choice of language is irrelevant to animation smoothness - which is the real
/// thing we want to learn here.
///
/// PASS GATE: dropped frames &lt; 1% AND frame-time p99 &lt; 6.94 ms at 144 Hz.
/// </summary>
internal static class S2AnimationTiming
{
    private struct AnimWindow
    {
        public HWND Hwnd;
        public float StartX, StartY, StartW, StartH;
        public float EndX, EndY, EndW, EndH;
    }

    private const string WindowClass = "ShubbakSpikeS2";
    private static readonly List<HWND> s_created = [];

    /// <summary>TIMER_ALL_ACCESS. Not exposed as a CsWin32 enum, so spelled out here.</summary>
    private const uint TimerAllAccess = 0x1F0003;

    public static unsafe int Run(string[] args)
    {
        int windowCount = ArgUtil.GetInt(args, "--windows", 20);
        double targetHz = ArgUtil.GetDouble(args, "--hz", 144.0);
        int durationSec = ArgUtil.GetInt(args, "--seconds", 60);
        bool noGcPressure = ArgUtil.HasFlag(args, "--no-gc-pressure");
        bool noBatch = ArgUtil.HasFlag(args, "--no-batch");

        // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 == (HANDLE)-4. CsWin32 models
        // this as a handle type rather than an enum, so the sentinel is spelled out.
        PInvoke.SetProcessDpiAwarenessContext((DPI_AWARENESS_CONTEXT)(nint)(-4));

        double frameBudgetMs = 1000.0 / targetHz;
        int totalFrames = (int)(durationSec * targetHz);

        Console.WriteLine("=== S2: Animation frame timing ===");
        Console.WriteLine($"Runtime         : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"AOT             : {(RuntimeFeature.IsDynamicCodeSupported ? "no (JIT)" : "yes (NativeAOT)")}");
        Console.WriteLine($"Windows         : {windowCount}");
        Console.WriteLine($"Target rate     : {targetHz:F1} Hz  (budget {frameBudgetMs:F3} ms/frame)");
        Console.WriteLine($"Duration        : {durationSec}s  ({totalFrames:N0} frames)");
        Console.WriteLine($"Batching        : {(noBatch ? "OFF (individual SetWindowPos)" : "ON (DeferWindowPos)")}");
        Console.WriteLine($"GC pressure     : {(noGcPressure ? "disabled" : "enabled")}");
        ReportDwmRefresh();
        Console.WriteLine();

        // ---- Create the test windows -----------------------------------------
        if (!CreateTestWindows(windowCount))
        {
            Console.Error.WriteLine("FATAL: could not create test windows.");
            return 2;
        }
        Console.WriteLine($"Created {s_created.Count} borderless test windows.");

        // Pre-allocate everything the tick loop touches. Nothing below this line
        // may allocate on the hot path.
        var anim = new AnimWindow[s_created.Count];
        int screenW = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSCREEN);
        int screenH = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYSCREEN);
        InitAnimTargets(anim, screenW, screenH);

        var frameTime = new LatencyStats(totalFrames + 1024) { Name = "frame-total" };
        var managedTime = new LatencyStats(totalFrames + 1024) { Name = "frame-managed" };
        var win32Time = new LatencyStats(totalFrames + 1024) { Name = "frame-win32" };
        var jitter = new LatencyStats(totalFrames + 1024) { Name = "wake-jitter" };

        using var pressure = new GcPressure();
        if (!noGcPressure) pressure.Start(2, forceBlockingGen2: true);

        // ---- High resolution waitable timer ----------------------------------
        using var timer = PInvoke.CreateWaitableTimerEx(
            null, null,
            (uint)PInvoke.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
            TimerAllAccess);

        bool highResTimer = timer is not null && !timer.IsInvalid;
        if (!highResTimer)
        {
            Console.WriteLine("WARNING: high-resolution waitable timer unavailable; " +
                              "falling back to timeBeginPeriod(1) + Sleep.");
            PInvoke.timeBeginPeriod(1);
        }

        // Raise thread priority: the real animation thread will do the same.
        PInvoke.SetThreadPriority(PInvoke.GetCurrentThread(),
            THREAD_PRIORITY.THREAD_PRIORITY_TIME_CRITICAL);

        long budgetTicks = Qpc.MsToTicks(frameBudgetMs);
        long start = Qpc.Now();
        long nextWake = start + budgetTicks;
        int dropped = 0;
        int reportEvery = Math.Max(1, totalFrames / 20);

        Console.WriteLine("Running...");

        for (int frame = 0; frame < totalFrames; frame++)
        {
            // ---- wait for the frame boundary ---------------------------------
            WaitUntil(timer, highResTimer, nextWake);

            long wake = Qpc.Now();
            jitter.Record(Qpc.TicksToMs(wake - nextWake));

            // ---- managed: interpolate ----------------------------------------
            long t0 = Qpc.Now();

            float progress = (float)frame / totalFrames;
            // Ping-pong so windows sweep back and forth for the whole run.
            float p = progress * 8f % 2f;
            if (p > 1f) p = 2f - p;
            float eased = EaseInOutCubic(p);

            long t1 = Qpc.Now();

            // ---- win32: commit the frame -------------------------------------
            if (noBatch) CommitIndividual(anim, eased);
            else CommitBatched(anim, eased);

            long t2 = Qpc.Now();

            managedTime.Record(Qpc.TicksToMs(t1 - t0));
            win32Time.Record(Qpc.TicksToMs(t2 - t1));
            double total = Qpc.TicksToMs(t2 - wake);
            frameTime.Record(total);

            if (total > frameBudgetMs) dropped++;

            nextWake += budgetTicks;

            // If we fell far behind, resynchronise rather than spiral.
            long now = Qpc.Now();
            if (nextWake < now) nextWake = now + budgetTicks;

            if (frame % reportEvery == 0)
                Console.Write($"\r  {(double)frame / totalFrames * 100,5:F1}%  " +
                              $"dropped={dropped}    ");
        }

        Console.WriteLine($"\r  100.0%  dropped={dropped}          ");

        PInvoke.SetThreadPriority(PInvoke.GetCurrentThread(),
            THREAD_PRIORITY.THREAD_PRIORITY_NORMAL);
        if (!highResTimer) PInvoke.timeEndPeriod(1);

        var gc = noGcPressure ? default : pressure.Stop();
        DestroyTestWindows();

        // ---- Report -----------------------------------------------------------
        var ft = frameTime.Compute();
        var mt = managedTime.Compute();
        var wt = win32Time.Compute();
        var jt = jitter.Compute();

        double dropPct = (double)dropped / totalFrames * 100.0;

        Console.WriteLine();
        Console.WriteLine("--- Results -------------------------------------------------");
        Console.WriteLine($"Timer           : {(highResTimer ? "high-resolution waitable" : "timeBeginPeriod fallback")}");
        if (!noGcPressure) Console.WriteLine($"GC activity     : {gc}");
        Console.WriteLine();
        Console.WriteLine($"Frame total     : {ft}");
        Console.WriteLine($"  managed math  : {mt}");
        Console.WriteLine($"  win32 commit  : {wt}");
        Console.WriteLine($"Wake jitter     : {jt}");
        Console.WriteLine();
        Console.WriteLine($"Dropped frames  : {dropped:N0} / {totalFrames:N0}  ({dropPct:F3}%)");
        Console.WriteLine();

        double managedShare = ft.Mean > 0 ? mt.Mean / ft.Mean * 100.0 : 0;
        double win32Share = ft.Mean > 0 ? wt.Mean / ft.Mean * 100.0 : 0;
        Console.WriteLine($"Time attribution: managed {managedShare:F1}%  |  win32 {win32Share:F1}%");
        Console.WriteLine($"  -> {(win32Share > managedShare * 3 ? "Win32 dominates: language choice is NOT the bottleneck." : "Managed code is a meaningful share; worth optimising.")}");
        Console.WriteLine();

        bool passDrop = dropPct < 1.0;
        bool passP99 = ft.P99 < frameBudgetMs;
        bool pass = passDrop && passP99;

        Console.WriteLine($"GATE dropped < 1%       : {(passDrop ? "PASS" : "FAIL")}  ({dropPct:F3}%)");
        Console.WriteLine($"GATE p99 < {frameBudgetMs:F2} ms budget: {(passP99 ? "PASS" : "FAIL")}  ({ft.P99:F3} ms)");
        Console.WriteLine();
        Console.WriteLine($"S2: {(pass ? "PASS" : "FAIL")}");

        return pass ? 0 : 1;
    }

    // ---- frame commit paths ------------------------------------------------

    /// <summary>
    /// The real path: one atomic transaction for every window in the frame.
    /// Allocation-free.
    /// </summary>
    private static unsafe void CommitBatched(AnimWindow[] anim, float t)
    {
        HDWP hdwp = PInvoke.BeginDeferWindowPos(anim.Length);
        if (hdwp.IsNull) return;

        for (int i = 0; i < anim.Length; i++)
        {
            ref AnimWindow a = ref anim[i];
            int x = (int)(a.StartX + (a.EndX - a.StartX) * t);
            int y = (int)(a.StartY + (a.EndY - a.StartY) * t);
            int w = (int)(a.StartW + (a.EndW - a.StartW) * t);
            int h = (int)(a.StartH + (a.EndH - a.StartH) * t);

            hdwp = PInvoke.DeferWindowPos(hdwp, a.Hwnd, HWND.Null, x, y, w, h,
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
                SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
                SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER |
                SET_WINDOW_POS_FLAGS.SWP_NOSENDCHANGING |
                SET_WINDOW_POS_FLAGS.SWP_NOCOPYBITS);

            if (hdwp.IsNull) return;
        }

        PInvoke.EndDeferWindowPos(hdwp);
    }

    /// <summary>Control group: naive per-window SetWindowPos, for comparison.</summary>
    private static void CommitIndividual(AnimWindow[] anim, float t)
    {
        for (int i = 0; i < anim.Length; i++)
        {
            ref AnimWindow a = ref anim[i];
            int x = (int)(a.StartX + (a.EndX - a.StartX) * t);
            int y = (int)(a.StartY + (a.EndY - a.StartY) * t);
            int w = (int)(a.StartW + (a.EndW - a.StartW) * t);
            int h = (int)(a.StartH + (a.EndH - a.StartH) * t);

            PInvoke.SetWindowPos(a.Hwnd, HWND.Null, x, y, w, h,
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
                SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
                SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER |
                SET_WINDOW_POS_FLAGS.SWP_NOSENDCHANGING |
                SET_WINDOW_POS_FLAGS.SWP_NOCOPYBITS);
        }
    }

    private static float EaseInOutCubic(float t) =>
        t < 0.5f ? 4f * t * t * t : 1f - MathF.Pow(-2f * t + 2f, 3f) / 2f;

    // ---- timing ------------------------------------------------------------

    private static unsafe void WaitUntil(
        Microsoft.Win32.SafeHandles.SafeFileHandle? timer, bool highRes, long deadlineTicks)
    {
        long remaining = deadlineTicks - Qpc.Now();
        if (remaining <= 0) return;

        double remainingMs = Qpc.TicksToMs(remaining);

        // Sleep for the bulk of the interval, then spin the last ~0.4 ms. Spinning
        // the tail is what removes wake jitter; sleeping the bulk is what keeps a
        // core free. This is standard frame pacing.
        const double SpinTailMs = 0.4;

        if (remainingMs > SpinTailMs)
        {
            double sleepMs = remainingMs - SpinTailMs;

            if (highRes && timer is not null)
            {
                // Negative = relative, in 100 ns units.
                long due = -(long)(sleepMs * 10_000.0);
                if (PInvoke.SetWaitableTimerEx(timer, in due, 0, null, null, null, 0))
                    PInvoke.WaitForSingleObject(timer, 1000);
            }
            else
            {
                Thread.Sleep((int)sleepMs);
            }
        }

        // Spin the tail.
        var spin = new SpinWait();
        while (Qpc.Now() < deadlineTicks)
        {
            if (spin.NextSpinWillYield) spin.Reset();
            spin.SpinOnce();
        }
    }

    private static unsafe void ReportDwmRefresh()
    {
        try
        {
            var info = new DWM_TIMING_INFO { cbSize = (uint)sizeof(DWM_TIMING_INFO) };
            HRESULT hr = PInvoke.DwmGetCompositionTimingInfo(HWND.Null, out info);
            if (hr.Succeeded && info.rateRefresh.uiDenominator != 0)
            {
                double hz = (double)info.rateRefresh.uiNumerator / info.rateRefresh.uiDenominator;
                Console.WriteLine($"DWM composition : {hz:F2} Hz  (monitor refresh, for phase-lock reference)");
            }
            else
            {
                Console.WriteLine($"DWM composition : unavailable (hr=0x{hr.Value:X8})");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"DWM composition : query failed ({ex.GetType().Name})");
        }
    }

    // ---- test window plumbing ----------------------------------------------

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT TestWndProc(HWND hwnd, uint msg, WPARAM wparam, LPARAM lparam)
        => PInvoke.DefWindowProc(hwnd, msg, wparam, lparam);

    private static unsafe bool CreateTestWindows(int count)
    {
        fixed (char* className = WindowClass)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &TestWndProc,
                hInstance = HINSTANCE.Null,
                lpszClassName = className,
            };

            if (PInvoke.RegisterClassEx(in wc) == 0)
            {
                int err = Marshal.GetLastWin32Error();
                // 1410 = class already registered; fine on a re-run.
                if (err != 1410)
                {
                    Console.Error.WriteLine($"RegisterClassEx failed: {err}");
                    return false;
                }
            }
        }

        for (int i = 0; i < count; i++)
        {
            HWND hwnd = PInvoke.CreateWindowEx(
                WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOACTIVATE,
                WindowClass, $"Shubbak S2 #{i}",
                WINDOW_STYLE.WS_POPUP | WINDOW_STYLE.WS_VISIBLE,
                100 + i * 5, 100 + i * 5, 200, 150,
                HWND.Null, (SafeHandle?)null, (SafeHandle?)null, null);

            if (hwnd.IsNull)
            {
                Console.Error.WriteLine($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
                return false;
            }

            PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);
            s_created.Add(hwnd);
        }

        return s_created.Count > 0;
    }

    private static void InitAnimTargets(AnimWindow[] anim, int screenW, int screenH)
    {
        // Lay the windows out in a grid, then have every one sweep to a mirrored
        // position. This is representative of a workspace-wide relayout.
        int cols = (int)Math.Ceiling(Math.Sqrt(anim.Length));
        int rows = (int)Math.Ceiling((double)anim.Length / cols);
        int cw = Math.Max(120, screenW / (cols + 1));
        int ch = Math.Max(100, screenH / (rows + 1));

        for (int i = 0; i < anim.Length; i++)
        {
            int c = i % cols, r = i / cols;
            anim[i] = new AnimWindow
            {
                Hwnd = s_created[i],
                StartX = c * cw, StartY = r * ch, StartW = cw - 8, StartH = ch - 8,
                EndX = (cols - 1 - c) * cw, EndY = (rows - 1 - r) * ch,
                EndW = cw - 8, EndH = ch - 8,
            };
        }
    }

    private static void DestroyTestWindows()
    {
        foreach (var hwnd in s_created) PInvoke.DestroyWindow(hwnd);
        s_created.Clear();
    }
}
