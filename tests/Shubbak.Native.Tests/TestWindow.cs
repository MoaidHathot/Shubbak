using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Shubbak.Native.Tests;

/// <summary>
/// A real, throwaway top-level window.
/// </summary>
/// <remarks>
/// The platform layer cannot be tested against a stub: whether a window is cloaked,
/// whether the compositor accepted the request, and whether the filter then still
/// recognises it are all questions only Windows can answer. This creates a genuine
/// window, which is cheap - a borderless popup that is never painted - and disposes
/// of it afterwards.
/// </remarks>
internal sealed class TestWindow : IDisposable
{
    private const string ClassName = "ShubbakNativeTestWindow";
    private static bool s_registered;
    private static readonly Lock s_gate = new();

    private HWND _handle;
    private bool _disposed;

    public unsafe TestWindow(string title = "Shubbak test window", bool visible = true)
    {
        EnsureClassRegistered();

        _handle = PInvoke.CreateWindowEx(
            WINDOW_EX_STYLE.WS_EX_NOACTIVATE,
            ClassName,
            title,
            WINDOW_STYLE.WS_POPUP | WINDOW_STYLE.WS_CAPTION | WINDOW_STYLE.WS_SYSMENU,
            100, 100, 320, 240,
            HWND.Null, (SafeHandle?)null, (SafeHandle?)null, null);

        if (_handle.IsNull)
            throw new InvalidOperationException(
                $"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");

        if (visible) PInvoke.ShowWindow(_handle, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);
    }

    public unsafe nint Handle => (nint)_handle.Value;

    public bool IsVisible => PInvoke.IsWindowVisible(_handle);

    /// <summary>
    /// Drains this thread's message queue.
    /// </summary>
    /// <remarks>
    /// The committer conceals and reveals with <c>ShowWindowAsync</c>, which posts to
    /// the owning window's thread rather than acting immediately - deliberately, so a
    /// hung application cannot stall a whole relayout. Test windows are created on the
    /// test thread, so the effect only lands once that thread pumps.
    /// </remarks>
    public static void Pump()
    {
        // A bounded loop: an unbounded one would hang the test run if something kept
        // posting messages.
        for (int i = 0; i < 128; i++)
        {
            if (!PInvoke.PeekMessage(out MSG msg, default, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_REMOVE))
                return;

            PInvoke.TranslateMessage(in msg);
            PInvoke.DispatchMessage(in msg);
        }
    }

    private static unsafe void EnsureClassRegistered()
    {
        lock (s_gate)
        {
            if (s_registered) return;

            fixed (char* className = ClassName)
            {
                var wc = new WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(WNDCLASSEXW),
                    lpfnWndProc = &WindowProc,
                    hInstance = HINSTANCE.Null,
                    lpszClassName = className,
                };

                // 1410 is "class already registered", which is fine across test runs
                // in the same process.
                if (PInvoke.RegisterClassEx(in wc) == 0 && Marshal.GetLastWin32Error() != 1410)
                    throw new InvalidOperationException(
                        $"RegisterClassEx failed: {Marshal.GetLastWin32Error()}");
            }

            s_registered = true;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT WindowProc(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam) =>
        PInvoke.DefWindowProc(hwnd, message, wParam, lParam);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (!_handle.IsNull)
        {
            PInvoke.DestroyWindow(_handle);
            _handle = HWND.Null;
        }
    }
}
