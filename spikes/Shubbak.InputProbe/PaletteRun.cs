using System.Diagnostics;
using Shubbak.Ipc;
using Shubbak.Native;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Shubbak.InputProbe;

/// <summary>Actual resident palette signal-to-foreground readiness, sampled at ~1 ms.</summary>
internal static class PaletteRun
{
    internal static async Task<int> Run(string[] args)
    {
        int count = Program.Number(args, "--operations", 100, 1, 1000);
        int pid = Program.Number(args, "--target-pid", 0, 1, int.MaxValue);
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var client = new IpcClient();
        await client.ConnectAsync(TimeSpan.FromSeconds(5), stop.Token).ConfigureAwait(false);
        using var pacer = new Pacer();
        nint original = Win32Window.GetForeground();
        var samples = new double[count];
        try
        {
            for (int i = -5; i < count; i++)
            {
                long start = Stopwatch.GetTimestamp();
                IpcResponse reply = await client.SendAsync("command", "signal \"palette\" \"windows\"", stop.Token).ConfigureAwait(false);
                if (!reply.Ok) throw new InvalidOperationException(reply.Error);
                nint foreground;
                do
                {
                    foreground = Win32Window.GetForeground();
                    if (Win32Window.GetProcessId(foreground) == pid) break;
                    if (Stopwatch.GetElapsedTime(start).TotalSeconds > 5) throw new TimeoutException("The palette never received foreground.");
                    pacer.Wait(1000);
                } while (true);
                if (Win32Window.GetClassName(foreground) != "DalilPaletteWindow") throw new InvalidOperationException("Unexpected foreground window in the palette process.");
                if (i >= 0) samples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                // Allow paint and the asynchronous source refresh to run. The metric
                // above is foreground readiness, not a claim about device-to-photon.
                pacer.Wait(50_000);
                if (Win32Window.GetForeground() != foreground) throw new InvalidOperationException("Foreground changed during the palette check.");
                Escape();
                long closed = Stopwatch.GetTimestamp();
                while (Win32Window.IsVisible(foreground))
                {
                    if (Stopwatch.GetElapsedTime(closed).TotalSeconds > 5) throw new TimeoutException("The palette did not close on Escape.");
                    pacer.Wait(1000);
                }
                pacer.Wait(20_000);
            }
            Program.Report(new ProbeReport { Label = "palette-foreground", Measurement = "signal-to-foreground-poll-1ms", Sent = count, Received = count, Delivery = Distribution.Of(samples) });
            return 0;
        }
        finally
        {
            nint foreground = Win32Window.GetForeground();
            if (Win32Window.GetProcessId(foreground) == pid && Win32Window.GetClassName(foreground) == "DalilPaletteWindow") Escape();
            if (original != 0 && Win32Window.Exists(original)) WindowActions.Focus(original);
        }
    }

    private static unsafe void Escape()
    {
        Span<INPUT> inputs = stackalloc INPUT[2];
        inputs.Clear();
        for (int i = 0; i < 2; i++)
        {
            inputs[i].type = INPUT_TYPE.INPUT_KEYBOARD;
            inputs[i].Anonymous.ki.wVk = VIRTUAL_KEY.VK_ESCAPE;
            inputs[i].Anonymous.ki.dwFlags = i == 0 ? 0 : KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP;
        }
        if (PInvoke.SendInput(inputs, sizeof(INPUT)) != 2) throw new InvalidOperationException("Escape injection was not accepted.");
    }
}
