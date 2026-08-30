using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Shubbak.Native;

/// <summary>What kind of window change a <see cref="WinEventNotification"/> reports.</summary>
public enum WinEventKind
{
    Created,
    Destroyed,
    Shown,
    Hidden,
    TitleChanged,
    Cloaked,
    Uncloaked,
    Foreground,
    MinimiseStart,
    MinimiseEnd,
    MoveSizeStart,
    MoveSizeEnd,
}

/// <summary>A single window change reported by the operating system.</summary>
public readonly record struct WinEventNotification(WinEventKind Kind, nint Handle);

/// <summary>
/// Subscribes to system-wide window events.
/// </summary>
/// <remarks>
/// <para>
/// The hook callback runs on the message-pump thread and must be cheap. S4 measured
/// the raw stream at ~130 events/second during ordinary use, of which only 47% are
/// <c>OBJID_WINDOW</c>; <c>EVENT_OBJECT_NAMECHANGE</c> in particular is <b>93%</b>
/// child-object noise. So the object-id test is the first statement in the callback,
/// before any string or handle work
/// (docs/adr/0001-language-choice.md, constraint 8).
/// </para>
/// <para>
/// Surviving events are queued rather than dispatched inline, so that a slow
/// consumer can never stall the hook.
/// </para>
/// </remarks>
public sealed class WinEventSource : IDisposable
{
    private static WinEventSource? s_instance;

    private readonly List<UnhookWinEventSafeHandle> _hooks = [];
    private readonly Queue<WinEventNotification> _queue = new();
    private readonly Lock _gate = new();
    private bool _disposed;

    private static readonly (uint Id, WinEventKind Kind)[] Subscriptions =
    [
        (PInvoke.EVENT_OBJECT_CREATE, WinEventKind.Created),
        (PInvoke.EVENT_OBJECT_DESTROY, WinEventKind.Destroyed),
        (PInvoke.EVENT_OBJECT_SHOW, WinEventKind.Shown),
        (PInvoke.EVENT_OBJECT_HIDE, WinEventKind.Hidden),
        (PInvoke.EVENT_OBJECT_NAMECHANGE, WinEventKind.TitleChanged),

        // EVENT_OBJECT_LOCATIONCHANGE is deliberately absent.
        //
        // It was subscribed and then discarded by an empty case, which is not the same
        // as being free. WINEVENT_SKIPOWNPROCESS does not help: it skips windows owned
        // by this process, and every managed window belongs to another one. So every
        // DeferWindowPos the animation path issued came straight back as a callback,
        // and callbacks arrive through this thread's message queue, which is exactly
        // what the pump waits on with QS_ALLINPUT.
        //
        // The loop therefore paced itself against its own output: commit a frame, be
        // woken by the echo of that frame, commit another. The 7 ms frame interval was
        // a ceiling that was never reached, and the only thing limiting the rate was
        // how long EndDeferWindowPos took - which is the definition of a spin. A
        // dragged window produced 122 of these a second on its own, each waking a pump
        // that would throw it away.
        //
        // Nothing downstream wants it. MoveSizeEnd is the event that carries intent.
        //
        // What this costs, and where that is paid: a window that moves *itself* -
        // a browser restoring its remembered geometry a moment after it opens, or
        // taking itself full-screen to play a video - announces it through this event
        // and no other, so Shubbak cannot be told about it. It is noticed by looking
        // instead, in three places: WindowCommitter's skip check, the daemon's settle
        // check for newly adopted windows, and the daemon's full-screen watch. The
        // first two use PlacementDrift, which asks the question coarsely enough that
        // an application rounding its own size cannot start the fight this removal was
        // avoiding; the third uses NativeFullscreen, which asks a different question
        // and can afford to be exact.
        //
        // For the record, since this comment used to claim otherwise: the two obvious
        // comparisons do not agree with each other. komorebi hooks the whole event
        // range and then drops this one on the floor - window_manager_event.rs maps it
        // to None - and carries the consequences, several of which are open issues
        // about browser video refusing to go full-screen. GlazeWM subscribes to it and
        // builds its entire full-screen and self-maximise handling on top of it; it
        // ignores the event only for an ordinary tiling window that moved itself,
        // which is the same conclusion reached here by a different route.
        //
        // GlazeWM can afford the subscription because of two differences that are not
        // available cheaply here. Its hook runs on a dispatcher thread and forwards
        // through a channel, so the echo does not wake the loop that produced it; and
        // it has no animation engine, so a layout is one commit rather than one commit
        // per frame. Both are exactly what turned the echo into a spin above.
        (PInvoke.EVENT_OBJECT_CLOAKED, WinEventKind.Cloaked),
        (PInvoke.EVENT_OBJECT_UNCLOAKED, WinEventKind.Uncloaked),
        (PInvoke.EVENT_SYSTEM_FOREGROUND, WinEventKind.Foreground),
        (PInvoke.EVENT_SYSTEM_MINIMIZESTART, WinEventKind.MinimiseStart),
        (PInvoke.EVENT_SYSTEM_MINIMIZEEND, WinEventKind.MinimiseEnd),
        (PInvoke.EVENT_SYSTEM_MOVESIZESTART, WinEventKind.MoveSizeStart),
        (PInvoke.EVENT_SYSTEM_MOVESIZEEND, WinEventKind.MoveSizeEnd),
    ];

    /// <summary>
    /// Installs the hooks. Must be called on a thread that runs a message pump:
    /// <c>WINEVENT_OUTOFCONTEXT</c> callbacks are delivered through its queue.
    /// </summary>
    public unsafe void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (Interlocked.CompareExchange(ref s_instance, this, null) is not null)
            throw new InvalidOperationException("A WinEventSource is already active in this process.");

        foreach ((uint id, _) in Subscriptions)
        {
            UnhookWinEventSafeHandle hook = PInvoke.SetWinEventHook(
                id, id, null, &Callback, 0, 0,
                PInvoke.WINEVENT_OUTOFCONTEXT | PInvoke.WINEVENT_SKIPOWNPROCESS);

            if (!hook.IsInvalid) _hooks.Add(hook);
        }

        if (_hooks.Count == 0)
            throw new InvalidOperationException("Failed to install any WinEvent hook.");
    }

    /// <summary>Removes up to <paramref name="max"/> queued notifications.</summary>
    public int Drain(Span<WinEventNotification> destination, int max)
    {
        int count = 0;
        int limit = Math.Min(max, destination.Length);

        lock (_gate)
        {
            while (count < limit && _queue.Count > 0)
                destination[count++] = _queue.Dequeue();
        }

        return count;
    }

    /// <summary>How many notifications are waiting.</summary>
    public int PendingCount
    {
        get { lock (_gate) return _queue.Count; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe void Callback(
        HWINEVENTHOOK hook, uint eventId, HWND hwnd, int idObject, int idChild,
        uint threadId, uint eventTime)
    {
        try
        {
            // FIRST. S4 showed NAMECHANGE is 93% child-object noise; doing anything
            // before this test would multiply the cost of the whole stream.
            if (idObject != (int)OBJECT_IDENTIFIER.OBJID_WINDOW || idChild != 0) return;
            if (hwnd.IsNull) return;

            WinEventSource? source = s_instance;
            if (source is null) return;

            WinEventKind? kind = MapKind(eventId);
            if (kind is null) return;

            source.Enqueue(new WinEventNotification(kind.Value, (nint)hwnd.Value));
        }
        catch
        {
            // An exception escaping an UnmanagedCallersOnly callback tears down the
            // process (docs/adr/0001-language-choice.md, constraint 4).
        }
    }

    private static WinEventKind? MapKind(uint eventId)
    {
        foreach ((uint id, WinEventKind kind) in Subscriptions)
            if (id == eventId) return kind;

        return null;
    }

    /// <summary>Signalled after anything is queued, so a waiting pump wakes at once.</summary>
    /// <remarks>
    /// The consumer waits rather than polls, so without this an event sits in the
    /// queue until some unrelated timeout expires.
    /// </remarks>
    public Action? WorkQueued { get; set; }

    private void Enqueue(WinEventNotification notification)
    {
        lock (_gate)
        {
            // Bound the queue. A burst - dragging a window generates ~122
            // LOCATIONCHANGE per second on its own - must never grow without limit
            // if the consumer stalls. Dropping the oldest is correct here because
            // these events are level-triggered: the newest reflects reality.
            if (_queue.Count >= 4096) _queue.Dequeue();

            _queue.Enqueue(notification);
        }

        WorkQueued?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (UnhookWinEventSafeHandle hook in _hooks) hook.Dispose();
        _hooks.Clear();

        Interlocked.CompareExchange(ref s_instance, null, this);
    }
}
