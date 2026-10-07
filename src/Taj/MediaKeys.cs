using System.Runtime.InteropServices;
using Shubbak.Core.Diagnostics;
using Taj.Core;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Taj;

/// <summary>
/// Presses a media key, as the keyboard would.
/// </summary>
/// <remarks>
/// <para>
/// A synthesised key press through <c>SendInput</c>, down and up, which is exactly
/// what the key on a keyboard produces: Windows routes it to the current media
/// session - the player, the browser tab, whichever has the transport - and to the
/// system volume for the volume keys, with the same on-screen flyout the key gets.
/// Nothing here knows which player that is, and nothing needs to; that is the point
/// of pressing the key rather than running a program.
/// </para>
/// <para>
/// The bar never activates itself, so the window in front when the pill is clicked
/// is still the one the user was working in, and a media key does not care about
/// focus anyway. The one case it does nothing is a window in front that runs at a
/// higher integrity level than the bar - an elevated console, say - where Windows
/// refuses injected input; the refusal is said in the log.
/// </para>
/// </remarks>
internal static class MediaKeys
{
    /// <summary>Presses the key a command names.</summary>
    /// <returns>Whether Windows took both halves of the press.</returns>
    public static bool Press(MediaCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        VIRTUAL_KEY key = command.Key switch
        {
            MediaKey.PlayPause => VIRTUAL_KEY.VK_MEDIA_PLAY_PAUSE,
            MediaKey.Next => VIRTUAL_KEY.VK_MEDIA_NEXT_TRACK,
            MediaKey.Previous => VIRTUAL_KEY.VK_MEDIA_PREV_TRACK,
            MediaKey.Stop => VIRTUAL_KEY.VK_MEDIA_STOP,
            MediaKey.Mute => VIRTUAL_KEY.VK_VOLUME_MUTE,
            MediaKey.VolumeUp => VIRTUAL_KEY.VK_VOLUME_UP,
            _ => VIRTUAL_KEY.VK_VOLUME_DOWN,
        };

        var down = new INPUT { type = INPUT_TYPE.INPUT_KEYBOARD };
        down.Anonymous.ki.wVk = key;

        var up = new INPUT { type = INPUT_TYPE.INPUT_KEYBOARD };
        up.Anonymous.ki.wVk = key;
        up.Anonymous.ki.dwFlags = KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP;

        // Both in one call, so nothing can land between the down and the up and the
        // key cannot be left held - a held volume key would ramp.
        Span<INPUT> press = [down, up];
        uint sent = PInvoke.SendInput(press, Marshal.SizeOf<INPUT>());

        if (sent != 2)
        {
            Log.Warn(LogCategory.Wm,
                $"{command}: Windows took {sent} of 2 input events; the window in front may be elevated");
            return false;
        }

        if (Log.IsEnabled(LogLevel.Debug)) Log.Debug(LogCategory.Wm, $"{command}: pressed");

        return true;
    }
}
