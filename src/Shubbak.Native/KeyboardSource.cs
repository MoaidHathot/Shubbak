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

    /// <summary>
    /// Virtual keys whose press was swallowed, indexed by code.
    /// </summary>
    /// <remarks>
    /// A fixed 256-entry array so the hot path neither allocates nor hashes. Written
    /// only from the hook thread, so no synchronisation is needed.
    /// </remarks>
    private readonly bool[] _swallowed = new bool[256];

    private UnhookWindowsHookExSafeHandle? _hook;
    private Thread? _thread;
    private uint _threadId;

    /// <summary>Name of the thread servicing the hook, or null when not started.</summary>
    /// <remarks>
    /// Exposed so a test can assert the hook is not sharing a thread with anything
    /// else. That is not an implementation detail: sharing it with the window
    /// manager's message loop made typing slow across the entire machine.
    /// </remarks>
    public string? ThreadName => _thread?.Name;

    /// <summary>Managed id of the thread servicing the hook, or zero.</summary>
    public int ThreadId => _thread?.ManagedThreadId ?? 0;

    /// <summary>Whether the hook is installed.</summary>
    public bool IsRunning => _thread is not null && !_disposed;
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
    /// Installs the hook on a dedicated thread and begins delivering keystrokes.
    /// </summary>
    /// <param name="probe">Decides which keystrokes are bound and get swallowed.</param>
    /// <remarks>
    /// <para>
    /// The thread is the point. A <c>WH_KEYBOARD_LL</c> callback runs on the thread
    /// that installed the hook, and that thread must be pumping messages for the
    /// keystroke to be delivered at all. Windows gives it
    /// <c>LowLevelHooksTimeout</c> - 300 ms by default - and until it answers,
    /// <b>the keystroke has not reached the focused application</b>.
    /// </para>
    /// <para>
    /// Installing it on the window manager's own message loop therefore puts every
    /// keystroke the user types, in every application, behind whatever that loop is
    /// doing: applying a layout, talking to the shell over COM, writing a log line.
    /// Typing went sluggish system-wide, and nothing about the symptom pointed at a
    /// window manager.
    /// </para>
    /// <para>
    /// Here the hook gets a thread that does nothing else. The ring buffer was
    /// already the handoff to the consumer, so the producer simply moved.
    /// </para>
    /// </remarks>
    public void Start(BindingProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (Interlocked.CompareExchange(ref s_instance, this, null) is not null)
            throw new InvalidOperationException("A KeyboardSource is already active in this process.");

        _probe = probe;

        using var ready = new ManualResetEventSlim(false);
        Exception? failure = null;

        _thread = new Thread(() => PumpUntilStopped(ready, ref failure))
        {
            // Named so it is identifiable in a debugger or a hang dump - the first
            // question about input latency is which thread was busy.
            Name = "Shubbak keyboard hook",
            IsBackground = true,
        };

        // Above normal, because a late keystroke is a keystroke the focused
        // application has not received yet. Not time-critical: starving the rest of
        // the system to service a hook would be its own bug.
        _thread.Start();
        _thread.Priority = ThreadPriority.AboveNormal;

        ready.Wait();

        if (failure is not null)
        {
            Interlocked.CompareExchange(ref s_instance, null, this);
            throw failure;
        }
    }

    private unsafe void PumpUntilStopped(ManualResetEventSlim ready, ref Exception? failure)
    {
        try
        {
            _hook = PInvoke.SetWindowsHookEx(
                WINDOWS_HOOK_ID.WH_KEYBOARD_LL, &Callback, (SafeHandle?)null, 0);

            if (_hook.IsInvalid)
            {
                failure = new InvalidOperationException(
                    $"SetWindowsHookEx(WH_KEYBOARD_LL) failed with error {Marshal.GetLastWin32Error()}.");

                return;
            }

            _threadId = PInvoke.GetCurrentThreadId();
        }
        catch (Exception ex)
        {
            failure = ex;
            return;
        }
        finally
        {
            ready.Set();
        }

        // A plain blocking pump. This thread exists solely to be available the
        // instant a keystroke arrives, so it must never do anything else.
        while (PInvoke.GetMessage(out MSG message, default, 0, 0))
        {
            if (message.message == PInvoke.WM_QUIT) break;

            PInvoke.TranslateMessage(in message);
            PInvoke.DispatchMessage(in message);
        }

        _hook?.Dispose();
        _hook = null;
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
        var virtualKey = (ushort)info->vkCode;

        KeyModifiers modifiers = ReadModifiers();

        // A key-up must be swallowed if its key-down was, or the application is left
        // believing the key is still held. Tracked rather than re-evaluated, because
        // the modifiers may have been released between the two edges - which would
        // make the combination look unbound on the way up.
        if (!isKeyDown)
        {
            if (source.WasSwallowed(virtualKey))
            {
                source.ClearSwallowed(virtualKey);
                return new LRESULT(1);
            }

            return PInvoke.CallNextHookEx(HHOOK.Null, nCode, wParam, lParam);
        }

        var key = new KeyEvent(
            virtualKey,
            modifiers,
            isKeyDown,
            (info->flags & KBDLLHOOKSTRUCT_FLAGS.LLKHF_INJECTED) != 0);

        bool bound = source._probe is { } probe && probe(virtualKey, modifiers, true);

        if (bound)
        {
            source.Enqueue(in key);
            source.MarkSwallowed(virtualKey);

            // Swallow, so the focused application never sees the keystroke.
            return new LRESULT(1);
        }

        return PInvoke.CallNextHookEx(HHOOK.Null, nCode, wParam, lParam);
    }

    /// <summary>
    /// Reads which modifiers are physically held.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>GetAsyncKeyState</c>, not <c>GetKeyState</c>. The latter reports the state
    /// as of the last message the <i>calling thread</i> retrieved, and a low-level
    /// hook thread never retrieves the keystrokes it is inspecting - they are
    /// delivered to whichever application has focus. Its answer is therefore stale
    /// whenever focus has recently moved.
    /// </para>
    /// <para>
    /// The symptom is precise and maddening: after interacting with something
    /// Shubbak does not manage - the Start menu, a system flyout - the first
    /// modified keystroke reports no modifiers, so it matches no binding and is
    /// passed through. Press it a second time and it works.
    /// </para>
    /// <para>
    /// Still allocation-free and still microseconds: these are reads of a state
    /// table, not system calls that can block. The left and right keys are read
    /// separately rather than through the merged <c>VK_MENU</c> and
    /// <c>VK_CONTROL</c>, because telling the sides apart is the only way to
    /// recognise AltGr - see <see cref="DeriveModifiers"/>.
    /// </para>
    /// </remarks>
    private static KeyModifiers ReadModifiers() => DeriveModifiers(
        leftAlt: IsHeld(VIRTUAL_KEY.VK_LMENU),
        rightAlt: IsHeld(VIRTUAL_KEY.VK_RMENU),
        leftControl: IsHeld(VIRTUAL_KEY.VK_LCONTROL),
        rightControl: IsHeld(VIRTUAL_KEY.VK_RCONTROL),
        shift: IsHeld(VIRTUAL_KEY.VK_SHIFT),
        windows: IsHeld(VIRTUAL_KEY.VK_LWIN) || IsHeld(VIRTUAL_KEY.VK_RWIN));

    /// <summary>
    /// Turns the physical key states into the modifier set a binding is matched on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separated from reading the keyboard so the one decision in it can be tested.
    /// That decision is AltGr.
    /// </para>
    /// <para>
    /// On layouts that have AltGr - German, French, Polish, Spanish, the Nordics,
    /// Arabic 102, and many more - AltGr is not a key. Pressing it makes the layout
    /// emit <b>left Control followed by right Alt</b>. Read through
    /// <c>VK_CONTROL</c> and <c>VK_MENU</c>, which do not say which side was pressed,
    /// that is indistinguishable from someone holding Control and Alt.
    /// </para>
    /// <para>
    /// So AltGr+X reported <c>Alt | Control</c>. Matching is exact - the table is
    /// keyed on the packed modifier set - so what it collided with was every binding
    /// written <c>alt+ctrl+X</c>. The hook swallows what it matches, so on those
    /// layouts the character never arrived: AltGr+Q is @ on German, AltGr+A is ą on
    /// Polish, AltGr+2 is @ on Spanish. The key stopped producing anything, in every
    /// application on the machine, with nothing about the symptom pointing at a
    /// window manager.
    /// </para>
    /// <para>
    /// The shipped example config contained two such bindings and the author's own
    /// three, so this was not hypothetical for anyone who started from either and
    /// then switched layout.
    /// </para>
    /// <para>
    /// What it costs is that left Control with right Alt can no longer be typed as
    /// Control+Alt. On a layout with AltGr that combination is not typeable in the
    /// first place - it is exactly what the layout emits - and on one without it,
    /// left Alt or right Control reaches the same binding. This is the side to err
    /// on: the failure it removes breaks typing everywhere, and the one it introduces
    /// leaves a keybinding with three other ways to be pressed.
    /// </para>
    /// <para>
    /// The injected flag is deliberately not used for this, though the hook records
    /// it. <c>LLKHF_INJECTED</c> marks events synthesised through <c>SendInput</c>;
    /// the AltGr Control comes from layout processing beneath that and does not carry
    /// it, so a check on it would silently do nothing.
    /// </para>
    /// <para>
    /// Public, and a plain function of six booleans, so the AltGr rule can be proved
    /// by a test. It cannot be exercised on a US keyboard, and getting it wrong stops
    /// characters appearing across the whole machine, so "we reasoned about it" is not
    /// good enough.
    /// </para>
    /// </remarks>
    public static KeyModifiers DeriveModifiers(
        bool leftAlt,
        bool rightAlt,
        bool leftControl,
        bool rightControl,
        bool shift,
        bool windows)
    {
        bool altGr = leftControl && rightAlt;

        // Whatever is held over and above AltGr still counts, so a user pressing
        // AltGr and left Alt together gets Alt.
        bool alt = altGr ? leftAlt : leftAlt || rightAlt;
        bool control = altGr ? rightControl : leftControl || rightControl;

        KeyModifiers modifiers = KeyModifiers.None;

        if (alt) modifiers |= KeyModifiers.Alt;
        if (control) modifiers |= KeyModifiers.Control;
        if (shift) modifiers |= KeyModifiers.Shift;
        if (windows) modifiers |= KeyModifiers.Windows;

        return modifiers;
    }

    private static bool IsHeld(VIRTUAL_KEY key) =>
        (PInvoke.GetAsyncKeyState((int)key) & 0x8000) != 0;

    /// <summary>Records that a key's press was swallowed, so its release is too.</summary>
    private void MarkSwallowed(ushort virtualKey) => _swallowed[virtualKey] = true;

    private bool WasSwallowed(ushort virtualKey) => _swallowed[virtualKey];

    private void ClearSwallowed(ushort virtualKey) => _swallowed[virtualKey] = false;

    /// <summary>Signalled after a keystroke is queued, so a waiting pump wakes at once.</summary>
    /// <remarks>
    /// Runs inside the hook callback, so it must be as cheap as everything else there:
    /// one SetEvent, no allocation, about a microsecond. That is what stops a keystroke
    /// waiting on a timer before anything looks at it.
    /// </remarks>
    public Action? WorkQueued { get; set; }

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

        WorkQueued?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // The hook belongs to the pump thread and must be removed there, so the
        // thread is asked to leave rather than having the handle pulled from under it.
        if (_threadId != 0)
            PInvoke.PostThreadMessage(_threadId, PInvoke.WM_QUIT, default, default);

        // Bounded: a pump thread that will not leave must not stop the window manager
        // from exiting. The process is going away regardless.
        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
        _threadId = 0;

        _hook?.Dispose();
        _hook = null;
        _probe = null;

        Interlocked.CompareExchange(ref s_instance, null, this);
    }
}
