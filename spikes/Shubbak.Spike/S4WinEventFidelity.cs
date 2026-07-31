using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Shubbak.Spike;

/// <summary>
/// S4 - WinEvent hook fidelity and volume.
///
/// Two questions:
///
/// 1. FIDELITY. Does EVENT_OBJECT_NAMECHANGE actually fire when a browser tab is
///    switched (same HWND, new title)? This is the exact bug in Zebar: it listens
///    only to EVENT_SYSTEM_FOREGROUND, so the bar's window-title widget goes stale
///    on tab switches. If NAMECHANGE fires reliably, Taj gets correct live titles
///    essentially for free, because the WM already runs a global WinEvent hook.
///
/// 2. VOLUME. How noisy is the raw event stream, especially
///    EVENT_OBJECT_LOCATIONCHANGE? This determines how aggressively the real WM
///    must filter, and whether the hook callback can afford to do any work at all.
///
/// This spike is interactive: it prints a live tally and asks you to exercise the
/// scenarios that matter.
/// </summary>
internal static class S4WinEventFidelity
{
    private static readonly Dictionary<uint, long> s_counts = [];
    private static readonly Dictionary<uint, long> s_countsWindowOnly = [];
    private static readonly object s_lock = new();

    private static long s_nameChangeOnForegroundWindow;
    private static HWND s_foreground;
    private static string s_lastForegroundTitle = "";
    private static readonly List<string> s_titleTimeline = [];
    private static LatencyStats s_callbackLatency = null!;

    private static readonly (uint Id, string Name)[] Events =
    [
        (PInvoke.EVENT_OBJECT_CREATE,          "OBJECT_CREATE"),
        (PInvoke.EVENT_OBJECT_DESTROY,         "OBJECT_DESTROY"),
        (PInvoke.EVENT_OBJECT_SHOW,            "OBJECT_SHOW"),
        (PInvoke.EVENT_OBJECT_HIDE,            "OBJECT_HIDE"),
        (PInvoke.EVENT_OBJECT_NAMECHANGE,      "OBJECT_NAMECHANGE"),
        (PInvoke.EVENT_OBJECT_LOCATIONCHANGE,  "OBJECT_LOCATIONCHANGE"),
        (PInvoke.EVENT_OBJECT_CLOAKED,         "OBJECT_CLOAKED"),
        (PInvoke.EVENT_OBJECT_UNCLOAKED,       "OBJECT_UNCLOAKED"),
        (PInvoke.EVENT_SYSTEM_FOREGROUND,      "SYSTEM_FOREGROUND"),
        (PInvoke.EVENT_SYSTEM_MINIMIZESTART,   "SYSTEM_MINIMIZESTART"),
        (PInvoke.EVENT_SYSTEM_MINIMIZEEND,     "SYSTEM_MINIMIZEEND"),
    ];

    public static int Run(string[] args)
    {
        int seconds = ArgUtil.GetInt(args, "--seconds", 60);

        s_callbackLatency = new LatencyStats(2_000_000) { Name = "winevent-callback" };

        Console.WriteLine("=== S4: WinEvent hook fidelity and volume ===");
        Console.WriteLine($"Runtime         : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"Duration        : {seconds}s");
        Console.WriteLine();
        Console.WriteLine("PLEASE EXERCISE THESE SCENARIOS WHILE THIS RUNS:");
        Console.WriteLine("  1. Open a browser, then SWITCH BETWEEN TABS several times.");
        Console.WriteLine("     (This is the Zebar bug: title changes without a focus change.)");
        Console.WriteLine("  2. Switch focus between a few different applications.");
        Console.WriteLine("  3. Drag a window around, and resize it.");
        Console.WriteLine("  4. Minimise and restore a window.");
        Console.WriteLine();
        Console.WriteLine("Press ESC to stop early.");
        Console.WriteLine();

        var hooks = new List<UnhookWinEventSafeHandle>();

        foreach (var (id, name) in Events)
        {
            UnhookWinEventSafeHandle h;
            unsafe
            {
                h = PInvoke.SetWinEventHook(id, id, null, &WinEventCallback, 0, 0,
                    PInvoke.WINEVENT_OUTOFCONTEXT | PInvoke.WINEVENT_SKIPOWNPROCESS);
            }

            if (h.IsInvalid) Console.Error.WriteLine($"  WARN: failed to hook {name}");
            else hooks.Add(h);
        }

        Console.WriteLine($"Installed {hooks.Count}/{Events.Length} WinEvent hooks.");
        Console.WriteLine();

        long start = Qpc.Now();
        long deadlineTicks = start + Qpc.MsToTicks(seconds * 1000.0);
        long lastPrint = 0;

        // WinEvent hooks with WINEVENT_OUTOFCONTEXT are delivered to this thread's
        // message queue, so we need a pump.
        while (Qpc.Now() < deadlineTicks)
        {
            while (PInvoke.PeekMessage(out MSG msg, default, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_REMOVE))
            {
                PInvoke.TranslateMessage(in msg);
                PInvoke.DispatchMessage(in msg);
            }

            if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape) break;

            long now = Qpc.Now();
            if (Qpc.TicksToMs(now - lastPrint) > 1000)
            {
                lastPrint = now;
                double elapsed = Qpc.TicksToMs(now - start) / 1000.0;
                lock (s_lock)
                {
                    Console.Write($"\r  {elapsed,5:F0}s  events={Sum(s_counts),-8:N0} " +
                                  $"titles-captured={s_titleTimeline.Count,-4} " +
                                  $"namechange-on-fg={s_nameChangeOnForegroundWindow,-5:N0}   ");
                }
            }

            Thread.Sleep(1);
        }

        Console.WriteLine();

        foreach (var h in hooks) h.Dispose();

        double totalSec = Qpc.TicksToMs(Qpc.Now() - start) / 1000.0;

        // ---- Report -----------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("--- Event volume --------------------------------------------");
        Console.WriteLine($"{"Event",-24} {"total",10} {"OBJID_WINDOW",14} {"/sec",9}");

        lock (s_lock)
        {
            foreach (var (id, name) in Events)
            {
                s_counts.TryGetValue(id, out long total);
                s_countsWindowOnly.TryGetValue(id, out long windowOnly);
                Console.WriteLine($"{name,-24} {total,10:N0} {windowOnly,14:N0} {total / totalSec,9:F1}");
            }

            long grand = Sum(s_counts);
            Console.WriteLine($"{"TOTAL",-24} {grand,10:N0} {Sum(s_countsWindowOnly),14:N0} {grand / totalSec,9:F1}");

            Console.WriteLine();
            Console.WriteLine("--- Title change timeline (the Zebar bug) -------------------");
            Console.WriteLine($"NAMECHANGE events on the foreground window: {s_nameChangeOnForegroundWindow:N0}");
            Console.WriteLine();

            if (s_titleTimeline.Count == 0)
            {
                Console.WriteLine("  (no titles captured - was any window activity performed?)");
            }
            else
            {
                int show = Math.Min(40, s_titleTimeline.Count);
                for (int i = 0; i < show; i++) Console.WriteLine($"  {s_titleTimeline[i]}");
                if (s_titleTimeline.Count > show)
                    Console.WriteLine($"  ... and {s_titleTimeline.Count - show} more");
            }

            Console.WriteLine();
            Console.WriteLine("--- Callback cost -------------------------------------------");
            Console.WriteLine($"WinEvent callback: {s_callbackLatency.Compute()}");
            Console.WriteLine();

            s_counts.TryGetValue(PInvoke.EVENT_OBJECT_NAMECHANGE, out long nameChanges);
            s_counts.TryGetValue(PInvoke.EVENT_OBJECT_LOCATIONCHANGE, out long locChanges);

            bool fidelityPass = s_nameChangeOnForegroundWindow > 0;
            Console.WriteLine($"FINDING 1 (fidelity): NAMECHANGE on foreground window fired " +
                              $"{s_nameChangeOnForegroundWindow:N0} times -> " +
                              $"{(fidelityPass ? "USABLE for live titles" : "NOT OBSERVED - investigate")}");
            Console.WriteLine($"FINDING 2 (volume)  : LOCATIONCHANGE = {locChanges:N0} " +
                              $"({locChanges / totalSec:F0}/s). " +
                              $"{(locChanges / totalSec > 100 ? "High - must filter by OBJID_WINDOW + generation counter." : "Manageable.")}");
            Console.WriteLine($"FINDING 3 (noise)   : {PercentWindowOnly():F1}% of all events are OBJID_WINDOW; " +
                              "the rest are child-object noise the WM should discard immediately.");
            Console.WriteLine();
            Console.WriteLine($"S4: {(fidelityPass ? "PASS" : "INCONCLUSIVE")}");

            return fidelityPass ? 0 : 1;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void WinEventCallback(
        HWINEVENTHOOK hook, uint eventId, HWND hwnd, int idObject, int idChild,
        uint threadId, uint eventTime)
    {
        long entry = Qpc.Now();

        try
        {
            bool isWindow = idObject == (int)OBJECT_IDENTIFIER.OBJID_WINDOW && idChild == 0;

            lock (s_lock)
            {
                s_counts[eventId] = s_counts.GetValueOrDefault(eventId) + 1;
                if (isWindow) s_countsWindowOnly[eventId] = s_countsWindowOnly.GetValueOrDefault(eventId) + 1;

                if (isWindow && !hwnd.IsNull)
                {
                    if (eventId == PInvoke.EVENT_SYSTEM_FOREGROUND)
                    {
                        s_foreground = hwnd;
                        string title = GetTitle(hwnd);
                        s_lastForegroundTitle = title;
                        if (s_titleTimeline.Count < 500)
                            s_titleTimeline.Add($"[FOREGROUND] {Trunc(title, 70)}");
                    }
                    else if (eventId == PInvoke.EVENT_OBJECT_NAMECHANGE && hwnd == s_foreground)
                    {
                        s_nameChangeOnForegroundWindow++;
                        string title = GetTitle(hwnd);
                        if (title != s_lastForegroundTitle && title.Length > 0)
                        {
                            s_lastForegroundTitle = title;
                            if (s_titleTimeline.Count < 500)
                                s_titleTimeline.Add($"[NAMECHANGE] {Trunc(title, 70)}");
                        }
                    }
                }
            }
        }
        catch
        {
            // An exception escaping an UnmanagedCallersOnly callback tears down the
            // process. Swallow. (The real WM will log to a pre-allocated buffer.)
        }

        s_callbackLatency.Record(Qpc.TicksToMs(Qpc.Now() - entry));
    }

    private static unsafe string GetTitle(HWND hwnd)
    {
        int len = PInvoke.GetWindowTextLength(hwnd);
        if (len <= 0) return "";
        Span<char> buf = len < 512 ? stackalloc char[len + 1] : new char[len + 1];
        fixed (char* p = buf)
        {
            int n = PInvoke.GetWindowText(hwnd, p, buf.Length);
            return n > 0 ? new string(p, 0, n) : "";
        }
    }

    private static string Trunc(string s, int n) =>
        s.Length <= n ? s : string.Concat(s.AsSpan(0, n - 1), "\u2026");

    private static long Sum(Dictionary<uint, long> d)
    {
        long t = 0;
        foreach (var kv in d) t += kv.Value;
        return t;
    }

    private static double PercentWindowOnly()
    {
        long all = Sum(s_counts);
        return all == 0 ? 0 : (double)Sum(s_countsWindowOnly) / all * 100.0;
    }
}
