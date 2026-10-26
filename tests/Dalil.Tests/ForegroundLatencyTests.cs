using System.Diagnostics;
using Shubbak.Companion;
using Shubbak.Core.Geometry;
using Shubbak.Native;
using Shubbak.TestWindow;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using Xunit.Abstractions;

namespace Dalil.Tests;

[Trait("Requires", "Foreground")]
public sealed class ForegroundLatencyTests(ITestOutputHelper output)
{
    [Fact]
    public void TakingThePalettesKeyboardDoesNotWaitForABusyForeignForeground()
    {
        // The same desktop lease as Native.Tests and EndToEnd.Tests, held on this
        // thread for this test only. The rest of the palette suite is headless.
        using var desktop = new Mutex(false, @"Local\shubbak-tests-the-desktop");
        try
        {
            Assert.True(desktop.WaitOne(TimeSpan.FromMinutes(2)), "the desktop test lease was not released");
        }
        catch (AbandonedMutexException)
        {
            // The previous test host died; ownership has passed to this thread.
        }

        nint original = Win32Window.GetForeground();
        try
        {
            using var foreign = new ControlledWindow();
            using var own = new PaletteStandIn();
            own.ShowWithoutActivating();
            Assert.True(WindowActions.Focus(foreign.Handle));
            PumpUntil(() => Win32Window.GetForeground() == foreign.Handle);
            Assert.Equal(foreign.Handle, Win32Window.GetForeground());
            foreign.MakeBusy();

            long start = Stopwatch.GetTimestamp();
            ForegroundAttempt attempt = Foreground.Take(new HWND(own.Handle));
            double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            output.WriteLine($"palette focus with busy foreground: {elapsed:F2} ms, in front={attempt.InFront}");
            Assert.True(elapsed < 1000, $"palette focus waited {elapsed:F0} ms for a busy application");

            foreign.WaitUntilReady();
            PumpUntil(() => Win32Window.GetForeground() == own.Handle && own.HasKeyboardFocus);
            Assert.Equal(own.Handle, Win32Window.GetForeground());
            Assert.True(own.HasKeyboardFocus);

            // Reopening an already foreground palette must still repair local focus.
            PInvoke.SetFocus(HWND.Null);
            Assert.False(own.HasKeyboardFocus);
            Assert.True(Foreground.Take(new HWND(own.Handle)).InFront);
            Assert.True(own.HasKeyboardFocus);
        }
        finally
        {
            if (original != 0 && Win32Window.Exists(original)) WindowActions.Focus(original);
            desktop.ReleaseMutex();
        }
    }

    private static void PumpUntil(Func<bool> done)
    {
        long start = Stopwatch.GetTimestamp();
        using var loop = new Shubbak.Companion.MessageLoop();
        loop.Run(() => !done() && Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(5), () => 10);
    }

    private sealed class PaletteStandIn : CompanionWindow
    {
        public PaletteStandIn() : base(new WindowClassOptions("ShubbakPaletteFocusTest"))
        {
            Assert.True(CreateWindow(
                (uint)WINDOW_EX_STYLE.WS_EX_TOOLWINDOW, (uint)WINDOW_STYLE.WS_POPUP,
                "Shubbak palette focus test", new Rect(100, 100, 320, 240)));
        }

        public bool HasKeyboardFocus { get; private set; }

        protected override bool OnMessage(uint message, nuint wParam, nint lParam, out nint result)
        {
            if (message == 7) HasKeyboardFocus = true; // WM_SETFOCUS
            if (message == 8) HasKeyboardFocus = false; // WM_KILLFOCUS
            return base.OnMessage(message, wParam, lParam, out result);
        }

        protected override void OnPaint() { }
    }
}
