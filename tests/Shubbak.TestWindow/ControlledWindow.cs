using System.Diagnostics;
using System.Globalization;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Shubbak.TestWindow;

/// <summary>A test-owned foreign window with a controllable, bounded UI-thread pause.</summary>
/// <remarks>The caller must hold the shared desktop test lease before constructing one.</remarks>
public sealed class ControlledWindow : IDisposable
{
    private readonly Process _process;
    private bool _disposed;

    public ControlledWindow()
    {
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

        try
        {
            _process.StandardInput.Close();
            _process.BeginErrorReadLine();
            Handle = (nint)long.Parse(ReadLine(), CultureInfo.InvariantCulture);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public nint Handle { get; }

    public int ForegroundCount
    {
        get
        {
            if (!PInvoke.PostMessage(new HWND(Handle), PInvoke.WM_APP + 2, default, default))
                throw new InvalidOperationException("the controlled window is not accepting messages");
            return int.Parse(ReadLine(), CultureInfo.InvariantCulture);
        }
    }

    public static unsafe uint LastInputTime
    {
        get
        {
            var info = new LASTINPUTINFO { cbSize = (uint)sizeof(LASTINPUTINFO) };
            if (!PInvoke.GetLastInputInfo(ref info)) throw new InvalidOperationException("could not read the last input time");
            return info.dwTime;
        }
    }

    private string ReadLine() => _process.StandardOutput.ReadLineAsync()
        .WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult()
        ?? throw new InvalidOperationException("the controlled window exited before replying");

    public void MakeBusy()
    {
        if (!PInvoke.PostMessage(new HWND(Handle), PInvoke.WM_APP + 1, new WPARAM(2000), default))
            throw new InvalidOperationException("the controlled window is not accepting messages");
        Expect("busy");
    }

    public void WaitUntilReady() => Expect("ready");

    private void Expect(string expected)
    {
        string reply = ReadLine();
        if (reply != expected) throw new InvalidOperationException($"expected '{expected}', received '{reply}'");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

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
