using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Shubbak.Native;

/// <summary>
/// Layout metadata built off the keyboard thread. The hook reads an immutable map;
/// a newly installed layout requests a refresh and conservatively protects AltGr
/// until it is known. No polling, layout loading, translation or allocation per key.
/// </summary>
public sealed class KeyboardLayouts
{
    private volatile Dictionary<nint, bool> _known = [];
    private nint _requested;

    public bool RefreshPending => Volatile.Read(ref _requested) != 0;

    public bool UsesAltGr(nint layout)
    {
        if (_known.TryGetValue(layout, out bool result)) return result;
        Interlocked.Exchange(ref _requested, layout);
        return true;
    }

    /// <summary>Called at startup and on demand by the consumer, never in the hook.</summary>
    public unsafe void Refresh()
    {
        nint requested = Interlocked.Exchange(ref _requested, 0);
        int count = PInvoke.GetKeyboardLayoutList(0, null);
        var layouts = new HKL[Math.Max(1, count)];
        fixed (HKL* pointer = layouts) count = PInvoke.GetKeyboardLayoutList(layouts.Length, pointer);
        Dictionary<nint, bool> previous = _known;
        Dictionary<nint, bool> next = [];
        for (int i = 0; i < count; i++) Add(layouts[i]);
        if (requested != 0 && !next.ContainsKey(requested)) Add(new HKL(requested));
        _known = next;

        void Add(HKL layout)
        {
            nint handle = (nint)layout.Value;
            next[handle] = previous.TryGetValue(handle, out bool cached) ? cached : DetectAltGr(handle);
        }
    }

    public static unsafe nint ForegroundLayout()
    {
        uint thread = PInvoke.GetWindowThreadProcessId(PInvoke.GetForegroundWindow(), null);
        return (nint)PInvoke.GetKeyboardLayout(thread).Value;
    }

    /// <summary>
    /// Does this layout map Ctrl+Alt to distinct text or a dead key? Bit 2 prevents
    /// ToUnicodeEx from consuming or installing dead-key state; bit 0 excludes
    /// Alt+numpad composition. Only the worker pays for this bounded scan.
    /// </summary>
    public static unsafe bool DetectAltGr(nint handle)
    {
        var layout = new HKL(handle);
        byte* state = stackalloc byte[256];
        new Span<byte>(state, 256).Clear();
        char* alternate = stackalloc char[8];
        char* ordinary = stackalloc char[8];
        for (int shift = 0; shift < 2; shift++)
        {
            state[0x10] = (byte)(shift * 0x80);
            for (uint key = 0x20; key < 0xFF; key++)
            {
                uint scan = PInvoke.MapVirtualKeyEx(key, MAP_VIRTUAL_KEY_TYPE.MAPVK_VK_TO_VSC, layout);
                state[0x11] = state[0x12] = state[0xA2] = state[0xA5] = 0x80;
                int count = PInvoke.ToUnicodeEx(key, scan, state, alternate, 8, 5, layout);
                if (count == 0 || (count > 0 && alternate[0] < ' ')) continue;
                state[0x11] = state[0x12] = state[0xA2] = state[0xA5] = 0;
                int plain = PInvoke.ToUnicodeEx(key, scan, state, ordinary, 8, 5, layout);
                if (count != plain || !new ReadOnlySpan<char>(alternate, Math.Min(Math.Abs(count), 8))
                    .SequenceEqual(new ReadOnlySpan<char>(ordinary, Math.Min(Math.Abs(plain), 8)))) return true;
            }
        }
        return false;
    }
}
