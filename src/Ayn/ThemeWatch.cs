using Ayn.Core;
using Microsoft.Win32;
using Shubbak.Core.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Registry;

namespace Ayn;

/// <summary>
/// Whether apps are set to the dark theme, and a way to be woken when that changes.
/// </summary>
/// <remarks>
/// <para>
/// One registry value, <c>AppsUseLightTheme</c> under the user's <c>Personalize</c>
/// key: zero is dark. Settings writes it when the theme is switched, and so does
/// every "auto dark mode" tool, which is the point - a context that follows the theme
/// follows whatever is driving the theme, at sunset or on a schedule, without the
/// watcher knowing about schedules.
/// </para>
/// <para>
/// Watched the way the consent store is: a registry notification on the key, armed
/// again after every wake. There is no polling. A key that cannot be opened - a
/// locked-down profile - is one warning and the fact stays unreported.
/// </para>
/// </remarks>
internal sealed class ThemeWatch : IDisposable
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string ValueName = "AppsUseLightTheme";

    private RegistryKey? _key;
    private bool _complained;

    /// <summary>Signalled when anything under the key changes; the loop waits on it.</summary>
    public AutoResetEvent Changed { get; } = new(false);

    /// <summary>Whether the key is open and watched.</summary>
    public bool IsOpen => _key is not null;

    /// <summary>Opens and arms the watch. False when the key is not there or cannot be read.</summary>
    public bool Open()
    {
        if (IsOpen) return true;

        try
        {
            _key = Registry.CurrentUser.OpenSubKey(
                KeyPath, System.Security.AccessControl.RegistryRights.ReadKey | System.Security.AccessControl.RegistryRights.Notify);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Warn(LogCategory.Wm, $"could not open HKCU\\...\\Personalize; the theme will not be reported: {ex.Message}");
            return false;
        }

        if (_key is null)
        {
            Log.Warn(LogCategory.Wm, "HKCU\\...\\Personalize is not there; the theme will not be reported");
            return false;
        }

        Arm();
        return true;
    }

    /// <summary>Whether apps are set to the dark theme, or null when the value is not there or the key is not open.</summary>
    public bool? IsDark()
    {
        if (_key is null) return null;

        try
        {
            return _key.GetValue(ValueName) is int light ? light == 0 : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Debug(LogCategory.Wm, $"could not read {ValueName}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Re-arms the notification; called after every wake, since each fires once.</summary>
    public void Arm()
    {
        if (_key is null) return;

        WIN32_ERROR result = PInvoke.RegNotifyChangeKeyValue(
            _key.Handle,
            false,
            REG_NOTIFY_FILTER.REG_NOTIFY_CHANGE_LAST_SET,
            Changed.SafeWaitHandle,
            true);

        if (result != WIN32_ERROR.ERROR_SUCCESS && !_complained)
        {
            _complained = true;
            Log.Warn(LogCategory.Wm, $"could not watch HKCU\\...\\Personalize (error {(uint)result}); theme changes will be missed");
        }
    }

    public void Dispose()
    {
        _key?.Dispose();
        _key = null;
        Changed.Dispose();
    }
}
