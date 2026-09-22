using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Shubbak.Native.Tests;

/// <summary>
/// Real keystrokes, for the tests whose subject reads the keyboard.
/// </summary>
/// <remarks>
/// <para>
/// The focus sink decides whether it was cycled onto partly by whether Alt is down at
/// the moment it is activated, and reads that with <c>GetAsyncKeyState</c> - which
/// reports the physical keyboard, or what <c>SendInput</c> has told the system the
/// physical keyboard is doing. Nothing short of a real input event can stand in for
/// it, so these tests press keys the way <see cref="HookFaultTests"/> does.
/// </para>
/// <para>
/// With the timing of a hand rather than of a loop. Alt+Esc is processed while Esc is
/// going down, so Alt has to be down before Esc and still down for a moment after;
/// the system's own handling of the chord runs on the thread of the window in front,
/// and needs time to arrive there.
/// </para>
/// </remarks>
internal static class Keyboard
{
    public static void Press(VIRTUAL_KEY key) => Send(key, up: false);

    public static void Release(VIRTUAL_KEY key) => Send(key, up: true);

    /// <summary>Alt+Esc, as a person presses it: Alt, then Esc, then both released.</summary>
    public static void AltEsc()
    {
        Press(VIRTUAL_KEY.VK_MENU);
        Thread.Sleep(30);
        Press(VIRTUAL_KEY.VK_ESCAPE);
        Thread.Sleep(60);
        Release(VIRTUAL_KEY.VK_ESCAPE);
        Thread.Sleep(60);
        Release(VIRTUAL_KEY.VK_MENU);
    }

    private static void Send(VIRTUAL_KEY key, bool up)
    {
        var input = new INPUT { type = INPUT_TYPE.INPUT_KEYBOARD };
        input.Anonymous.ki.wVk = key;
        if (up) input.Anonymous.ki.dwFlags = KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP;

        Span<INPUT> one = [input];

        _ = PInvoke.SendInput(one, Marshal.SizeOf<INPUT>());
    }
}
