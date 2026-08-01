using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Shubbak.Core.Geometry;
using Shubbak.Core.Tree;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Shubbak.Native;

/// <summary>
/// Reads the state of a native window. Never mutates anything.
/// </summary>
/// <remarks>
/// Kept separate from <see cref="WindowCommitter"/> so the read path can be used
/// freely from event callbacks and the inspector without any risk of provoking the
/// feedback loop that mutation causes.
/// </remarks>
public static class Win32Window
{
    /// <summary>
    /// <c>DWMWA_CLOAKED</c>. Not present in the CsWin32 enum in all metadata
    /// versions, so it is spelled out.
    /// </summary>
    private const uint DwmwaCloaked = 14;

    public static bool Exists(nint handle) => PInvoke.IsWindow(new HWND(handle));

    public static bool IsVisible(nint handle) => PInvoke.IsWindowVisible(new HWND(handle));

    public static bool IsMinimised(nint handle) => PInvoke.IsIconic(new HWND(handle));

    public static bool IsMaximised(nint handle) => PInvoke.IsZoomed(new HWND(handle));

    public static unsafe nint GetForeground() => (nint)PInvoke.GetForegroundWindow().Value;

    /// <summary>
    /// True when the shell has cloaked the window.
    /// </summary>
    /// <remarks>
    /// UWP and some Electron windows remain "visible" by <c>IsWindowVisible</c>
    /// while cloaked - they exist but are not composited. Managing them produces
    /// phantom tiles: space is reserved on screen for a window that cannot be seen
    /// or focused. This check is the standard remedy and is why every serious
    /// Windows tiling manager carries it.
    /// </remarks>
    public static unsafe bool IsCloaked(nint handle)
    {
        uint cloaked = 0;
        HRESULT hr = PInvoke.DwmGetWindowAttribute(
            new HWND(handle), (DWMWINDOWATTRIBUTE)DwmwaCloaked, &cloaked, sizeof(uint));

        return hr.Succeeded && cloaked != 0;
    }

    public static Rect GetBounds(nint handle)
    {
        if (!PInvoke.GetWindowRect(new HWND(handle), out RECT rect)) return Rect.Empty;
        return Rect.FromEdges(rect.left, rect.top, rect.right, rect.bottom);
    }

    public static unsafe string GetTitle(nint handle)
    {
        var hwnd = new HWND(handle);
        int length = PInvoke.GetWindowTextLength(hwnd);
        if (length <= 0) return string.Empty;

        // +1 for the terminator GetWindowText always writes.
        Span<char> buffer = length < 256 ? stackalloc char[length + 1] : new char[length + 1];
        fixed (char* p = buffer)
        {
            int written = PInvoke.GetWindowText(hwnd, p, buffer.Length);
            return written > 0 ? new string(p, 0, written) : string.Empty;
        }
    }

    public static unsafe string GetClassName(nint handle)
    {
        // 256 is the documented maximum length of a window class name.
        Span<char> buffer = stackalloc char[256];
        fixed (char* p = buffer)
        {
            int written = PInvoke.GetClassName(new HWND(handle), p, buffer.Length);
            return written > 0 ? new string(p, 0, written) : string.Empty;
        }
    }

    public static uint GetProcessId(nint handle)
    {
        uint processId = 0;
        unsafe { _ = PInvoke.GetWindowThreadProcessId(new HWND(handle), &processId); }
        return processId;
    }

    public static uint GetThreadId(nint handle)
    {
        unsafe { return PInvoke.GetWindowThreadProcessId(new HWND(handle), null); }
    }

    /// <summary>
    /// The full path of the owning executable, or null when it cannot be read.
    /// </summary>
    /// <remarks>
    /// Returns null rather than throwing for elevated processes: an unelevated
    /// Shubbak cannot open them, and that is an expected condition rather than an
    /// error. <see cref="BuildIdentity"/> records it as
    /// <see cref="WindowIdentity.IsElevated"/> so the user is told why a window
    /// cannot be managed instead of watching it silently misbehave.
    /// </remarks>
    public static unsafe string? GetProcessPath(uint processId)
    {
        if (processId == 0) return null;

        using SafeFileHandle process = PInvoke.OpenProcess_SafeHandle(
            PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);

        if (process.IsInvalid) return null;

        Span<char> buffer = stackalloc char[512];
        uint size = (uint)buffer.Length;

        if (!PInvoke.QueryFullProcessImageName(process, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, buffer, ref size))
            return null;

        return new string(buffer[..(int)size]);
    }

    // Win32 style enums are generated as internal and are deliberately not exposed:
    // Shubbak.Core must never see a Win32 type. Callers outside this assembly get
    // the derived predicates instead.
    internal static WINDOW_STYLE GetStyle(nint handle) =>
        (WINDOW_STYLE)(uint)PInvoke.GetWindowLong(new HWND(handle), WINDOW_LONG_PTR_INDEX.GWL_STYLE);

    internal static WINDOW_EX_STYLE GetExStyle(nint handle) =>
        (WINDOW_EX_STYLE)(uint)PInvoke.GetWindowLong(new HWND(handle), WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);

    /// <summary>Raw window style bits, for the inspector to display.</summary>
    public static uint GetStyleBits(nint handle) =>
        (uint)PInvoke.GetWindowLong(new HWND(handle), WINDOW_LONG_PTR_INDEX.GWL_STYLE);

    /// <summary>Raw extended style bits, for the inspector to display.</summary>
    public static uint GetExStyleBits(nint handle) =>
        (uint)PInvoke.GetWindowLong(new HWND(handle), WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);

    /// <summary>Collects everything rules and the bar need about a window.</summary>
    public static WindowIdentity BuildIdentity(nint handle)
    {
        uint processId = GetProcessId(handle);
        string? path = GetProcessPath(processId);

        return new WindowIdentity
        {
            ProcessName = path is null ? string.Empty : Path.GetFileNameWithoutExtension(path),
            ProcessPath = path,
            ClassName = GetClassName(handle),
            Title = GetTitle(handle),
            ProcessId = (int)processId,

            // A path we could not read from a live process almost always means the
            // process is at a higher integrity level than ours.
            IsElevated = path is null && processId != 0,
        };
    }

    /// <summary>Every top-level window currently in the desktop's z-order.</summary>
    public static IReadOnlyList<nint> EnumerateTopLevel()
    {
        List<nint> handles = [];
        GCHandle gcHandle = GCHandle.Alloc(handles);

        try
        {
            unsafe
            {
                PInvoke.EnumWindows(&CollectWindow, new LPARAM(GCHandle.ToIntPtr(gcHandle)));
            }
        }
        finally
        {
            gcHandle.Free();
        }

        return handles;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static unsafe BOOL CollectWindow(HWND hwnd, LPARAM lParam)
    {
        try
        {
            var handle = GCHandle.FromIntPtr(lParam.Value);
            if (handle.Target is List<nint> list) list.Add((nint)hwnd.Value);
        }
        catch
        {
            // An exception escaping an UnmanagedCallersOnly callback tears down the
            // process, so enumeration stops quietly instead.
            return false;
        }

        return true;
    }
}
