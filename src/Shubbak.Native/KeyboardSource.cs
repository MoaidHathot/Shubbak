using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Shubbak.Native;

/// <summary>Modifier keys held when a key event occurred.</summary>
[Flags]
public enum KeyModifiers
{
    None = 0,
    Alt = 1 << 0,
    Control = 1 << 1,
    Shift = 1 << 2,
    Windows = 1 << 3,
}

/// <summary>A keystroke as seen by the low-level hook.</summary>
/// <param name="VirtualKey">Virtual-key code.</param>
/// <param name="Modifiers">Modifiers held at the time.</param>
/// <param name="IsKeyDown">False for key-up.</param>
/// <param name="IsInjected">True when synthesised by software rather than typed.</param>
public readonly record struct KeyEvent(
    ushort VirtualKey,
    KeyModifiers Modifiers,
    bool IsKeyDown,
    bool IsInjected);

/// <summary>
/// A global low-level keyboard hook.
/// </summary>
/// <remarks>
/// <para>
/// This is the most latency-sensitive code in Shubbak. If the callback does not
/// return within <c>LowLevelHooksTimeout</c> (300 ms by default) Windows silently
/// unhooks us and every keybinding stops working with no error surfaced.
/// </para>
/// <para>
/// The design is the one P0's S1 spike validated: the callback <b>allocates
/// nothing</b>, writes a small struct into a pre-allocated ring buffer, and returns.
/// A separate thread drains the buffer and does the real work. Measured under
/// hostile GC load - ~1300 forced blocking compacting Gen2 collections and ~200 GB
/// of allocation - the callback's p99.9 was <b>0.8-1.0 microseconds</b>, with a
/// worst case of 2.99 ms across 4 million events.
/// </para>
/// <para>
/// The rule that makes those numbers hold: <b>nothing in
/// <see cref="Callback"/> may allocate</b> - no LINQ, no closures, no boxing, no
/// string work (docs/adr/0001-language-choice.md, constraint 1).
/// </para>
/// </remarks>
public sealed class KeyboardSource : IDisposable
{
    private static KeyboardSource? s_instance;

    /// <summary>
    /// Decides whether a keystroke is bound, and therefore whether to swallow it.
    /// </summary>
    /// <remarks>
    /// Runs <b>inside</b> the hook callback, so it must be allocation-free and
    /// return in microseconds. In practice it is a lookup in a pre-built table.
    /// </remarks>
    public delegate bool BindingProbe(ushort virtualKey, KeyModifiers modifiers, bool isKeyDown);

    private readonly KeyEvent[] _ring;
    private readonly int _mask;
    private long _write;
    private long _read;
    private long _dropped;

    private UnhookWindowsHookExSafeHandle? _hook;
    private BindingProbe? _probe;
    private volatile bool _suspended;
    private bool _disposed;

    public KeyboardSource(int capacityPowerOfTwo = 1024)
    {
        if (capacityPowerOfTwo <= 0 || (capacityPowerOfTwo & (capacityPowerOfTwo - 1)) != 0)
            throw new ArgumentException("Capacity must be a power of two.", nameof(capacityPowerOfTwo));

        _ring = new KeyEvent[capacityPowerOfTwo];
        _mask = capacityPowerOfTwo - 1;
    }

    /// <summary>Keystrokes dropped because the consumer fell behind.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>
    /// When true the hook passes everything through untouched.
    /// </summary>
    /// <remarks>
    /// Backs <c>wm-toggle-pause</c>. Suspending rather than unhooking matters: the
    /// binding that resumes has to keep working, and re-installing a hook later can
    /// fail if the desktop has changed.
    /// </remarks>
    public bool Suspended
    {
        get => _suspended;
        set => _suspended = value;
    }

    /// <summary>
    /// Installs the hook. Must be called on a thread that runs a message pump.
    /// </summary>
    /// <param name="probe">Decides which keystrokes are bound and get swallowed.</param>
    public unsafe void Start(BindingProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (Interlocked.CompareExchange(ref s_instance, this, null) is not null)
            throw new InvalidOperationException("A KeyboardSource is already active in this process.");

        _probe = probe;

        _hook = PInvoke.SetWindowsHookEx(
            WINDOWS_HOOK_ID.WH_KEYBOARD_LL, &Callback, (SafeHandle?)null, 0);

        if (_hook.IsInvalid)
        {
            Interlocked.CompareExchange(ref s_instance, null, this);
            throw new InvalidOperationException(
                $"SetWindowsHookEx(WH_KEYBOARD_LL) failed with error {Marshal.GetLastWin32Error()}.");
        }
    }

    /// <summary>Removes up to <paramref name="max"/> queued keystrokes.</summary>
    public int Drain(Span<KeyEvent> destination, int max)
    {
        int limit = Math.Min(max, destination.Length);
        int count = 0;

        long read = Volatile.Read(ref _read);
        long write = Volatile.Read(ref _write);

        while (count < limit && read < write)
        {
            destination[count++] = _ring[(int)(read & _mask)];
            read++;
        }

        Volatile.Write(ref _read, read);
        return count;
    }

    /// <summary>
    /// THE HOT PATH. Must not allocate, lock, or call anything that can block.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe LRESULT Callback(int nCode, WPARAM wParam, LPARAM lParam)
    {
        if (nCode < 0) return PInvoke.CallNextHookEx(HHOOK.Null, nCode, wParam, lParam);

        KeyboardSource? source = s_instance;
        if (source is null || source._suspended)
            return PInvoke.CallNextHookEx(HHOOK.Null, nCode, wParam, lParam);

        var info = (KBDLLHOOKSTRUCT*)lParam.Value;

        uint message = (uint)wParam.Value;
        bool isKeyDown = message is PInvoke.WM_KEYDOWN or PInvoke.WM_SYSKEYDOWN;

        // GetKeyState is a cheap read of thread-local state, not a system call that
        // can block, so it is safe here. GetAsyncKeyState would not be: it can
        // contend with the raw input thread.
        KeyModifiers modifiers = KeyModifiers.None;
        if ((PInvoke.GetKeyState((int)VIRTUAL_KEY.VK_MENU) & 0x8000) != 0) modifiers |= KeyModifiers.Alt;
        if ((PInvoke.GetKeyState((int)VIRTUAL_KEY.VK_CONTROL) & 0x8000) != 0) modifiers |= KeyModifiers.Control;
        if ((PInvoke.GetKeyState((int)VIRTUAL_KEY.VK_SHIFT) & 0x8000) != 0) modifiers |= KeyModifiers.Shift;
        if ((PInvoke.GetKeyState((int)VIRTUAL_KEY.VK_LWIN) & 0x8000) != 0 ||
            (PInvoke.GetKeyState((int)VIRTUAL_KEY.VK_RWIN) & 0x8000) != 0) modifiers |= KeyModifiers.Windows;

        var key = new KeyEvent(
            (ushort)info->vkCode,
            modifiers,
            isKeyDown,
            (info->flags & KBDLLHOOKSTRUCT_FLAGS.LLKHF_INJECTED) != 0);

        bool bound = source._probe is { } probe && probe(key.VirtualKey, modifiers, isKeyDown);

        if (bound)
        {
            source.Enqueue(in key);

            // Swallow the keystroke so the focused application never sees it.
            // Both edges must be swallowed: passing the key-up through leaves
            // applications with a stuck modifier.
            return new LRESULT(1);
        }

        return PInvoke.CallNextHookEx(HHOOK.Null, nCode, wParam, lParam);
    }

    /// <summary>Single-producer enqueue. Wait-free and allocation-free.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Enqueue(in KeyEvent key)
    {
        long write = _write;
        long read = Volatile.Read(ref _read);

        if (write - read >= _ring.Length)
        {
            // Full. Drop rather than block: blocking here would hit the 300 ms
            // unhook threshold and disable every keybinding.
            Interlocked.Increment(ref _dropped);
            return;
        }

        _ring[(int)(write & _mask)] = key;
        Volatile.Write(ref _write, write + 1);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _hook?.Dispose();
        _hook = null;
        _probe = null;

        Interlocked.CompareExchange(ref s_instance, null, this);
    }
}
