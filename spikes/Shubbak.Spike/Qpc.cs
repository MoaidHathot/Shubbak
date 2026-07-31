using System.Runtime.CompilerServices;
using Windows.Win32;

namespace Shubbak.Spike;

/// <summary>
/// QueryPerformanceCounter-based clock. Every measurement in the P0 spike goes
/// through this rather than Stopwatch so the timing source is unambiguous and
/// the read path is allocation-free.
/// </summary>
internal static class Qpc
{
    private static readonly long s_frequency = ReadFrequency();

    private static long ReadFrequency()
    {
        long f;
        unsafe { PInvoke.QueryPerformanceFrequency(&f); }
        return f;
    }

    /// <summary>Raw counter ticks. Safe to call from a hook callback: no allocation.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Now()
    {
        long t;
        unsafe { PInvoke.QueryPerformanceCounter(&t); }
        return t;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double TicksToMs(long ticks) => ticks * 1000.0 / s_frequency;

    public static long MsToTicks(double ms) => (long)(ms * s_frequency / 1000.0);

    public static long Frequency => s_frequency;
}
