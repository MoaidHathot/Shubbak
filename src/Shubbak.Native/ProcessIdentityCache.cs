using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;

namespace Shubbak.Native;

/// <summary>
/// Process identity - executable path and integrity level - cached by process id.
/// </summary>
/// <remarks>
/// <para>
/// Both facts cost an <c>OpenProcess</c>, and both are properties of a
/// <em>process</em> rather than of a window. <see cref="WindowFilter.Evaluate"/> was
/// asking for them per window, so a browser with twenty windows paid for twenty
/// identical pairs of process handles - once on every arrival, and again for every
/// window on every full enumeration.
/// </para>
/// <para>
/// The interesting part is why the cache cannot go stale. Windows reuses process
/// ids, so a cache keyed by id is normally a correctness hazard: the entry survives
/// the process and then answers for whatever inherits the number. Validating that
/// needs the process creation time, which needs a handle - and by then the saving is
/// gone.
/// </para>
/// <para>
/// So the entry <em>keeps</em> the handle. Windows will not reuse a process id while
/// any handle to that process remains open, which turns the hazard into an
/// invariant: while the entry exists, the id still names the process the entry
/// describes. A cache hit therefore costs no system call at all, which is what makes
/// this safe to read from the message loop.
/// </para>
/// <para>
/// <c>PROCESS_QUERY_LIMITED_INFORMATION</c> is the right to pin with, and the choice
/// matters. It is deliberately granted across integrity levels, so an unelevated
/// Shubbak can still pin - and still read the path of - an elevated process. The
/// unlimited right is what <see cref="Win32Window.IsElevated"/> uses as its
/// discriminator, and that is a separate question asked once per process here.
/// </para>
/// <para>
/// Measured rather than assumed. Running <see cref="WindowFilter.Evaluate"/> over
/// every top-level window on a real desktop - 542 windows, of which 8 got far enough
/// down the filter to need a process handle - took 6.10 ms cold and 0.10 ms warm.
/// The warm pass still does all 542 windows' worth of style, class and title checks,
/// so very nearly the whole of that 6 ms was process identity, and it is now paid
/// once per process instead of once per window.
/// </para>
/// </remarks>
internal static class ProcessIdentityCache
{
    /// <summary>What is remembered about one process.</summary>
    private sealed class Entry
    {
        /// <summary>
        /// Held for its side effect, not its value: an open handle is what stops the
        /// process id being reused, and therefore what stops this entry going stale.
        /// </summary>
        public required SafeFileHandle Pin { get; init; }

        public required string? Path { get; init; }

        public required bool IsElevated { get; init; }
    }

    /// <summary>
    /// The point at which the cache sweeps rather than grows.
    /// </summary>
    /// <remarks>
    /// An entry appears only when a process has had a top-level window looked at, so
    /// the live population is tens, not hundreds. Reaching this many means a long
    /// session that has opened and closed a great many applications, and almost all
    /// of them will be dead by then - which is exactly what the sweep collects.
    /// </remarks>
    private const int Capacity = 512;

    private static readonly Dictionary<uint, Entry> s_cache = [];
    private static readonly Lock s_gate = new();

    /// <summary>The executable path, or null when it cannot be read.</summary>
    public static string? PathOf(uint processId) => Resolve(processId)?.Path;

    /// <summary>Whether the process runs at a higher integrity level than this one.</summary>
    public static bool IsElevated(uint processId) => Resolve(processId)?.IsElevated ?? false;

    /// <summary>How many processes are currently remembered.</summary>
    public static int Count
    {
        get { lock (s_gate) return s_cache.Count; }
    }

    /// <summary>
    /// Drops every entry, releasing the handles that pin the process ids.
    /// </summary>
    /// <remarks>
    /// For tests, which must not see one case's processes from another, and for
    /// configuration reload, where the excluded-process list may have changed and
    /// every window is reconsidered anyway.
    /// </remarks>
    public static void Clear()
    {
        lock (s_gate)
        {
            foreach (Entry entry in s_cache.Values) entry.Pin.Dispose();
            s_cache.Clear();
        }
    }

    private static Entry? Resolve(uint processId)
    {
        if (processId == 0) return null;

        lock (s_gate)
        {
            if (s_cache.TryGetValue(processId, out Entry? hit)) return hit;
        }

        // Opened outside the lock. A full enumeration resolves a couple of hundred
        // windows on a pipe thread, and holding the gate across that many system
        // calls would stall the message loop behind it - which is the one thread
        // that must never wait on anything.
        //
        // The cost of that choice is that two threads can open the same process at
        // once. Whoever inserts second discards their handle and adopts the winner's
        // entry, so the pin stays unique and the duplicate work is a handle opened
        // and closed.
        Entry? built = Build(processId);
        if (built is null) return null;

        lock (s_gate)
        {
            if (s_cache.TryGetValue(processId, out Entry? raced))
            {
                built.Pin.Dispose();
                return raced;
            }

            if (s_cache.Count >= Capacity) Evict();

            s_cache[processId] = built;
            return built;
        }
    }

    private static Entry? Build(uint processId)
    {
        SafeFileHandle pin = PInvoke.OpenProcess_SafeHandle(
            PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);

        // Nothing to pin, so nothing may be remembered. A protected process refuses
        // this, and so does one that is already exiting. Both are rare, both are
        // cheap to ask again, and caching either without a handle would reintroduce
        // exactly the staleness this design exists to rule out.
        if (pin.IsInvalid)
        {
            pin.Dispose();
            return null;
        }

        return new Entry
        {
            Pin = pin,
            Path = ReadPath(pin),
            IsElevated = ReadIsElevated(processId),
        };
    }

    private static unsafe string? ReadPath(SafeFileHandle process)
    {
        Span<char> buffer = stackalloc char[512];
        uint size = (uint)buffer.Length;

        return PInvoke.QueryFullProcessImageName(
            process, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, buffer, ref size)
            ? new string(buffer[..(int)size])
            : null;
    }

    /// <summary>
    /// Whether the process runs at a higher integrity level than this one.
    /// </summary>
    /// <remarks>
    /// A second handle, deliberately. The discriminator is the <em>refusal</em> of
    /// <c>PROCESS_QUERY_INFORMATION</c>, which is not granted across integrity
    /// levels the way the limited right is. The full reasoning, including the two
    /// approaches that look like discriminators and are not, is on
    /// <see cref="Win32Window.IsElevated"/>.
    /// </remarks>
    private static bool ReadIsElevated(uint processId)
    {
        using SafeFileHandle probe = PInvoke.OpenProcess_SafeHandle(
            PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_INFORMATION, false, processId);

        return probe.IsInvalid;
    }

    /// <summary>
    /// Makes room, called with the gate held.
    /// </summary>
    /// <remarks>
    /// Dead processes first, because they are what the capacity is actually full of
    /// and because dropping them costs nothing - the entry describes a process that
    /// no longer exists. Only their pins were keeping the ids reserved.
    /// <para>
    /// If that frees nothing the cache is cleared outright. Five hundred live
    /// window-owning processes is not a real desktop, so reaching this means an
    /// assumption here is wrong, and rebuilding lazily from an empty cache is
    /// correct however it happened. Evicting by age instead would be tidier and
    /// would hide it.
    /// </para>
    /// </remarks>
    private static void Evict()
    {
        List<uint> dead = [];

        foreach ((uint processId, Entry entry) in s_cache)
            if (HasExited(entry.Pin)) dead.Add(processId);

        foreach (uint processId in dead)
        {
            s_cache[processId].Pin.Dispose();
            s_cache.Remove(processId);
        }

        if (dead.Count > 0) return;

        foreach (Entry entry in s_cache.Values) entry.Pin.Dispose();
        s_cache.Clear();
    }

    /// <summary>Whether the process behind a pinned handle has exited.</summary>
    /// <remarks>
    /// A process handle signals on exit, so a zero-length wait answers this without
    /// blocking. <c>GetExitCodeProcess</c> would do too, but it reports
    /// <c>STILL_ACTIVE</c> as an exit code, so a process that genuinely exits with
    /// 259 is indistinguishable from a running one.
    /// </remarks>
    private static bool HasExited(SafeFileHandle process) =>
        PInvoke.WaitForSingleObject(process, 0) == WAIT_EVENT.WAIT_OBJECT_0;
}
