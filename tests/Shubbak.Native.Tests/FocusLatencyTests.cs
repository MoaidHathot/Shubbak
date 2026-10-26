using System.Diagnostics;
using Shubbak.TestWindow;
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
        DesktopLease.Acquire();
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
        DesktopLease.Acquire();
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
        DesktopLease.Acquire();
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
}
