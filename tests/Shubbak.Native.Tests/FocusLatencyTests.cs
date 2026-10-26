using System.Diagnostics;
using System.Globalization;
using Windows.Win32;
using Windows.Win32.Foundation;
using Xunit.Abstractions;

namespace Shubbak.Native.Tests;

/// <summary>
/// Focus must not join a foreign application's input queue: doing so makes the
/// daemon's animation and command dispatch wait for that application's UI thread.
/// Same-process windows cannot exercise that branch of the focus implementation.
/// </summary>
[Trait("Requires", Desktop.RequiresForeground)]
public sealed class FocusLatencyTests(ITestOutputHelper output) : IDisposable
{
    private readonly nint _original = Win32Window.GetForeground();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AForeignUiThreadCannotHoldUpTheFocusRequest(bool foregroundBusy)
    {
        using var previous = new ControlledWindow();
        using var target = new ControlledWindow();
        Assert.True(WindowActions.Focus(previous.Handle));
        TestWindow.PumpUntil(() => Win32Window.GetForeground() == previous.Handle);
        Assert.Equal(previous.Handle, Win32Window.GetForeground());

        ControlledWindow busy = foregroundBusy ? previous : target;
        busy.MakeBusy();

        long started = Stopwatch.GetTimestamp();
        bool accepted = WindowActions.Focus(target.Handle);
        double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        output.WriteLine($"busy {(foregroundBusy ? "foreground" : "target")}: focus returned in {elapsed:F2} ms, accepted={accepted}");

        // Not a microbenchmark. A generous bound distinguishes a posted request from
        // waiting out the controlled two-second pause, even on a loaded CI runner.
        Assert.True(elapsed < 1000, $"focus waited {elapsed:F0} ms for a busy application");
        Assert.True(accepted, Desktop.WhyNotInFront("the requested window"));
        busy.WaitUntilReady();

        TestWindow.PumpUntil(() => Win32Window.GetForeground() == target.Handle);
        Assert.Equal(target.Handle, Win32Window.GetForeground());
    }

    [Fact]
    public void RepeatedForeignFocusActuallyChangesTheForeground()
    {
        using var first = new ControlledWindow();
        using var second = new ControlledWindow();

        for (int i = 0; i < 12; i++)
        {
            nint wanted = (i % 2 == 0 ? first : second).Handle;
            Assert.True(WindowActions.Focus(wanted));
            TestWindow.PumpUntil(() => Win32Window.GetForeground() == wanted);
            Assert.Equal(wanted, Win32Window.GetForeground());
        }
    }

    [Fact]
    public void WarmForeignFocusRequestsAllocateNothing()
    {
        using var first = new ControlledWindow();
        using var second = new ControlledWindow();
        long allocated = 0;

        for (int i = 0; i < 24; i++)
        {
            nint wanted = (i % 2 == 0 ? first : second).Handle;
            long before = GC.GetAllocatedBytesForCurrentThread();
            bool accepted = WindowActions.Focus(wanted);
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            if (i >= 4) allocated += bytes;

            Assert.True(accepted);
            TestWindow.PumpUntil(() => Win32Window.GetForeground() == wanted);
            Assert.Equal(wanted, Win32Window.GetForeground());
        }

        Assert.Equal(0, allocated);
    }

    public void Dispose()
    {
        if (_original != 0 && Win32Window.Exists(_original)) WindowActions.Focus(_original);
    }

    private sealed class ControlledWindow : IDisposable
    {
        private readonly Process _process;

        public ControlledWindow()
        {
            DesktopLease.Acquire();
            Process[] running = Process.GetProcessesByName("shubbak-wm");
            foreach (Process process in running) process.Dispose();
            if (running.Length != 0) throw new InvalidOperationException("Stop shubbak-wm before testing foreign focus.");

            _process = Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Shubbak.TestWindow.exe"))
            {
                ArgumentList = { "--test-window" },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
            }) ?? throw new InvalidOperationException("could not start the controlled window");

            _process.StandardInput.Close();
            _process.BeginErrorReadLine();

            try
            {
                Handle = (nint)long.Parse(ReadLine(), CultureInfo.InvariantCulture);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public nint Handle { get; }

        private string ReadLine() => _process.StandardOutput.ReadLineAsync()
            .WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("the controlled window exited before replying");

        public void MakeBusy()
        {
            Assert.True(PInvoke.PostMessage(new HWND(Handle), PInvoke.WM_APP + 1, new WPARAM(2000), default));
            Assert.Equal("busy", ReadLine());
        }

        public void WaitUntilReady() => Assert.Equal("ready", ReadLine());

        public void Dispose()
        {
            if (!_process.HasExited)
            {
                if (Handle != 0) PInvoke.PostMessage(new HWND(Handle), PInvoke.WM_CLOSE, default, default);
                if (!_process.WaitForExit(5000))
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(5000);
                }
            }

            _process.Dispose();
        }
    }
}
