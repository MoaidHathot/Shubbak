using System.Runtime.InteropServices;
using Shubbak.Core.Geometry;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;

namespace Shubbak.Native;

/// <summary>A display as reported by the operating system.</summary>
/// <param name="DeviceId">
/// Stable device name, e.g. <c>\\.\DISPLAY1</c>. Used as the identity key rather
/// than an index, because Windows renumbers displays on replug, on DisplayPort
/// wake and on driver restart - which is why index-keyed window managers scramble
/// workspace assignments after undocking.
/// </param>
/// <param name="Bounds">Full monitor rectangle in virtual-desktop coordinates.</param>
/// <param name="WorkArea">Bounds minus the taskbar and any docked appbars.</param>
/// <param name="Dpi">Effective DPI; 96 is 100% scaling.</param>
/// <param name="IsPrimary">Whether this is the primary display.</param>
public readonly record struct MonitorInfo(
    string DeviceId,
    Rect Bounds,
    Rect WorkArea,
    uint Dpi,
    bool IsPrimary);

/// <summary>Enumerates displays.</summary>
public static class MonitorSource
{
    /// <summary>
    /// Declares the process per-monitor DPI aware.
    /// </summary>
    /// <remarks>
    /// Must run before any window is created or any monitor queried. Without it
    /// Windows lies about coordinates on scaled displays - reporting virtualised
    /// values - and every rectangle the window manager computes lands in the wrong
    /// place on a mixed-DPI setup.
    /// </remarks>
    public static void EnableDpiAwareness()
    {
        // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 == (HANDLE)-4. CsWin32 models
        // this as a handle type rather than an enum, so the sentinel is spelled out.
        PInvoke.SetProcessDpiAwarenessContext((DPI_AWARENESS_CONTEXT)(nint)(-4));
    }

    /// <summary>Every attached display.</summary>
    public static IReadOnlyList<MonitorInfo> Enumerate()
    {
        List<MonitorInfo> monitors = [];
        GCHandle handle = GCHandle.Alloc(monitors);

        try
        {
            unsafe
            {
                PInvoke.EnumDisplayMonitors(
                    HDC.Null, (RECT?)null, &Collect, new LPARAM(GCHandle.ToIntPtr(handle)));
            }
        }
        finally
        {
            handle.Free();
        }

        return monitors;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static unsafe BOOL Collect(HMONITOR monitor, HDC _, RECT* __, LPARAM lParam)
    {
        try
        {
            var gcHandle = GCHandle.FromIntPtr(lParam.Value);
            if (gcHandle.Target is not List<MonitorInfo> list) return true;

            if (Describe(monitor) is { } info) list.Add(info);
        }
        catch
        {
            // An exception escaping an UnmanagedCallersOnly callback tears down the
            // process, so enumeration stops quietly instead.
            return false;
        }

        return true;
    }

    private static unsafe MonitorInfo? Describe(HMONITOR monitor)
    {
        var info = new MONITORINFOEXW
        {
            monitorInfo = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFOEXW) },
        };

        if (!PInvoke.GetMonitorInfo(monitor, (MONITORINFO*)&info)) return null;

        RECT bounds = info.monitorInfo.rcMonitor;
        RECT work = info.monitorInfo.rcWork;

        string deviceId = info.szDevice.ToString();

        uint dpi = 96;
        if (PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out uint dpiX, out _).Succeeded)
            dpi = dpiX;

        const uint MonitorPrimary = 1;

        return new MonitorInfo(
            deviceId,
            Rect.FromEdges(bounds.left, bounds.top, bounds.right, bounds.bottom),
            Rect.FromEdges(work.left, work.top, work.right, work.bottom),
            dpi,
            (info.monitorInfo.dwFlags & MonitorPrimary) != 0);
    }

    /// <summary>The display a window mostly occupies.</summary>
    public static MonitorInfo? ForWindow(nint handle)
    {
        HMONITOR monitor = PInvoke.MonitorFromWindow(
            new HWND(handle), MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);

        return monitor.IsNull ? null : Describe(monitor);
    }
}
