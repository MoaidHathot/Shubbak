using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Shubbak.Native;

/// <summary>
/// What the operating system says about animating, and how fast.
/// </summary>
/// <remarks>
/// <para>
/// Three questions the program did not ask. Each is one call, and each has an answer
/// the user or the situation has already given somewhere else.
/// </para>
/// </remarks>
public static class DisplayPreferences
{
    /// <summary>
    /// Whether the user has left animations switched on in Windows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>SPI_GETCLIENTAREAANIMATION</c> is the setting behind Settings, Accessibility,
    /// Visual effects, "Animation effects". Someone who turns it off has answered the
    /// question in the place the operating system asks it - often because motion makes
    /// them ill - and a window manager that animates anyway has overruled them.
    /// </para>
    /// <para>
    /// This is not a performance setting and is not treated as one. It is a preference
    /// that was already expressed.
    /// </para>
    /// </remarks>
    public static unsafe bool SystemWantsAnimation()
    {
        var wanted = new BOOL(true);

        bool ok = PInvoke.SystemParametersInfo(
            SYSTEM_PARAMETERS_INFO_ACTION.SPI_GETCLIENTAREAANIMATION,
            0,
            &wanted,
            0);

        // A failure means the question could not be asked, not that the answer is no.
        // Refusing to animate because a system call failed would be a worse guess than
        // carrying on.
        return !ok || wanted;
    }

    /// <summary>
    /// Whether this session is being viewed over a remote connection.
    /// </summary>
    /// <remarks>
    /// Every frame of every animation is a screen region on the wire. Twenty of them
    /// to move a window somewhere it could be told about once is a poor trade over a
    /// network, and worse the slower the link. The window still arrives where it
    /// belongs; it simply arrives directly.
    /// </remarks>
    public static bool IsRemoteSession() =>
        PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_REMOTESESSION) != 0;

    /// <summary>
    /// The refresh rate of a display, in hertz, or zero if it cannot be read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There was no refresh-rate query anywhere in the program. The frame interval was
    /// a constant - seven milliseconds, then whatever the configuration named - and
    /// the number of frames a panel can actually show played no part in it.
    /// </para>
    /// <para>
    /// The cost of guessing runs both ways. On a sixty hertz panel, asking for ninety
    /// means half as many frames again as the display can present, and the compositor
    /// discards them after the applications have already been asked to repaint. On a
    /// faster panel the same constant delivers a fraction of what it would take.
    /// </para>
    /// <para>
    /// <c>ENUM_CURRENT_SETTINGS</c> reports the mode in force rather than one the
    /// adapter merely supports. It returns whole hertz, so 59.94 arrives as 59 - which
    /// is close enough to matter to nobody, and is why the caller treats a rate below
    /// a plausible floor as unreadable rather than obeying it.
    /// </para>
    /// </remarks>
    public static unsafe int RefreshRateHz(string deviceName)
    {
        var mode = new DEVMODEW { dmSize = (ushort)sizeof(DEVMODEW) };

        if (!PInvoke.EnumDisplaySettings(
                deviceName, ENUM_DISPLAY_SETTINGS_MODE.ENUM_CURRENT_SETTINGS, ref mode))
        {
            return 0;
        }

        // Zero and one are documented as meaning "the hardware default", which is not
        // a rate anything can be derived from.
        return mode.dmDisplayFrequency <= 1 ? 0 : (int)mode.dmDisplayFrequency;
    }
}
