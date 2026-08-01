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
    /// <summary>Who cloaked a window, if anyone.</summary>
    /// <remarks>
    /// The distinction matters enormously. A window Shubbak cloaked to hide an
    /// inactive workspace must still be recognised and managed; a window the shell
    /// cloaked - a suspended UWP app, or anything on a different Windows virtual
    /// desktop - must not be.
    /// </remarks>
    public enum CloakState
    {
        /// <summary>Not cloaked.</summary>
        None,

        /// <summary>
        /// Cloaked at the application level. This is what Shubbak's own cloak
        /// reports as, so these windows are still managed and are un-cloaked when
        /// their workspace becomes active.
        /// </summary>
        App,

        /// <summary>
        /// Cloaked by the shell: a suspended UWP app, or a window on another
        /// Windows virtual desktop. Not ours to manage.
        /// </summary>
        Shell,

        /// <summary>Cloaked because its owner is. Not ours to manage.</summary>
        Inherited,
    }

    /// <summary><c>DWMWA_CLOAK</c> - write to cloak or un-cloak a window.</summary>
    private const uint DwmwaCloak = 13;

    /// <summary><c>DWMWA_CLOAKED</c> - read to discover who cloaked it.</summary>
    private const uint DwmwaCloaked = 14;

    private const uint DwmCloakedApp = 0x00000001;
    private const uint DwmCloakedShell = 0x00000002;
    private const uint DwmCloakedInherited = 0x00000004;

    public static bool Exists(nint handle) => PInvoke.IsWindow(new HWND(handle));

    public static bool IsVisible(nint handle) => PInvoke.IsWindowVisible(new HWND(handle));

    public static bool IsMinimised(nint handle) => PInvoke.IsIconic(new HWND(handle));

    public static bool IsMaximised(nint handle) => PInvoke.IsZoomed(new HWND(handle));

    public static unsafe nint GetForeground() => (nint)PInvoke.GetForegroundWindow().Value;

    /// <summary>
    /// Reports who cloaked a window.
    /// </summary>
    /// <remarks>
    /// A cloaked window still reports <see cref="IsVisible"/> as true - it exists
    /// and is "shown", it is simply not composited. That property is what makes
    /// cloaking recoverable: a restarted Shubbak re-adopts its own cloaked windows
    /// through the ordinary path and un-cloaks them, whereas a window hidden with
    /// <c>SW_HIDE</c> would be rejected as invisible and stranded for good.
    /// </remarks>
    public static unsafe CloakState GetCloakState(nint handle)
    {
        uint cloaked = 0;
        HRESULT hr = PInvoke.DwmGetWindowAttribute(
            new HWND(handle), (DWMWINDOWATTRIBUTE)DwmwaCloaked, &cloaked, sizeof(uint));

        if (hr.Failed || cloaked == 0) return CloakState.None;

        // Checked in order of authority: a shell cloak outranks an app cloak, and
        // an inherited one means the decision was really made about the owner.
        if ((cloaked & DwmCloakedShell) != 0) return CloakState.Shell;
        if ((cloaked & DwmCloakedInherited) != 0) return CloakState.Inherited;
        if ((cloaked & DwmCloakedApp) != 0) return CloakState.App;

        return CloakState.None;
    }

    /// <summary>
    /// Cloaks a window so it stops being composited.
    /// </summary>
    /// <remarks>
    /// <para>
    /// How Shubbak hides the windows of an inactive workspace. Preferred over
    /// <c>ShowWindow(SW_HIDE)</c> for two reasons.
    /// </para>
    /// <para>
    /// First, recoverability: a cloaked window is still visible to
    /// <c>IsWindowVisible</c>, so if Shubbak exits, crashes or is killed, the next
    /// run adopts it normally and un-cloaks it. A hidden window is rejected by the
    /// filter as invisible and can never be recovered - it vanishes from Alt+Tab and
    /// the taskbar with the process still running.
    /// </para>
    /// <para>
    /// Second, compatibility: some applications treat <c>WM_SHOWWINDOW</c> with
    /// <c>FALSE</c> as a signal that the user dismissed them, and behave oddly
    /// afterwards. Cloaking happens below the application entirely.
    /// </para>
    /// </remarks>
    /// <returns>False if the compositor refused, so the caller can fall back.</returns>
    public static unsafe bool Cloak(nint handle)
    {
        uint value = 1;
        HRESULT hr = PInvoke.DwmSetWindowAttribute(
            new HWND(handle), (DWMWINDOWATTRIBUTE)DwmwaCloak, &value, sizeof(uint));

        return hr.Succeeded;
    }

    /// <summary>Un-cloaks a window.</summary>
    public static unsafe bool Uncloak(nint handle)
    {
        uint value = 0;
        HRESULT hr = PInvoke.DwmSetWindowAttribute(
            new HWND(handle), (DWMWINDOWATTRIBUTE)DwmwaCloak, &value, sizeof(uint));

        return hr.Succeeded;
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

    /// <summary>
    /// The cursor position, in virtual-desktop coordinates.
    /// </summary>
    /// <remarks>
    /// Read when a drag finishes. The window's own rectangle is not enough to decide
    /// where it was dropped: the user grabs a title bar at an arbitrary offset, so
    /// the window's top-left can be a long way from the point they are pointing at.
    /// </remarks>
    public static (int X, int Y)? GetCursorPosition()
    {
        if (!PInvoke.GetCursorPos(out System.Drawing.Point point)) return null;

        return (point.X, point.Y);
    }

    public static uint GetProcessId(nint handle)    {
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
