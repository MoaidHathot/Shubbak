using Shubbak.Core.Rendering;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Shubbak.Native;

/// <summary>
/// The colours Windows is set to, for configs that would rather follow the machine
/// than hard-code a blue.
/// </summary>
/// <remarks>
/// <para>
/// The accent is read from the compositor's colorization colour, which is what the
/// accent has been since Windows 8: the documented call, and the one that changes
/// the moment the user picks another colour in Settings or their wallpaper does it
/// for them. The colorization value carries an alpha that is the compositor's own
/// blend weight rather than anything about the colour, so it is dropped.
/// </para>
/// <para>
/// Every executable hands this to <see cref="Colour.AccentSource"/> at startup, so
/// <c>accent</c> means the same thing in the bar, the palette and a focus border.
/// The core cannot ask for itself, being free of Win32 on purpose.
/// </para>
/// </remarks>
public static class SystemColours
{
    /// <summary>The machine's accent colour, or null when the compositor will not say.</summary>
    public static unsafe Colour? Accent()
    {
        uint colorization;
        BOOL opaque;

        HRESULT result = PInvoke.DwmGetColorizationColor(&colorization, &opaque);

        if (result.Failed) return null;

        // 0xAARRGGBB.
        return new Colour(
            (byte)((colorization >> 16) & 0xFF),
            (byte)((colorization >> 8) & 0xFF),
            (byte)(colorization & 0xFF));
    }

    /// <summary>Makes <c>accent</c> mean this machine's accent for the rest of the process.</summary>
    public static void Adopt() => Colour.AccentSource = Accent;
}
