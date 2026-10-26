using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Shubbak.TestWindow;

/// <summary>
/// A window whose UI thread can be made busy without suspending or injecting into an
/// application. Only test-owned processes are used to measure cross-process focus.
/// </summary>
internal static class Program
{
    private const string ClassName = "ShubbakControlledTestWindow";

    private static unsafe int Main(string[] args)
    {
        if (args.Length != 1 || args[0] != "--test-window") return 1;

        fixed (char* name = ClassName)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WindowProc,
                lpszClassName = name,
            };

            if (PInvoke.RegisterClassEx(in wc) == 0) return 2;
        }

        HWND window = PInvoke.CreateWindowEx(
            0, ClassName, "Shubbak controlled test window",
            WINDOW_STYLE.WS_OVERLAPPEDWINDOW,
            100, 100, 320, 240, HWND.Null, (SafeHandle?)null, (SafeHandle?)null, null);

        if (window.IsNull) return 3;

        try
        {
            PInvoke.ShowWindow(window, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);
            Console.WriteLine(((nint)window.Value).ToString(CultureInfo.InvariantCulture));

            while (PInvoke.GetMessage(out MSG message, default, 0, 0))
            {
                if (message.message is PInvoke.WM_CLOSE or PInvoke.WM_QUIT) break;

                if (message.message == PInvoke.WM_APP + 1)
                {
                    // Bounded even if the parent dies. The acknowledgement makes the
                    // busy period a precondition, rather than a race with the caller.
                    Console.WriteLine("busy");
                    Thread.Sleep((int)Math.Min(message.wParam.Value, 2000u));
                    Console.WriteLine("ready");
                    continue;
                }

                PInvoke.TranslateMessage(in message);
                PInvoke.DispatchMessage(in message);
            }
        }
        finally
        {
            PInvoke.DestroyWindow(window);
        }

        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT WindowProc(HWND window, uint message, WPARAM wParam, LPARAM lParam) =>
        PInvoke.DefWindowProc(window, message, wParam, lParam);
}
