using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Shubbak.Native.Tests;

/// <summary>
/// A real, throwaway top-level window with a thread of its own.
/// </summary>
/// <remarks>
/// <para>
/// The platform layer cannot be tested against a stub: whether a window is cloaked,
/// whether the shell accepted the request, and whether the filter then still
/// recognises it are all questions only Windows can answer. This creates a genuine
/// window - a borderless popup that is never painted - and disposes of it afterwards.
/// </para>
/// <para>
/// It runs on its own thread, and that is not incidental. The committer conceals with
/// <c>ShowWindowAsync</c>, which hands the request to the thread owning the window and
/// requires that thread to be waiting on its input queue. Sharing xunit's thread made
/// that unreliable: other tests initialise COM on it, and a window whose owner is off
/// doing something else does not process the request. The result was a suite that
/// failed perhaps one run in ten, always on visibility, never reproducibly.
/// </para>
/// <para>
/// With a dedicated pump the window is always ready to service its own messages, and
/// waiting for an outcome becomes a matter of watching for it rather than hoping the
/// test thread pumps at the right moment.
/// </para>
/// </remarks>
internal sealed class TestWindow : IDisposable
{
    private const string ClassName = "ShubbakNativeTestWindow";
    private static bool s_registered;
    private static readonly Lock s_gate = new();

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);

    private HWND _handle;
    private Exception? _failure;
    private bool _disposed;

    public TestWindow(string title = "Shubbak test window", bool visible = true)
    {
        FailIfAWindowManagerIsRunning();

        _thread = new Thread(() => Run(title, visible))
        {
            Name = "Shubbak test window",
            IsBackground = true,
        };

        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(10));

        if (_failure is not null) throw _failure;

        if (_handle.IsNull)
            throw new InvalidOperationException("the test window never appeared");
    }

    /// <summary>
    /// Refuses to run these tests while a window manager is live.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A test window is a real, captioned, titled top-level window, which is to say
    /// exactly the thing a running Shubbak exists to manage. It will tile it, conceal
    /// it on a workspace switch and reveal it again - so a test asserting where the
    /// window is, or whether it is visible, is racing the very software it is testing.
    /// </para>
    /// <para>
    /// This was not theoretical. A test window was found at (1276,7 1285x1434) when
    /// it had been created at (100,100 320x240), and an earlier round of intermittent
    /// failures about SW_HIDE never taking effect is best explained the same way: the
    /// window manager was revealing the windows as fast as the tests hid them. Several
    /// hours went into hardening the tests against a race that was a live window
    /// manager all along.
    /// </para>
    /// <para>
    /// Failing loudly is the point. A skipped test looks like a passing suite, and a
    /// silently wrong result is what cost the time.
    /// </para>
    /// </remarks>
    private static void FailIfAWindowManagerIsRunning()
    {
        if (Process.GetProcessesByName("shubbak-wm").Length == 0) return;

        throw new InvalidOperationException(
            "shubbak-wm is running. These tests create real windows, which it will " +
            "manage, move and conceal - any result would be measuring the window " +
            "manager rather than the code under test. Stop it and run them again.");
    }

    public unsafe nint Handle => (nint)_handle.Value;

    public bool IsVisible => PInvoke.IsWindowVisible(_handle);

    /// <summary>
    /// Waits for <paramref name="until"/> to hold.
    /// </summary>
    /// <remarks>
    /// No pumping here: the window's own thread does that continuously. This only
    /// watches, which is what makes it a fair way to observe an asynchronous API.
    /// </remarks>
    public static void PumpUntil(Func<bool> until, int timeoutMs = 5000)
    {
        ArgumentNullException.ThrowIfNull(until);

        long deadline = Environment.TickCount64 + timeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            if (until()) return;

            Thread.Sleep(5);
        }

        // Left to the caller's assertion to report - it knows what it was waiting for.
    }

    /// <summary>Kept for tests that only need a moment to pass.</summary>
    public static void PumpOnce() => Thread.Sleep(20);

    private unsafe void Run(string title, bool visible)
    {
        try
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
        catch (Exception ex)
        {
            _failure = ex;
            return;
        }
        finally
        {
            _ready.Set();
        }

        // Blocking pump for the life of the window, so a request handed to this
        // thread is serviced immediately rather than whenever someone else remembers
        // to pump.
        while (PInvoke.GetMessage(out MSG message, default, 0, 0))
        {
            if (message.message is PInvoke.WM_QUIT or PInvoke.WM_CLOSE) break;

            PInvoke.TranslateMessage(in message);
            PInvoke.DispatchMessage(in message);
        }

        if (!_handle.IsNull)
        {
            PInvoke.DestroyWindow(_handle);
            _handle = HWND.Null;
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

        // Posted to the window rather than the thread. Thread ids are recycled, and a
        // WM_QUIT that arrives after its thread has gone can be delivered to whichever
        // new thread inherited the id - which would stop the next test window pumping
        // before it had started.
        if (!_handle.IsNull) PInvoke.PostMessage(_handle, PInvoke.WM_CLOSE, default, default);

        _thread.Join(TimeSpan.FromSeconds(5));
        _ready.Dispose();
    }
}
