using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Shubbak.TestWindow;

/// <summary>Temporarily controls the system animation preference for desktop tests.</summary>
/// <remarks>
/// The caller must own the desktop test lease. No setting is written to the user
/// profile or broadcast; the original session value is restored even on test failure.
/// This belongs to the test harness: the real daemon must respect the OS preference.
/// </remarks>
public sealed class AnimationPreferenceScope : IDisposable
{
    private readonly bool _changed;
    private bool _disposed;

    public AnimationPreferenceScope(bool enabled)
    {
        Process[] running = Process.GetProcessesByName("shubbak-wm");
        foreach (Process process in running) process.Dispose();
        if (running.Length != 0)
            throw new InvalidOperationException("Stop shubbak-wm before controlling the desktop's animation preference.");

        OriginalEnabled = Read();
        if (OriginalEnabled == enabled) return;

        try
        {
            Write(enabled);
            if (Read() != enabled)
                throw new InvalidOperationException("Windows accepted but did not apply the test animation preference.");
            _changed = true;
        }
        catch
        {
            Write(OriginalEnabled);
            throw;
        }
    }

    public bool OriginalEnabled { get; }

    public static unsafe bool Read()
    {
        BOOL enabled = default;
        if (!PInvoke.SystemParametersInfo(SYSTEM_PARAMETERS_INFO_ACTION.SPI_GETCLIENTAREAANIMATION, 0, &enabled, 0))
            throw new InvalidOperationException($"Could not read the animation preference: Win32 error {Marshal.GetLastWin32Error()}.");
        return enabled;
    }

    private static unsafe void Write(bool enabled)
    {
        // SET takes the BOOL value in pvParam, whereas GET takes a pointer to a BOOL.
        // Zero flags keep this temporary: no SPIF_UPDATEINIFILE or SPIF_SENDCHANGE.
        if (!PInvoke.SystemParametersInfo(
                SYSTEM_PARAMETERS_INFO_ACTION.SPI_SETCLIENTAREAANIMATION, 0, (void*)(enabled ? 1 : 0), 0))
            throw new InvalidOperationException($"Could not set the animation preference: Win32 error {Marshal.GetLastWin32Error()}.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (_changed) Write(OriginalEnabled);
        _disposed = true;
    }
}
