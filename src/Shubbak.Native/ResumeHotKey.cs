using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Shubbak.Native;

/// <summary>
/// One chord the system watches for on our behalf, with no hook.
/// </summary>
/// <remarks>
/// <para>
/// This exists so that suspending can be undone. Suspending removes the low-level
/// keyboard hook - that is the point of it - which also removes the only way Shubbak
/// had of noticing a keystroke. A suspend that can only be undone with the mouse or
/// another program is the trap the pause command's own comment warns about, so
/// something has to keep listening.
/// </para>
/// <para>
/// <c>RegisterHotKey</c> is that something, and it is a fundamentally different
/// mechanism from a hook. A hook runs our code on every keystroke the machine
/// receives; this asks the system to watch for one combination and post a single
/// <c>WM_HOTKEY</c> when it matches. Nothing of ours executes for any other key, so a
/// suspended window manager costs the input path nothing at all - which is the whole
/// reason suspending is worth having over exiting.
/// </para>
/// <para>
/// The chord is registered to the calling <em>thread</em>, so <c>WM_HOTKEY</c> arrives
/// as a thread message with no window. It is therefore observed in the message pump
/// rather than dispatched: <c>DispatchMessage</c> has no window procedure to hand it
/// to and would silently discard it.
/// </para>
/// </remarks>
public sealed class ResumeHotKey : IDisposable
{
    /// <summary>
    /// Identifies this registration among any others on the same thread.
    /// </summary>
    /// <remarks>
    /// Arbitrary but fixed, and compared on arrival so that a <c>WM_HOTKEY</c> meant
    /// for something else is not read as a request to resume.
    /// </remarks>
    public const int Id = 0x5B10;

    /// <summary>
    /// The message the system posts when the chord matches.
    /// </summary>
    /// <remarks>
    /// Surfaced here so the daemon can recognise it without taking a dependency on
    /// generated Win32 constants, which it otherwise has none of.
    /// </remarks>
    public const uint Message = PInvoke.WM_HOTKEY;

    private bool _registered;

    /// <summary>Whether the system is currently watching for the chord.</summary>
    public bool IsRegistered => _registered;

    /// <summary>The chord as it was written in the config, for logging.</summary>
    public string? Display { get; private set; }

    /// <summary>
    /// Asks the system to watch for a chord.
    /// </summary>
    /// <param name="modifiers">Modifiers, in <see cref="KeyModifiers"/> terms.</param>
    /// <param name="virtualKey">The main key.</param>
    /// <param name="display">The chord as written, for logging.</param>
    /// <returns>Whether the registration succeeded.</returns>
    /// <remarks>
    /// Failure is normal and survivable: another program may already own the chord,
    /// and <c>RegisterHotKey</c> refuses rather than sharing. The caller reports it and
    /// carries on, because a suspend that cannot be undone by keyboard is still
    /// undoable by <c>shubbak wm-resume</c>.
    /// </remarks>
    public bool Register(KeyModifiers modifiers, ushort virtualKey, string display)
    {
        Unregister();

        HOT_KEY_MODIFIERS flags = Translate(modifiers);

        // Without this the chord also fires while it is held down, at the hardware
        // repeat rate, so one press would resume and then immediately re-suspend.
        flags |= HOT_KEY_MODIFIERS.MOD_NOREPEAT;

        if (!PInvoke.RegisterHotKey(default, Id, flags, virtualKey)) return false;

        _registered = true;
        Display = display;

        return true;
    }

    /// <summary>Stops the system watching.</summary>
    public void Unregister()
    {
        if (!_registered) return;

        PInvoke.UnregisterHotKey(default, Id);

        _registered = false;
        Display = null;
    }

    private static HOT_KEY_MODIFIERS Translate(KeyModifiers modifiers)
    {
        HOT_KEY_MODIFIERS flags = 0;

        if (modifiers.HasFlag(KeyModifiers.Alt)) flags |= HOT_KEY_MODIFIERS.MOD_ALT;
        if (modifiers.HasFlag(KeyModifiers.Control)) flags |= HOT_KEY_MODIFIERS.MOD_CONTROL;
        if (modifiers.HasFlag(KeyModifiers.Shift)) flags |= HOT_KEY_MODIFIERS.MOD_SHIFT;
        if (modifiers.HasFlag(KeyModifiers.Windows)) flags |= HOT_KEY_MODIFIERS.MOD_WIN;

        return flags;
    }

    public void Dispose() => Unregister();
}
