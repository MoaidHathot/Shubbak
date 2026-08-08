# Analysis 3

Findings from a review of the tree at `d551b04`, none of them acted on yet.
Companion to [analysis.md](analysis.md), written at `fcd7839`, and
[analysis2.md](analysis2.md), written at `08ec686`, and to
[further-improvements.md](further-improvements.md). Written for the same reason
as all three: so the reasoning survives the conversation it came from.

> **Since written.** The paragraph above describes the document as it stood at
> `d551b04`. Thirty-three commits later — `d551b04..147187f` — tiers M, 0 and 1
> have shipped, tier 2 in part, and six of the claims below did not survive being
> measured. What shipped is marked in the tier table near the end; what was wrong
> is in [What measurement disproved](#what-measurement-disproved), immediately
> below.
>
> The findings themselves are left exactly as written. Where one was right it
> still reads as it did; where it was wrong the correction lives in one place
> rather than being edited into the body. That is the same treatment analysis 3
> gave analysis 2, and it is the point of keeping these documents at all — a
> review that quietly rewrites its own mistakes teaches nothing about how the
> mistakes were made.

The review was asked one question — **where is the performance** — with the two
paths named: keystroke handling, and animation fluidity. It is ordered by
leverage rather than by file, so the largest finding is first and the smallest
last. Everything cites a file and a line. The last section says which claims
were verified by reading and which were reasoned and still need measuring.

The short version, because the document is long: the hook is not the problem and
has not been the problem for two reviews. The frame clock is a timeout rather
than a clock, the frame itself asks every application to relayout twenty times a
second when it needs to ask once, and the interpolated-string handler built
specifically to keep allocation off the daemon thread is switched off by its own
threshold at the default log level.

## What analysis 2 retired

Nine commits sit between `08ec686` and here, and between them they closed almost
everything [analysis2.md](analysis2.md) opened with. Recorded so that document
can be read without re-litigating settled ground.

| Finding | Now |
|---|---|
| The animation loop paced against its own `LOCATIONCHANGE` echo | fixed — the subscription is gone, and its absence is documented in place at `WinEventSource.cs:64-80` |
| An unclamped delta collapsing an animation into one frame | fixed — `ClampAnimationStep`, `WmDaemon.cs:274-306` |
| The 1 ms timer raised for `_layoutDirty` rather than for motion | fixed — `WmDaemon.cs:251-252` keys on `IsAnimating` alone |
| `Placement.Raise` never honoured for an animated window | fixed — raised before the motion starts, `WmDaemon.cs:1781` |
| `LayoutChange` and `WorkspaceSwitch` never constructed | fixed — `WmEventGeometry.cs:94-120`, consumed at `WmDaemon.cs:2535` |
| `cubic-bezier(...)` unwritable in a config | fixed — `Easing.cs:82-119` |
| `_swallowed` never bulk-reset | fixed — `ForgetSwallowed`, `KeyboardSource.cs:456`, called at `WmDaemon.cs:2481` |
| AltGr swallowed on European layouts | fixed — `DeriveModifiers`, `KeyboardSource.cs:390-413`, and it is a pure function with tests |
| Every auto-repeat executing the command | fixed — `KeyEvent.IsRepeat`, `WmDaemon.cs:394`, and `repeat=` is now a real property |
| `wm-toggle-pause` doing nothing | fixed — `WmDaemon.cs:327` and `:522`, with keybindings deliberately still live |
| A reload losing the binding mode; an undeclared mode reported as active | fixed — `WmDaemon.cs:2472-2495` |
| Numpad, media and browser keys unbindable | fixed — `KeyParser.cs:74-131` |

Two are unchanged and are restated at the end, because a finding that survives a
round of fixes is worth more than one that has only been made once.

## What analysis 2 got wrong

It is worth recording the one claim that does not hold, so the next review does
not spend an afternoon on it.

The implied focus echo — `ApplyLayout` calls `FocusIfDisplayed`, which sets the
foreground, which produces `EVENT_SYSTEM_FOREGROUND`, which publishes
`WindowFocused`, which `AffectsGeometry` and therefore dirties the layout again —
**does not happen**. The foreground handler guards on identity first:

```csharp
// WmDaemon.cs:597-608
case WinEventKind.Foreground:
    if (_windows.TryGet(handle, out WindowNode? focused))
    {
        if (!ReferenceEquals(_wm.FocusedWindow, focused))
        {
            Publish(_wm.FocusWindow(focused));
            ...
        }
    }
```

By the time `FocusIfDisplayed` (`:1846-1853`) runs, `_wm.FocusedWindow` is
already the node it is about to focus, so the echo publishes nothing and the
layout is not re-dirtied. `FocusIfDisplayed` additionally checks the foreground
before touching it (`:1850`), so in the common case it does not even fire.

## What measurement disproved

Added after the work, in the same spirit as the section above. Six claims here
did not hold once tier M's instrumentation existed to check them against. Four
were wrong about the cause while being right that something was wrong; two were
wrong about the size of the thing.

The pattern is worth naming, because it is the same one each time: every claim
below was reached by reading the code and reasoning forwards, and reasoning
forwards from correct code gets the mechanism right and the magnitude wrong. The
last section of this document says as much about itself, and was right to.

| Claim | What measurement showed |
|---|---|
| `Tick` should consult `Options.Enabled`; `Clear()` wired (`:995-1001`, tier 0) | The diagnosis was right and the prescription wrong. `Tick` still does not read `Enabled`, deliberately: a track stopped mid-flight leaves its window between two rectangles with nothing to finish the journey. The fix was `Clear()` at the two places the answer changes — config reload (`WmDaemon.cs:3176`) and the system preference (`:2788`). |
| `SolveForX` returns a bad answer because it throws away four Newton iterations and bisects from `[0, 1]` (`:484-496`, `:1103-1104`) | Not the operative cause. Bisection is entered legitimately and the discarded estimate costs accuracy it was not going to keep. What actually produced the error was the iteration count: **twelve** passes bound the answer at 2⁻¹² = 2.4e-4, against an epsilon of 1e-6 it therefore could never reach. The fix was 12 → 20 iterations, not returning the Newton result. |
| `EaseOut` converges early, so "the default configuration is probably not hitting the bug"; `EaseOutExpo` and `EaseOutBack` are the candidates (`:504-511`, `:1146-1149`) | **Exactly backwards**, and this is the one worth reading twice. x′(0) = 3·x₁, and ease-out is (0, 0, 0.58, 1) — its slope at the start is exactly zero, so Newton bails on the first iteration and it reaches bisection on *every* solve near the start of its travel. The most-used curve in the project was the worst affected, at 2.707e-4; it is now 1.677e-6. Working one curve through by hand at `t = 0.5` sampled the one region where the claim held. |
| `volatile` on `BindingTable`'s four fields "costs nothing and closes it" (`:896-899`) | It would not have closed it. Four volatile fields are four independent writes, and the property that matters is consistency *between* them, which no amount of per-field volatility provides. There was also a real bug sitting behind the theoretical one: reloading while a non-pass-through mode was active briefly resolved keystrokes against the defaults, which for a `pause` mode is the behaviour it exists to prevent. One immutable snapshot behind one volatile reference makes a reload a single publication. |
| Publish ordered above the layout pass by leverage (`:645` before `:696`) | Comparable, not dominant. At `info`: drain p99 6008 B, of which publish p99 5584 B, against layout p99 5752 B. Both are dwarfed by something this document identified but never sized — the log level. At `debug` the ring sits at Trace and drain p99 is **51,040 B**, twenty-seven times the 1,880 B at `info`. The ordering error came from comparing a per-call percentile against a per-tick one. |
| Tier 3's premise, "removes the GC coupling to the hook thread" (`:1047`) | There is little coupling left to remove. Over 8 h 20 m: gen0 **2**, gen1 2, gen2 2, and frame p99 **0 B** across 839 frames. The allocation that remains is on the drain and layout paths and already sits below the level at which it produces collections. Tier 3 is not wrong, but it is optimising against a cost that measurement cannot find. |

---

## The frame clock is not a clock

This is the largest finding in the review, and unlike analysis 2's it is not a
one-line fix. It is three separate decisions, each defensible on its own, which
together mean the animation path has no idea when a frame is due.

### Seven milliseconds, chosen once, for every panel

```csharp
// WmDaemon.cs:259-260
/// <summary>Roughly 144 Hz, the rate ADR 0001 gates the animation path on.</summary>
private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(7);
```

There is no refresh-rate query anywhere in `src`. `MonitorInfo`
(`MonitorSource.cs:21-26`) carries `DeviceId`, `Bounds`, `WorkArea`, `Dpi` and
`IsPrimary`, and no refresh rate. `EnumDisplaySettings` and `GetDeviceCaps` are
absent from `src/Shubbak.Native/NativeMethods.txt`;
`DwmGetCompositionTimingInfo` appears only in the spike
(`spikes/Shubbak.Spike/S2AnimationTiming.cs:315`, labelled "for phase-lock
reference"), where it was measured and then not carried into production.

On a 60 Hz panel the loop therefore attempts roughly 2.4 complete
`BeginDeferWindowPos` / `DeferWindowPos` × N / `EndDeferWindowPos` transactions
for every frame the user can possibly see. ADR 0001 measured Win32 at 94.6% of
frame time, so what is being discarded is not the cheap part. On a 240 Hz panel
the same constant delivers 60% of the frames the panel would take.

Sixty hertz is not an edge case. It is what almost every laptop panel, every
office monitor and every television does, and it is what a 144 Hz panel falls
back to on battery.

### A millisecond timeout is not a millisecond

The wait is a timeout on a message wait:

```csharp
// MessageLoop.cs:134-151
private unsafe void WaitForWork(TimeSpan requested, int defaultMs)
{
    uint timeout =
        requested == Timeout.InfiniteTimeSpan
            ? 0xFFFFFFFFu
            : (uint)Math.Clamp((int)requested.TotalMilliseconds, 0, defaultMs * 1000);

    if (timeout == 0) return;

    HANDLE handle = (HANDLE)_wake.SafeWaitHandle.DangerousGetHandle();

    PInvoke.MsgWaitForMultipleObjectsEx(
        1, &handle, timeout,
        QUEUE_STATUS_FLAGS.QS_ALLINPUT,
        MSG_WAIT_FOR_MULTIPLE_OBJECTS_EX_FLAGS.MWMO_INPUTAVAILABLE);
}
```

Choosing `MsgWaitForMultipleObjectsEx` over a sleep was right, and
[analysis.md](analysis.md) is why it happened. But a timeout is a *floor*, not a
deadline. It expires on the next system timer tick at or after the requested
duration, so `timeBeginPeriod(1)` is required simply to make seven milliseconds
mean something nearer seven than fifteen — and even then the loop has no phase.
Each pass waits seven milliseconds *from wherever the previous pass finished*,
so the frame period is 7 ms plus however long the tick took, and it drifts.

`TimerResolution` (`MessageLoop.cs:194-219`) is honest about what it costs:

> The resolution is process-wide and raising it permanently defeats timer
> coalescing and the deeper idle states, which for a process that runs all day is
> a real cost to pay for animations measured in tenths of a second.

It is now held only while something is moving (`WmDaemon.cs:251-252`), which is
the correct scope and was analysis 2's fix. The remaining question is whether it
needs to be held at all. **A high-resolution waitable timer does not need it.**
`CreateWaitableTimerExW` with `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION` — available
since Windows 10 1803, and already used in the spike at
`spikes/Shubbak.Spike/S2AnimationTiming.cs:98-103` — schedules to a sub-millisecond
absolute deadline without touching the global timer resolution. Passed as a
second handle to the same `MsgWaitForMultipleObjectsEx` call, it changes nothing
about the structure of the loop:

- the wait still services the message queue, so hook callbacks still arrive;
- `_wake` still short-circuits it for a keystroke;
- but the frame deadline becomes absolute rather than relative, so the loop stops
  accumulating drift, and `timeBeginPeriod` can be deleted outright.

That last part matters beyond Shubbak. A window manager that runs all day and
holds the system timer at 1 ms during every animation is measurably worse for
battery on every machine it is installed on, and the alternative costs nothing.

### Nothing is phase-locked to the compositor

Even a perfect 7 ms timer beats against a 6.944 ms compositor. The two clocks
drift past one another, and the visible result is a frame occasionally landing
just after a vblank instead of just before, which reads as a stutter in
otherwise smooth motion. This is what judder is.

`DwmGetCompositionTimingInfo` reports `qpcVBlank` and `qpcRefreshPeriod`, which
is everything needed to set the waitable timer to fire a fixed margin before the
next composition rather than a fixed interval after the last frame. `DwmFlush`
is the one-line version and is the wrong one here: it blocks, and this thread is
also the message pump, so blocking it stops WinEvent callbacks arriving.

### What it would take

Three changes, in increasing order of ambition, each independently useful:

1. Derive `FrameInterval` from the refresh rate of the monitor the animating
   windows are on, refreshed by the two-second `MaybeSyncMonitors` pass
   (`WmDaemon.cs:1901-1911`) that already exists.
2. Replace the timeout with a high-resolution waitable timer, and delete
   `TimerResolution`.
3. Phase-lock the timer to `qpcVBlank`.

The first two are contained. The third should be measured before it is believed,
which brings us to the reason none of this can currently be settled.

## The instrumentation cannot answer the question it was added for

`analysis2.md:795-801` says of the free-running frame rate: "**Measure it
first**", and notes that `_tickInterval` already records what is needed. It does
not, quite.

```csharp
// WmDaemon.cs:312-318
long now = Stopwatch.GetTimestamp();
double deltaMs = _lastTickTicks == 0
    ? 0
    : (now - _lastTickTicks) * 1000.0 / Stopwatch.Frequency;
_lastTickTicks = now;

if (deltaMs > 0) _tickInterval.Record(deltaMs);
```

Every tick is recorded, and the loop has two completely different modes: idle,
where the wait is 250 ms (`:270`), and animating, where it is 7 ms. The
distribution is therefore bimodal, and every percentile drawn from it is a
statement about the ratio between the two rather than about either. The
diagnostic report presents it as a frequency anyway:

```csharp
// WmDaemon.cs:1064-1066
$"- **Tick interval** (last {_tickInterval.Count}): p50 {_tickInterval.Percentile(0.5):F2} ms, " +
$"p99 {_tickInterval.Percentile(0.99):F2} ms, max {_tickInterval.Max:F2} ms all-time " +
$"(~{(_tickInterval.Percentile(0.5) > 0 ? 1000.0 / _tickInterval.Percentile(0.5) : 0):F0} Hz)",
```

On a desktop nobody is touching, that line reads about 4 Hz and means nothing.
Under a burst of activity it reads whatever fraction of the ring happened to be
animation frames. There is no configuration in which it answers "did the last
animation deliver its frames".

What is missing is small and specific:

- a `_frameInterval` recorded **only** on ticks where `_animation.IsAnimating`,
  so a percentile over it is a frame rate;
- frames due against frames delivered, which is the number ADR 0001's control
  group is quoted in ("33-42% of frames dropped") and which nothing currently
  computes;
- the duration of `CommitFrame` alone, separately from the whole tick, since
  that is where the 94.6% is claimed to be;
- the batch size, because a frame of two windows and a frame of twenty are not
  the same measurement;
- allocated bytes per tick, from `GC.GetAllocatedBytesForCurrentThread()`, which
  is the only way to hold the line ADR 0001 constraint 2 draws.

And `LogCategory.Animation` is declared at `LogLevel.cs:56-57`, named in the
category guidance at `:35` as where to look when motion stutters, and **written
to by nothing**. Grep across `src` returns the declaration and the doc comment.

This was true at analysis 2 and is restated because it is now the blocking item:
several findings below are sized rather than proved, and none of them can be
sized properly until the loop can say what it is doing.

---

## Every frame asks the application to relayout itself

The commit path is where the frame time actually goes, and it is doing more work
per frame than the motion requires.

### A resize is not a move

```csharp
// WindowCommitter.cs:569-588
foreach (AnimationFrame frame in frames)
{
    Rect target = Expand((nint)frame.Handle, frame.Rect);

    batch = PInvoke.DeferWindowPos(
        batch, new HWND((nint)frame.Handle), HWND.Null,
        target.X, target.Y, target.Width, target.Height,
        (SET_WINDOW_POS_FLAGS)DefaultFlags);
    ...
}
```

Every frame passes a position **and** a size. For the compositor those are not
comparable operations: moving a window translates a quad, whereas resizing it
forces DWM to reallocate the window's redirection surface and forces the
application to process `WM_WINDOWPOSCHANGED` and `WM_SIZE` and lay out its own
contents again. At twenty frames per animation, an application relayouts twenty
times to arrive somewhere it could have been told about once.

The codebase already knows this. `AnimationOptions.AnimateNewWindows` is off by
default and the reason given (`AnimationEngine.cs:47-53`) is exactly this
mechanism:

> It is also the most expensive animation there is: a window that relays out its
> contents on every resize does so once per frame, and File Explorer doing that
> through a whole animation is a visible stutter rather than a slide.

That is a correct diagnosis applied to one setting, when it is a property of
every animation the program performs. Two mitigations, either of which can be
had without new concepts:

- **`SWP_NOSIZE` when the size is unchanged for this frame.** Free, correct, and
  covers every animation that is a pure translation — a swap between
  equally-sized tiles, a workspace slide, a window moving between monitors of the
  same resolution.
- **Quantise the size.** Update position every frame and size only when it has
  moved more than a few pixels since the last committed size. The window arrives
  at exactly the right size because the final frame is exact
  (`AnimationEngine.cs:226` returns `track.To` verbatim at `progress >= 1`); the
  frames in between are within a pixel or two of a size nobody is measuring. For
  a 140 ms animation this turns twenty relayouts into three or four.

The honest version of the second is a config choice rather than a decision made
for everyone, in the same shape as `AnimateNewWindows`: some people will prefer
the exact intermediate size, and the cost falls on their applications rather than
on the window manager.

### Frames that change nothing

```csharp
// AnimationEngine.cs:226-230
Rect rect = progress >= 1 ? track.To : Interpolate(track.From, track.To, eased);
track.Current = rect;

if (written < destination.Length)
    destination[written++] = new AnimationFrame(track.Handle, rect, progress >= 1);
```

`track.Current` holds the previous frame's rectangle and is never compared
against the new one. `Interpolate` (`:316-320`) rounds to integers, and an
ease-out curve spends most of its duration in the settling tail where
consecutive frames round to the same integers. Every one of those is a
`DeferWindowPos` entry, which is a real window move and a real repaint request to
an application that is already where it is being told to go.

The comparison is one `Rect` equality — a `readonly record struct` of four ints —
against a field that is already being written. It shrinks the batch by whatever
fraction of the tail is stationary, which for `ease-out` over a short distance
is most of it.

There is a subtlety worth stating so it is not discovered as a regression: the
final frame must always be emitted even when identical, because `IsFinal` is what
records `_lastCommitted` and `_lastApplied` (`WindowCommitter.cs:596-613`), and a
missing entry there makes the skip check in `Commit` (`:287-295`) place the
window again on the next layout pass. The skip is for the intermediate frames
only.

### A hung application stalls the frame for everyone

```csharp
// WindowCommitter.cs:34-39
private const uint DefaultFlags =
    (uint)(SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
           SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
           SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER |
           SET_WINDOW_POS_FLAGS.SWP_NOSENDCHANGING |
           SET_WINDOW_POS_FLAGS.SWP_NOCOPYBITS);
```

`SWP_ASYNCWINDOWPOS` is absent. Without it, a `SetWindowPos` or
`EndDeferWindowPos` against a window owned by another thread **sends** messages
to that thread and does not return until it pumps them. Every window Shubbak
manages belongs to another process, so this applies to all of them, on every
frame.

The consequence is that one busy or hung application stalls the entire frame —
not only its own window, but every window in the same `DeferWindowPos`
transaction, because the transaction is atomic. An application that stops
pumping for 200 ms drops twenty-eight frames of everyone else's animation. The
symptom is motion that is smooth except when a particular application is on
screen, which is close to unreportable.

`SWP_ASYNCWINDOWPOS` posts the request instead. It is designed for precisely
this, it is one flag, and the committer is already structured to tolerate it:
nothing reads geometry back after a commit to confirm it landed.
`_lastCommitted` and `_lastApplied` are computed, not measured
(`WindowCommitter.cs:312-319`, `:594-613`), and the comment at `:276-286`
explains that comparing against where the window actually is was deliberately
abandoned.

Two things to check rather than assume, both listed at the end as unverified:
whether the asynchronous move can be reordered against the synchronous `Raise`
at `:361-367`, and whether `SWP_NOCOPYBITS` is now costing more than it saves —
it discards the client area and forces a full repaint, which is the right trade
for one placement and possibly the wrong one for twenty.

### A lock and a hash per window per frame

```csharp
// WindowCommitter.cs:468-483
private static Win32Window.ShadowMargins ShadowOf(nint handle)
{
    lock (s_shadowGate)
    {
        if (!s_shadows.TryGetValue(handle, out Win32Window.ShadowMargins margins))
        {
            margins = Win32Window.GetShadowMargins(handle);
            s_shadows[handle] = margins;
        }

        return margins;
    }
}
```

Caching the DWM round trip was the point and it is correct — the comment at
`:426-430` is right that this may not make a system call per frame. But what is
left is still a lock acquisition and a dictionary probe **per window per frame**,
on the one path in the program that ADR 0001 constraint 2 names.

It is entirely removable. The margins are a property of the window, the track
already exists for the duration of the motion, and `Retarget`
(`AnimationEngine.cs:173-201`) is the natural place to resolve them once. Storing
`From` and `To` already expanded would remove the lookup from `CommitFrame`
altogether, at the cost of the engine knowing about shadows — which is a layering
question worth arguing about, since `Shubbak.Core` contains no Win32 and that is
described in the README as the highest-leverage decision in the project. The
alternative that keeps the layering is a margin cached on the committer's own
per-window record rather than in a static dictionary behind a static lock.

### An index rebuilt for nothing

```csharp
// AnimationEngine.cs:241-245
if (surviving != _count)
{
    _count = surviving;
    RebuildIndex();
}
```

```csharp
// AnimationEngine.cs:299-303
private void RebuildIndex()
{
    _index.Clear();
    for (int i = 0; i < _count; i++) _index[_tracks[i].Handle] = i;
}
```

A full dictionary clear and refill runs on every frame in which any track
finishes, and on every `Remove` (`:270`). Tracks finish at different times —
`Retarget` restarts the clock per window and the layout pass calls it in list
order — so through the tail of a layout change this fires repeatedly.

It allocates nothing, which is why it has survived. But `_index` is a
`Dictionary<long, int>` guarding an array that in practice holds fewer entries
than a dictionary needs to be worth its indirection: the initial capacity is 64
(`:145`) and a desktop with sixty-four windows in simultaneous motion is not the
case being optimised for. A linear scan over the dense `Track[]` would remove the
dictionary, the rebuild and the `TryGetValue` in `TryGetCurrent` (`:251-261`) in
one go. If the dictionary is kept, the compaction loop already knows both indices
and can fix the index up in place rather than discarding it.

---

## The solver throws away its own answer

```csharp
// Easing.cs:143-173
private double SolveForX(double x)
{
    double u = x;

    for (int i = 0; i < 4; i++)
    {
        double error = Bezier(u, _x1, _x2) - x;
        if (Math.Abs(error) < 1e-6) return u;

        double slope = BezierDerivative(u, _x1, _x2);
        if (Math.Abs(slope) < 1e-9) break;

        u -= error / slope;
    }

    // Fall back to bisection if Newton stalls, which can happen on curves with
    // a near-flat segment.
    double low = 0, high = 1;
    u = x;

    for (int i = 0; i < 12; i++)
    {
        double value = Bezier(u, _x1, _x2);
        if (Math.Abs(value - x) < 1e-6) break;

        if (value > x) high = u; else low = u;
        u = (low + high) / 2;
    }

    return u;
}
```

The convergence test is at the **top** of the Newton loop, so it examines the
value produced by the previous iteration. When the fourth iteration updates `u`,
the loop condition fails and nothing checks the result. Control falls into the
bisection block, which executes `u = x` — discarding four iterations of
Newton-Raphson — and restarts from a bracket of `[0, 1]`.

That bisection is then worse than it looks. Its first step compares `Bezier(x)`
against `x`, sets one end of the bracket, and assigns `u = (low + high) / 2`,
which is `0.5` regardless of where `x` was. A good estimate has been replaced by
the midpoint of the whole domain. Twelve halvings leave an interval of `1/4096`
in the parameter, against the `1e-6` the Newton path was holding itself to.

So on the curves that need four iterations, the function returns an answer some
four orders of magnitude less accurate than the one it computed and threw away,
having done twenty polynomial evaluations to do it. The doc comment at `:126`
states "Newton-Raphson converges in three or four iterations here", which is a
description of the intent rather than of the code.

How much this matters depends on the curve, and this is the part that wants
measuring rather than asserting:

- `Linear` (`:33`) returns at the shortcut on `:137` and never solves.
- `EaseOut` (`:38`) — `(0, 0, 0.58, 1)`, the default for `WindowMove`,
  `LayoutChange` and `WorkspaceSwitch` (`AnimationEngine.cs:66-73`) — converges
  in about three iterations by hand at `t = 0.5` and returns early. The default
  configuration is probably not hitting the bug.
- `EaseOutExpo` (`:46`) — `(0.16, 1, 0.3, 1)` — and `EaseOutBack` (`:43`) are the
  steep ones, and `EaseOutExpo` is the default for `WindowOpen`
  (`AnimationEngine.cs:63-64`). These are the candidates.

The fix is three lines and does not need a decision: check convergence after the
update as well as before, return the Newton result when it converged, and seed
the bisection with a bracket around the last Newton estimate instead of throwing
it away. Precomputing the cubic's coefficients once per track rather than
re-deriving them from the Bernstein form on every evaluation is a separate and
smaller win.

This is filed under performance because that is what the review was looking for,
but it is really a correctness finding with a performance smell attached. The
cost — twenty double-precision polynomial evaluations per window per frame — is
not the problem. Returning the wrong number is.

---

## Allocation on the daemon thread is the hook thread's problem

The whole design rests on one coupling, stated plainly in
`LogInterpolatedStringHandler.cs:17-21`:

> Allocation on the message loop means garbage collections on the message loop,
> and a collection suspends every thread in the process — including the one
> servicing the keyboard hook, which is holding a keystroke the user is waiting
> on.

That is the correct frame for everything in this section. None of these
allocations is large. What they are is *on the wrong thread*, and the deadline
they compete against is Windows' 300 ms unhook threshold.

### The interpolated-string handler is defeated by its own threshold

This is the one to fix first, because the machinery to fix it already exists and
is being bypassed by a two-line interaction between two files.

`DebugLogHandler` decides whether to build anything in its constructor:

```csharp
// LogInterpolatedStringHandler.cs:76-84
public DebugLogHandler(int literalLength, int formattedCount, out bool enabled)
{
    enabled = Log.IsEnabled(LogLevel.Debug);
    ...
}
```

`Log.IsEnabled` deliberately compares against the ring buffer's threshold rather
than the sink's:

```csharp
// Log.cs:88-95
public static bool IsEnabled(LogLevel level) => level >= RingLevel();

private static LogLevel RingLevel()
{
    LogLevel level = s_level;
    return level == LogLevel.None ? LogLevel.None : (LogLevel)Math.Max(0, (int)level - 1);
}
```

`s_level` defaults to `Information` (`Log.cs:36`), and `LogLevel` is
`Trace = 0, Debug = 1, Information = 2` (`LogLevel.cs:11-17`). So `RingLevel()`
is `Debug`, and:

> **`Log.IsEnabled(LogLevel.Debug)` is `true` in the default configuration.**

Which means this, on every bound keystroke:

```csharp
// WmDaemon.cs:383-384
if (Log.IsEnabled(LogLevel.Debug))
    Log.Debug(LogCategory.Hook, $"{binding.Key.Display} -> {binding.Commands.Describe()}");
```

and this, on every layout pass:

```csharp
// WmDaemon.cs:1797-1801
if (moved > 0 && Log.IsEnabled(LogLevel.Debug))
{
    Log.Debug(LogCategory.Layout,
        $"placed {moved}/{total} windows, {_animation.ActiveCount} animating");
}
```

Both build a string. `Describe` has a fast path for the common single-command
binding (`CommandDescription.cs:19-26`) and returns the stored name without
joining, which is a good decision already made — but the interpolation around it
still allocates the result. Then `Log.Write` runs:

```csharp
// Log.cs:240-253
private static void Write(LogLevel level, LogCategory category, string message)
{
    if (level < RingLevel()) return;

    var entry = new LogEntry(DateTime.Now, level, category, message);

    int slot = Interlocked.Increment(ref s_ringWrite) - 1;
    s_ring[(int)((uint)slot % RingCapacity)] = entry;
    Interlocked.Increment(ref s_totalEntries);

    if (level < s_level) return;
    ...
}
```

`DateTime.Now`, not `UtcNow`. The difference is a time-zone lookup and a
conversion, which is roughly an order of magnitude more expensive than reading
the clock, and it is paid on the daemon thread for an entry that is then
discarded at `:253` at the default level. The entry is only ever formatted on the
writer thread (`:366`), so the timestamp does not need to be a local `DateTime`
at all — a raw timestamp converted at format time would be cheaper still and
would keep the ring entry a pure value.

The ring-one-level-deeper design is good and worth keeping: it is what makes
`shubbak diagnose` useful from a user who had not thought to enable logging. What
is wrong is that it silently promotes `IsEnabled(Debug)` to always-true, which
switches off the exact mechanism built to prevent allocation on this thread. The
handler's own doc comment describes the failure it now permits:

> On the window manager's tick that meant a string per keystroke and a string per
> window event, permanently, for nothing.

Three ways out, and this wants a decision rather than a patch:

1. Have the hot call sites guard on `Log.Level` (the sink) rather than
   `Log.IsEnabled` (the ring). Cheapest, and makes the two thresholds a visible
   choice at each site rather than an invisible one.
2. Keep the ring at the sink's level and accept a shallower `diagnose` history.
3. Record into the ring **unformatted** — the category, the level, the handle, the
   virtual key — and format only when the report is written. Most expensive to
   build, and the only one that keeps both properties.

### JSON for nobody

```csharp
// WmDaemon.cs:2518
_ipc?.Publish(wmEvent.Topic, StateProjection.Payload(wmEvent, _wm));
```

`StateProjection.Payload` (`StateProjection.cs:90-121`) is a full
`JsonSerializer.Serialize` per event. It is evaluated as an argument, so it runs
whether or not any client is connected and whether or not any connected client
subscribed to that topic. `IpcServer.Publish` then discovers there is nobody to
send it to:

```csharp
// IpcServer.cs:93-105
public void Publish(string topic, string json)
{
    ClientConnection[] clients;
    lock (_gate) clients = [.. _clients];

    if (clients.Length == 0) return;

    string message = JsonSerializer.Serialize(
        new IpcEvent(topic, json), IpcJsonContext.Default.IpcEvent);

    foreach (ClientConnection client in clients)
        if (client.IsSubscribed(topic)) client.TryEnqueue(message);
}
```

With clients connected the cost is: one lock and one array allocation for the
snapshot, a second serialization for the envelope, and two more lock
acquisitions per client for `IsSubscribed` and `TryEnqueue` — so `1 + 2N` locks,
two serializations and an array, **per event**. A workspace switch emits a dozen
events, and Taj holds one connection per monitor. On a three-monitor desktop
that is roughly two dozen serializations and forty lock acquisitions inside the
tick that is also computing the layout and committing the first frame of the
animation.

`IpcServer.cs:124-126` already names this cost in a different context:

> Every connected client costs a lock taken on the daemon thread for every event
> published, so an unbounded set is a way for one runaway process to slow the
> window manager down for everybody.

The cheap half is a subscriber test before the payload is built — the topic is
known at the call site, and a set of subscribed topics maintained on subscription
change rather than interrogated per event makes it a single read. That takes the
whole tail to nothing when no bar is running, which is the case for anyone using
the window manager without Taj.

### The layout pass allocates per container, per pass

```csharp
// LayoutEngine.cs:154-172
// Only tiled children participate; a floating window must not consume a
// slot, or removing it from the flow would leave a hole.
Node[] tiled = [.. container.Children.Where(IsTiled)];
if (tiled.Length == 0) return;

Rect[] rects = new Rect[tiled.Length];

if (tiled.Length == container.Children.Count)
{
    container.Layout.Arrange(container, area, in options, rects);
}
else
{
    using var view = TiledView.Create(container, tiled);
    view.Container.Layout.Arrange(view.Container, area, in options, rects);
}
```

Per container, per pass: a LINQ `Where` iterator, a collection-expression builder
and the resulting `Node[]` (`:156`), and a `Rect[]` (`:159`). When a container
mixes tiled and non-tiled children, `TiledView.Create` (`:273-297`) adds a
`double[]` and **a whole `ContainerNode`**.

Underneath that, the predicate is more expensive than it looks:

```csharp
// Node.cs:162-167
public bool ParticipatesInTiling => this switch
{
    WindowNode window => window.IsTiled,
    ContainerNode container => container.DescendantWindows().Any(w => w.IsTiled),
    _ => false,
};
```

```csharp
// Node.cs:140-150
public IEnumerable<Node> SelfAndDescendants()
{
    yield return this;
    foreach (Node child in Children)
        foreach (Node d in child.SelfAndDescendants())
            yield return d;
}

public IEnumerable<WindowNode> DescendantWindows() =>
    SelfAndDescendants().OfType<WindowNode>();
```

A recursive `yield return` allocates one iterator state machine per node per
level of nesting, `OfType` adds another, and `foreach (Node child in Children)`
boxes a `List<Node>.Enumerator` at each level because `Children` is typed
`IReadOnlyList<Node>` (`:91`). All of that to answer a boolean — and it walks the
entire subtree to do it, once per child of every container, on every pass.

`ArrangeNonTiled` then walks it again for **every workspace on every monitor**,
including workspaces nobody is looking at:

```csharp
// LayoutEngine.cs:211-213
private void ArrangeNonTiled(WorkspaceNode workspace, Rect workArea, bool visible)
{
    foreach (WindowNode window in workspace.DescendantWindows())
```

reached from `ArrangeMonitorInto` (`:125-132`), which loops every workspace on
the monitor rather than only the active one.

Downstream of the engine, `WindowCommitter.Commit` allocates a fresh list per
pass (`:239`) plus another when anything is raised (`:270`), and three loops in
`WmDaemon` box an enumerator each by iterating `IReadOnlyList<Placement>` —
`:1679`, `ConcealOutgoing` at `:1697`, and `TraceLayout` at `:1816`. The
codebase already avoids exactly this elsewhere, with the reason written out:

```csharp
// WmEventGeometry.cs:64-66
// Indexed rather than foreach: this runs on the tick path, and the enumerator
// for an IReadOnlyList is an interface call per element that allocates.
```

The inconsistency is the finding. Somebody worked this out once and applied it
in one file.

`ParticipatesInTiling` deserves separate treatment from the buffer pooling. It is
a pure function of the subtree, it is asked repeatedly during a single pass, and
the tree only changes through `TreeOps`. Caching it on the node and invalidating
up the parent chain on mutation removes the walk entirely rather than making it
cheaper, and it would also speed up focus navigation, which asks the same
question (`FocusNavigator`, `GeometricNavigator`).

### A full re-arrange for every keystroke

```csharp
// WindowManager.cs:1377-1378
public IReadOnlyList<Placement> ComputePlacements() =>
    _engine.Arrange(Root, Options.ToArrangeOptions() with { Focused = FocusedWindow });
```

There is no incremental path. Every command that dirties the layout re-arranges
every monitor, every workspace and every window, then `Schedule` reads the real
position of each visible one:

```csharp
// WmDaemon.cs:1739-1741
Rect current = _animation.TryGetCurrent(placement.Window.Handle, out Rect inFlight)
    ? inFlight
    : WindowCommitter.VisibleBounds(handle);
```

`VisibleBounds` is a `GetWindowRect` (`WindowCommitter.cs:502-508`), and `Commit`
adds an `IsWindow` per placement (`:249`), and `Reveal` adds an `IsIconic`
(`:624`). So a `focus --direction left` — which moves nothing — costs a full tree
arrange, two or three system calls per window, a list allocation, an enumerator
per loop, a JSON serialization per event, and a string for the log line.

That is the real answer to "keystroke handling performance". The hook is 0.8
microseconds and has been measured to death. The distance between pressing the
key and seeing anything change is this pass, and it is doing work proportional to
the whole desktop for a change that affects one container.

Dirty-subtree arrangement is the structural fix and it is not small: the engine
would need to know which workspace changed, `_placements` would need to be
mergeable rather than rebuilt, and the tests are all written against whole-tree
output. The intermediate step — arranging only the workspaces that are
displayed, since the inactive ones are only being arranged so they look right
when shown, and they can be arranged at the moment they are shown instead —
is much smaller and takes the common case from every workspace to one per
monitor.

---

## Three things in the callback

The hook itself remains, as the last two reviews found, in good shape. These are
not latency findings. Two are robustness and one is a small waste.

### It is the only `UnmanagedCallersOnly` without a `try`

`WinEventSource.Callback` complies with ADR 0001 constraint 4:

```csharp
// WinEventSource.cs:155-159
catch
{
    // An exception escaping an UnmanagedCallersOnly callback tears down the
    // process (docs/adr/0001-language-choice.md, constraint 4).
}
```

`KeyboardSource.Callback` (`:240-305`) has no `try` at all, and there is a
reachable throw. `MessageLoop.Wake` is check-then-act:

```csharp
// MessageLoop.cs:69-72
public void Wake()
{
    if (!_disposed) _wake.Set();
}
```

`_disposed` is a plain `bool` written by `Dispose` (`:171-173`) on the daemon
thread immediately before `_wake.Dispose()`. A keystroke arriving in that window
calls `Set()` on a disposed `AutoResetEvent`, which throws
`ObjectDisposedException` — inside the hook callback, from
`KeyboardSource.Enqueue:484`, which is reached from the callback at `:293`. The
process dies during shutdown, which is when it is least likely to be noticed and
most likely to leave windows stranded.

Both halves want fixing: a `try`/`catch` around the callback body that returns
`CallNextHookEx`, and a `Wake` that cannot throw.

### A table published without a fence

```csharp
// BindingTable.cs:27-32
private Dictionary<int, Keybinding> _default = [];

private Dictionary<string, ModeTable> _modes =
    new(StringComparer.OrdinalIgnoreCase);

private ModeTable? _activeMode;
```

Written on the daemon thread by `Load` (`:86-89`) and `SetMode` (`:111-115`);
read on the hook thread by `IsBound` (`:135`, `:154`). No release on the write,
no acquire on the read.

[analysis2.md](analysis2.md) recorded this as "benign in practice and unsound in
principle", which is right for x86-64: the store buffer is FIFO, so the writes
that populate a dictionary's internal arrays cannot become visible after the
write that publishes the reference. It is not right in general. On a weaker
memory model the hook thread can observe the new reference with the arrays not
yet visible, and the result is a `NullReferenceException` inside an
`[UnmanagedCallersOnly]` callback — which is the previous finding, arriving by a
different route.

`volatile` on the four fields costs nothing on the read side on x64 — a volatile
read of a reference is a plain load — and closes it. Given that Windows on ARM64
exists and that this is exactly the class of bug that never reproduces, it is
cheap insurance rather than pedantry.

### Six reads of a keyboard nobody asked about

```csharp
// KeyboardSource.cs:332-338
private static KeyModifiers ReadModifiers() => DeriveModifiers(
    leftAlt: IsHeld(VIRTUAL_KEY.VK_LMENU),
    rightAlt: IsHeld(VIRTUAL_KEY.VK_RMENU),
    leftControl: IsHeld(VIRTUAL_KEY.VK_LCONTROL),
    rightControl: IsHeld(VIRTUAL_KEY.VK_RCONTROL),
    shift: IsHeld(VIRTUAL_KEY.VK_SHIFT),
    windows: IsHeld(VIRTUAL_KEY.VK_LWIN) || IsHeld(VIRTUAL_KEY.VK_RWIN));
```

Six or seven `GetAsyncKeyState` calls, on every key-down, for every character
typed in every application on the machine. Analysis 2 found this being paid on
key-**up** as well and that half has been fixed — the release now returns before
the modifiers are read (`:263-272`), with the reasoning written out at `:260-262`.

What is left is that the modifiers are read before anything has established that
the key could match. Two cheap filters, in order of value:

- **Modifier keys themselves.** Pressing shift, control, alt or a Windows key is
  a key-down like any other, and reads six modifier states to build a lookup that
  cannot hit — `KeyParser` requires a non-modifier key (`SHB0204`), so no binding
  is ever keyed on one. `IsBound` already has `IsModifierKey` (`BindingTable.cs:158-165`)
  for the swallowing-mode case; hoisting that test above `ReadModifiers` costs a
  compare chain and removes the whole cost for what is a large fraction of all
  key-downs.
- **A participation bitmap.** A 256-entry `bool[]` built when the binding table
  is loaded, recording which virtual keys appear in *any* binding under *any*
  modifier set. A key-down for a virtual key nobody bound can return before
  reading a single modifier. This is most of the alphabet on a typical config and
  effectively all punctuation and digits.

Neither changes the semantics of `GetAsyncKeyState`, which is chosen for a good
reason spelled out at `:307-331` and should not be revisited. Both need care in
one place: a non-pass-through binding mode swallows everything, so both filters
must be conditional on `_activeMode` being null or pass-through, exactly as
`IsBound` already is.

It should be said plainly that this is not a throughput finding. At ten
keystrokes a second, six `GetAsyncKeyState` calls cost nothing measurable. The
argument for doing it is the tail: each is a managed-to-native transition inside
a callback with a 300 ms deadline, and the file's own rule (`:52-55`) is that
nothing in it should do work it does not need to.

---

## The machine is not being asked what it wants

Three system-level questions the program does not ask. Each is a single call.

### E-cores and EcoQoS

There is no `SetProcessInformation` anywhere in `src`. On Windows 11 with a
hybrid CPU, a process that looks like background work — long-lived, mostly idle,
low CPU — is a candidate for scheduling onto efficiency cores and for EcoQoS
throttling. That is a reasonable default for most daemons and the wrong one for
this one, which has a thread with a 300 ms hard deadline and a loop that wants to
wake accurately every seven milliseconds.

`PROCESS_POWER_THROTTLING_STATE` with `PROCESS_POWER_THROTTLING_EXECUTION_SPEED`
explicitly disabled opts out. It is a few lines in `Program.cs` next to the
`GCSettings.LatencyMode` line (`:25`) that is there for exactly the same reason,
and it is arguably a larger effect than anything else in this document on the
machines it applies to.

The same API at thread granularity would let the keyboard hook thread opt out
individually, which is more targeted than opting the whole process out. Worth
measuring both ways.

### The accessibility setting

`SPI_GETCLIENTAREAANIMATION` is absent from `src`. A user who has turned off
"Show animations in Windows" has answered the question, in the place the
operating system asks it, and Shubbak animates anyway. Honouring a setting the
user explicitly set is not a preference, and reading it is one
`SystemParametersInfo` call at startup plus a `WM_SETTINGCHANGE` handler if it
should track changes.

### Remote desktop

`SM_REMOTESESSION` is absent. Over RDP every animation frame is a screen-region
update on the wire, twenty of them per window per animation, to move a window
somewhere it could be told about once. Detecting the session type and treating
animation as disabled is one `GetSystemMetrics` call.

All three point the same way: `AnimationOptions.Enabled` is currently a config
value, and it wants to be a config value combined with what the system says.

---

## Smaller things

- **`AnimationEngine.Tick` never consults `Options.Enabled`.** It is read only in
  `Retarget` (`:183`). `LoadConfig` swaps the options wholesale
  (`WmDaemon.cs:2198`), so a reload that sets `enabled #false` stops new
  animations and lets every in-flight track run to completion. `Clear()`
  (`:274-278`) exists for this — its summary says "Stops everything, e.g. when
  animation is turned off" — and still has no callers. Restated from analysis 2
  unchanged.

- **`Track.Active` is dead.** Set to `true` at `AnimationEngine.cs:198` and never
  to `false` anywhere. The guard at `:219` therefore never fires, and if it ever
  did it would silently drop the track from compaction — an inactive track is
  skipped by `continue` before the `surviving` bookkeeping, so it would be
  removed from the array without being removed from `_index`.

- **`focus-follows-cursor` and both `cursor-jump` settings still parse and do
  nothing.** `ShubbakConfig.cs:93`, `:96`, `:99`; parsed at
  `ConfigLoader.cs:184`, `:187-188`; absent from `ToWmOptions()`
  (`ShubbakConfig.cs:181-195`) and read by nothing in `Shubbak.Wm`,
  `Shubbak.Core` or `Shubbak.Native`. They are still in the example config
  (`docs/shubbak.example.kdl:15`, `:58`). Analysis 2 recorded the decision to
  delete them and gave the reasoning; the decision has not been carried out, and
  `check-config` still reports a file containing them as ok.

- **Four dead entries in the CsWin32 manifest.** `SetLayeredWindowAttributes` and
  `LAYERED_WINDOW_ATTRIBUTES_FLAGS`
  (`src/Shubbak.Native/NativeMethods.txt:68-69`), `EVENT_OBJECT_LOCATIONCHANGE`
  (`:93` — left behind when the subscription was removed), `MapVirtualKey` and
  `MAP_VIRTUAL_KEY_TYPE` (`:114-115`), and `PostQuitMessage` (`:124`) have no
  callers in `src`. The file's own header says "Every entry is API the window
  manager genuinely needs. Keeping the list tight keeps the generated surface
  small, which keeps NativeAOT output small". Six entries is not a size problem;
  the header making a claim that is no longer true is the finding.

- **The README's test count is stale again.** It says 737 in two places
  (`README.md:23`, `:198`); the tree declares 793 `[Fact]` and `[Theory]`
  methods. Analysis found this wrong at 459, analysis 2 at 635, and here at 737.
  Three reviews, three wrong numbers, which is a number that wants deriving in CI
  or removing.

---

## What to do, in order

Sequenced so that each tier is independently shippable and the measurement comes
before the claims that depend on it.

| | Work | Why it is first/last | Since |
|---|---|---|---|
| **M** | Split `_frameInterval` from `_tickInterval`; frames due vs delivered; `CommitFrame` duration and batch size; allocated bytes per tick; emit `LogCategory.Animation`; surface all of it in `diagnose` | Nothing below can be sized without it, and analysis 2 already asked for it | **shipped** |
| **0** | `Easing.SolveForX` convergence; `try`/`catch` in the keyboard callback and a `Wake` that cannot throw; `volatile` on `BindingTable`; `Tick` honours `Enabled`, `Clear()` wired, `Track.Active` deleted | Correctness on paths where the failure mode is a dead process or a wrong number | **shipped**, three of them by a different route than prescribed |
| **1** | `SWP_ASYNCWINDOWPOS`; skip unchanged intermediate frames; `SWP_NOSIZE` when the size is unchanged; subscriber gate before `Payload`; `DateTime.Now`; shadow margins off the frame path; `RebuildIndex`; bezier coefficients per track; power-throttling opt-out | Small diffs, no new concepts, most of the available win | **mostly** — see below |
| **2** | Refresh-rate-derived interval; high-resolution waitable timer replacing `timeBeginPeriod`; vblank phase lock; `SPI_GETCLIENTAREAANIMATION` and `SM_REMOTESESSION` | A real frame clock. New CsWin32 imports, all AOT-safe | **part** — see below |
| **3** | Pooled buffers in `LayoutEngine`; cached `ParticipatesInTiling`; non-iterator subtree walk; reused commit lists; concrete `List<Placement>` at the three boxing sites; an allocation-counting test for the tick | Removes the GC coupling to the hook thread. ADR 0001 asks for the test | **the test only** — `TickAllocationTests`; the rest is optimising against a cost measurement cannot find |
| **4** | Arrange displayed workspaces only, then dirty-subtree arrangement; size-quantised animation as a config choice; a dedicated frame thread | Structural. Each is its own decision with its own tests | not started |

What is left in tiers 1 and 2, and why:

- **`DateTime.Now`** is still on the write path at `Log.cs:261`. Untouched
  because the log-level finding turned out to dominate it by so much that fixing
  the timestamp first would have been measuring the wrong thing.
- **Bezier coefficients per track** were not precomputed. The solve stopped being
  interesting once the iteration count fixed the accuracy: it runs once per
  window per frame at a p50 the frame budget does not notice.
- **Shadow margins** are cached (`WindowCommitter.cs:560`) but `ShadowOf` still
  takes a lock per call. The per-call `PInvoke` is gone; the contention the
  finding named is not.
- **The high-resolution waitable timer** was not needed as written. The symptom
  it was aimed at — waking late while pacing — had a different cause: Windows 11
  discards a windowless process's `timeBeginPeriod` unless it explicitly clears
  `PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION`. One flag on a call the
  process already made. The timer remains the more robust answer and is now the
  fallback rather than the plan.
- **Vblank phase lock** is untouched, and is the one item in tier 2 that is still
  a real frame-clock improvement rather than a workaround for one.

One experiment belongs beside the `SWP_ASYNCWINDOWPOS` row, though it is not from
this document. Sending the **settling frame synchronously** — so a window coming
to rest is told properly and paints promptly instead of showing bare background —
was predicted to cost about the 3.7 ms the median frame cost. Measured, it cost
**87 ms**: the end of a resize is exactly when an application does its layout and
repaint work, and so the worst possible moment to wait on it. Commit p99 went
from 1.35 ms to 55.36 ms, half the frames in each motion were lost, and since the
tick thread dispatches commands it added 87 ms of keystroke latency after every
motion. Reverted; the reasoning is kept at `WindowCommitter.cs:79-89` so it is
not tried a second time.

The one deliberate omission from that table is the item below, which is not a
change.

## The one that is a project, not a change

Everything above makes the current approach — `SetWindowPos` per window per frame
— as cheap as it can be. It does not remove the ceiling, which is that every
frame of every animation is a real window operation that a real application has
to respond to. That is why resizes are expensive, why File Explorer stutters, and
why the honest recommendation for size quantisation is "update it less often"
rather than "make it faster".

The approach that removes the ceiling is the one compositing window managers use:
capture the window into a visual, animate the visual on the GPU, and touch the
real window exactly once, at the end. On Windows that is `Windows.Graphics.Capture`
into a DirectComposition visual, with the capture border suppressed
(`IsBorderRequired`, Windows 11 21H2 and later). The application is resized once,
sees one `WM_SIZE`, and every frame in between is a transform of a texture.

It is also a substantial piece of work with real risks: a capture session per
animating window, an overlay surface to composite into, WinRT interop under
NativeAOT, and a fallback path for every case where capture is refused. It should
not be built on the strength of an argument.

The project already has the right instrument for this. `spikes/Shubbak.Spike`
contains S1 through S4, and ADR 0001 was written from their numbers rather than
from reasoning. This wants **S5**: capture one window, animate it, measure frames
delivered and CPU against the current path on the same machine, and find out what
capture refuses. Then an ADR, and then a decision. That is the same process that
produced everything this document has been able to take for granted.

---

## How this was gathered

A review of the working tree at `d551b04`, clean apart from the untracked
`ideas/analysis2.md`, on 2026-08-04, asked to find performance in the keystroke
and animation paths.

**Verified directly**, by reading the file and by grep across `src` and `tests`:

- `FrameInterval` is a `static readonly` 7 ms with no config key and no
  refresh-rate query anywhere (`WmDaemon.cs:259-260`); `MonitorInfo` carries no
  refresh rate (`MonitorSource.cs:21-26`); `EnumDisplaySettings`,
  `DwmGetCompositionTimingInfo`, `DwmFlush`, `SystemParametersInfo`,
  `GetSystemMetrics` and `SetProcessInformation` are absent from
  `src/Shubbak.Native/NativeMethods.txt` and from `src` entirely
- `Log.IsEnabled(LogLevel.Debug)` is `true` at the default level, traced through
  `Log.cs:36`, `:88-95` and `LogLevel.cs:11-17`, and consumed by
  `DebugLogHandler`'s constructor at `LogInterpolatedStringHandler.cs:78`
- `Write` calls `DateTime.Now` at `Log.cs:244` and discards the entry at `:253`
- `StateProjection.Payload` is an eagerly-evaluated argument at `WmDaemon.cs:2518`;
  `IpcServer.Publish:93-105` serializes a second time and takes `1 + 2N` locks
- `Easing.SolveForX` checks convergence only at the top of the Newton loop and
  executes `u = x` at `Easing.cs:161` before bisecting from `[0, 1]`
- `SWP_ASYNCWINDOWPOS` is absent from `WindowCommitter.cs:34-39`
- `AnimationEngine.Tick` writes a frame without comparing against `track.Current`
  (`:226-230`); `RebuildIndex` is a full clear and refill (`:299-303`);
  `Track.Active` is set true at `:198` and never false; `Options.Enabled` is read
  only in `Retarget` (`:183`); `Clear()` has no callers
- `ShadowOf` takes a static lock per call (`WindowCommitter.cs:468-483`), reached
  per window per frame from `CommitFrame:576`
- `KeyboardSource.Callback` has no `try`/`catch`; `MessageLoop.Wake` is
  check-then-act on a plain `bool`
- `BindingTable`'s four fields are plain, written at `:86-89` and `:111-115`, read
  on the hook thread at `:135` and `:154`
- the `LayoutEngine` allocation sites at `:156`, `:159`, `:275` and `:278`; the
  iterator chain in `Node.cs:140-150` and `:162-167`; the three boxing `foreach`
  loops at `WmDaemon.cs:1679`, `:1697` and `:1816`, against the counter-example
  at `WmEventGeometry.cs:64-66`
- `ArrangeMonitorInto` arranges every workspace, not only the active one
  (`LayoutEngine.cs:125-132`)
- the foreground handler's identity guard at `WmDaemon.cs:600`, which is what
  disproves analysis 2's implied focus echo
- `FocusFollowsCursor`, `CursorJumpOnMonitorFocus` and `CursorJumpOnWindowFocus`
  are parsed, stored, absent from `ToWmOptions()`, and read by nothing
- the dead manifest entries at `NativeMethods.txt:68-69`, `:93`, `:114-115`, `:124`
- the test declaration count: 793, against 737 in the README

**Reasoned from the code and not reproduced.** Every one of these is worth
confirming before it is acted on, and tier M exists so that they can be:

- **What the frame rate actually is.** The mechanism of each finding is read
  directly, but no frame rate was measured, on any panel, at any refresh rate.
  The claim that a 60 Hz panel discards well over half the committed frames
  follows from the two numbers and from how DWM composites; it was not observed.
  `_tickInterval` cannot settle it for the reason given above.
- **That `SWP_ASYNCWINDOWPOS` is a net win.** The blocking behaviour of
  `SetWindowPos` across input queues is documented, and the committer's
  independence from read-back is verified by reading. Whether the asynchronous
  move can be reordered against the synchronous `Raise` at
  `WindowCommitter.cs:361-367`, and whether anything downstream depends on the
  move having completed when `EndDeferWindowPos` returns, was not tested.
- **That `SWP_NOCOPYBITS` is now costing more than it saves.** Reasoned from what
  the flag does during a twenty-frame animation rather than measured, and it
  points the opposite way from the reason it was presumably added.
- **Which easing curves reach the Newton fall-through.** `EaseOut` was worked
  through by hand at one value of `t` and converges in three iterations, so the
  default configuration is probably unaffected. `EaseOutExpo` and `EaseOutBack`
  are the candidates and neither was evaluated.
- **That E-core scheduling and EcoQoS affect this process.** The APIs and the
  behaviour are documented; that Windows actually classifies `shubbak-wm` as a
  throttling candidate on a hybrid machine was not observed, and it is the kind
  of claim that varies by build and by power plan.
- **The relative cost of the layout pass in keystroke-to-pixels latency.** The
  work it does is enumerated by reading — a full tree arrange, two or three
  system calls per window, the allocations listed — but the split between that
  and everything else in the tick was not measured. `_tickDuration`
  (`WmDaemon.cs:61`, `:353`) records the whole tick and cannot separate them.

No profiler was run and nothing here was measured under load, which is the same
caveat analysis 2 closed with and the same reason tier M is first rather than
last.
