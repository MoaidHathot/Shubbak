# ADR 0001 — Implementation language for Shubbak

- **Status:** Accepted. All four spikes complete.
- **Date:** 2026-08-01
- **Deciders:** @moaid
- **Supersedes:** —

## Context

Shubbak is a tiling window manager for Windows, with an animation engine and a
companion bar (Taj). The comparable projects are all systems languages:

| Project | Language |
| --- | --- |
| GlazeWM | Rust |
| komorebi | Rust |
| Zebar | Rust + WebView2 |

The question is whether .NET 10 introduces performance or compatibility problems
severe enough to justify Rust (or Zig) instead. Two hot paths could plausibly
fail under a garbage-collected runtime:

1. **`WH_KEYBOARD_LL` callback.** Windows silently unhooks a low-level keyboard
   hook whose callback exceeds `LowLevelHooksTimeout` (default **300 ms**). Once
   unhooked, every keybinding stops working until the process restarts, with no
   error surfaced. A GC pause on the hook thread is the obvious hazard.
2. **Animation tick loop.** At 144 Hz the frame budget is **6.94 ms**. Missing
   frames produces visible stutter, which is precisely the quality gap we are
   trying to close versus GlazeWM.

Secondary concerns: distribution (users should not need a .NET runtime
installed), memory footprint for a permanently-resident daemon, and whether
NativeAOT is compatible with the Win32 interop surface a WM requires.

We resolved this by measurement rather than argument. The harness is in
`spikes/Shubbak.Spike/`; the runner is `tools/run-p0.ps1`.

## Method

All four spikes are built two ways — framework-dependent JIT and NativeAOT — and
run against both. Every real configuration is paired with a **control group** so
each number has a baseline.

The interop layer is **CsWin32** with `allowMarshaling: false`, which emits
`delegate* unmanaged[Stdcall]` function pointers rather than managed delegates.
Combined with `[UnmanagedCallersOnly]`, the OS calls directly into managed code
with **no marshalling stub**.

Measurements use `QueryPerformanceCounter` throughout, never `Stopwatch`.

GC pressure is deliberately hostile: two allocator threads churning mixed SOH and
LOH allocations with ~1-in-64 promotion, plus a thread issuing
`GC.Collect(2, Forced, blocking: true, compacting: true)` every 250 ms. This is
far worse than anything a real WM would experience.

**Environment:** Windows 10.0.26200, 32 logical cores, .NET SDK 10.0.302,
runtime 10.0.10, workstation concurrent GC.

Raw transcripts:
[`p0-results/p0-20260801-014917.md`](p0-results/p0-20260801-014917.md) (S1-S3, JIT + AOT),
[`p0-results/p0-20260801-020235.md`](p0-results/p0-20260801-020235.md) (full suite incl. S4).

---

## S1 — Low-level keyboard hook latency

**Design under test:** the callback allocates nothing. It writes a 24-byte struct
into a pre-allocated SPSC lock-free ring buffer and returns. A separate worker
thread drains the buffer and does the real work (binding lookup, command
dispatch), where allocation is free.

**Gate:** p99.9 < 5 ms and max < 50 ms, over 1,000,000 injected key events.

| Mode | GC pressure | p50 | p99 | p99.9 | p99.99 | max | ≥300 ms | Ring drops |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| JIT | **on** | 0.0001 | 0.0003 | **0.0008** | 0.1140 | 2.9867 | 0 | 0 |
| JIT | off | 0.0001 | 0.0004 | 0.0011 | 0.0016 | 0.2241 | 0 | 0 |
| AOT | **on** | 0.0001 | 0.0003 | **0.0010** | 0.0323 | 0.7254 | 0 | 0 |
| AOT | off | 0.0001 | 0.0002 | 0.0007 | 0.0014 | 0.0401 | 0 | 0 |

All values in milliseconds. The GC-pressure runs sustained **1,248–1,363 forced
blocking compacting Gen2 collections** and ~190–205 GB of allocation while these
numbers were recorded.

**Result: PASS, by roughly four orders of magnitude.**

- p99.9 of **0.8–1.0 µs** against a 5 ms gate — a ~5,000× margin.
- Worst observed callback across 4 million events: **2.99 ms**, against a 300 ms
  unhook threshold — a ~100× margin.
- Enabling hostile GC pressure did not move p50/p99/p99.9 at all. It only
  perturbed the extreme tail (p99.99, max), and even then stayed 30× inside the
  gate.
- Zero ring-buffer drops, so no key event was ever lost.

**Interpretation.** The hazard we feared is real in principle but does not
materialise, because the callback never allocates and therefore is never itself a
GC suspension point. The GC suspends threads at safepoints; an allocation-free,
call-free callback passes through in well under a microsecond. This is a property
of the *design*, not of the language — and the same design is what a Rust
implementation would use.

---

## S2 — Animation frame timing

**Design under test:** a high-resolution waitable timer
(`CREATE_WAITABLE_TIMER_HIGH_RESOLUTION`) with a ~0.4 ms spin tail for jitter
removal, driving one atomic
`BeginDeferWindowPos` → `DeferWindowPos`×N → `EndDeferWindowPos` transaction per
frame. The tick loop allocates nothing.

**Gate:** dropped frames < 1% and frame-time p99 < 6.94 ms, at 144 Hz for 60 s
(8,640 frames), under GC pressure.

| Mode | Windows | Batched | p50 | p99 | p99.9 | max | Dropped |
| --- | --- | --- | --- | --- | --- | --- | --- |
| JIT | 20 | **yes** | 0.0113 | **0.2592** | 0.9630 | 1.9127 | **0 / 8,640 (0.000%)** |
| AOT | 20 | **yes** | 0.0112 | **0.2621** | 0.8583 | 2.2204 | **0 / 8,640 (0.000%)** |
| AOT | 60 | **yes** | 0.0253 | 0.3189 | 1.6585 | 2.6540 | **0 / 8,640 (0.000%)** |
| JIT | 60 | yes | 0.0280 | 0.3609 | 1.4231 | 2.2125 | 0 / 8,640 (0.000%) |
| JIT | 20 | **no** (control) | 6.3850 | 11.7006 | 13.9528 | 23.4521 | 3,612 / 8,640 (**41.8%**) |
| AOT | 20 | **no** (control) | 6.1446 | 10.1143 | 13.1053 | 17.9663 | 2,810 / 8,640 (**32.5%**) |

**Result: PASS, with ~26× headroom.** p99 of 0.262 ms against a 6.94 ms budget;
zero dropped frames across 34,560 measured frames in the batched configurations.
Tripling the window count to 60 — well beyond any realistic workspace — still
drops zero frames.

### The decisive finding: where the time actually goes

The harness separates managed interpolation math from the Win32 commit call:

| Configuration | Managed | Win32 |
| --- | --- | --- |
| AOT, 20 windows, batched | **4.4%** | 94.6% |
| JIT, 20 windows, batched | 5.3% | 93.0% |
| AOT, 60 windows, batched | **2.5%** | 97.1% |
| AOT, 20 windows, unbatched | 0.0% | 100.0% |

Managed code accounts for **2.5–5.3% of frame time**; the remaining 95%+ is spent
inside `EndDeferWindowPos`, which is cross-process IPC into each target window's
message loop. Managed math at p50 costs **0.9 µs** per frame.

Rewriting in Rust would attack that 2.5–5.3%. Even eliminating managed cost entirely
would improve frame time by under 5%, which is invisible.

### The control group is the real lesson

The unbatched control — naive per-window `SetWindowPos`, the obvious
implementation — **drops 33–42% of frames**, with p50 alone (6.1–6.4 ms) nearly
consuming the entire 6.94 ms budget.

So the thing that determines whether animation is smooth is **`DeferWindowPos`
batching**, not language choice. Both configurations use identical managed code;
only the Win32 call pattern differs, and it is the difference between 0% and 41%
dropped frames. This is very likely the mechanism behind the "feel" gap between
window managers.

### Correction, 2026-10-16: the batched path never ran

The S2 batched numbers above are void, and the conclusion drawn from them was
wrong. `DeferWindowPos` rejects `SWP_NOSENDCHANGING` (and `SWP_ASYNCWINDOWPOS`)
with `ERROR_INVALID_PARAMETER`, whatever the documentation lists as accepted - Wine's
implementation carries the same mask, so this is long-standing behaviour rather
than a build quirk. The spike's `CommitBatched` passed `SWP_NOSENDCHANGING`, got a
null handle back from its first `DeferWindowPos`, and `return`ed. That is how it
"moved" twenty windows in 11 µs with no dropped frames: it moved none of them. The
shipping `WindowCommitter` carried the same flags into the same API, so every batch
it ever built failed on its first window and fell back - silently, because the
fallback existed for resource pressure - to individual `SetWindowPos` calls. The
project ran unbatched from the first day, and nothing noticed, because the
synchronous `SetWindowPos` of the fallback was blocking the tick on each target's
thread and that block was, by accident, pacing the frames.

The spike was also moving windows on its own thread, where a synchronous
`SetWindowPos` is processed inline and an asynchronous one has nothing to post to.
Re-run with the windows on a thread of their own, the flags `DeferWindowPos` does
accept, and a gate that fails the run if any `DeferWindowPos` returns null
(`shubbak-spike s2 --mode async|sync|batched [--target-pause N]`):

| Windows | Target thread | Commit | p50 | p99 | Dropped |
| --- | --- | --- | --- | --- | --- |
| 3 | pumping | posted per window (`SWP_ASYNCWINDOWPOS`) | **0.05** | **1.44** | **0.00%** |
| 3 | pumping | sent per window | 1.31 | 4.09 | 0.00% |
| 3 | pumping | one `DeferWindowPos` transaction | 1.01 | 3.47 | 0.09% |
| 3 | pauses 50 ms between pumps | posted per window | **0.03** | **1.01** | **0.00%** |
| 3 | pauses 50 ms between pumps | sent per window | 57.58 | 182.01 | 92.6% |
| 3 | pauses 50 ms between pumps | one `DeferWindowPos` transaction | 243.70 | 367.82 | 100.0% |
| 20 | pumping | posted per window | **0.65** | **2.76** | **0.00%** |
| 20 | pumping | sent per window | 3.57 | 7.41 | 1.56% |
| 20 | pumping | one `DeferWindowPos` transaction | 1.92 | 5.01 | 0.09% |

(144 Hz, 6.94 ms budget, JIT debug build, no GC pressure, all targets on one thread,
taken on an otherwise idle machine - a first run with a test suite building in the
background doubled every tail and was discarded.)

Three things follow. A working batch does beat sending each window separately when
there are many of them on a responsive thread - it is the middle row of every group -
but it never beats posting, and the daemon moves a median of one window per frame
and at most three in any case. Against a target that is busy, which is what a window
that has just opened always is, anything that waits on the target's thread waits for
the whole of its pause, batch included; the transaction is only as fast as its
slowest window and gets nothing in exchange, since `EndDeferWindowPos` moves each
window on its own thread in turn and offers no visual atomicity across processes.
And posting costs a few hundredths of a millisecond whatever the target is doing. The
committer now posts one `SetWindowPos` per window with `SWP_ASYNCWINDOWPOS` for
every placement, and remembers what it posted so the layout pass is never told a
window is where the window's thread has not yet put it
(`WindowCommitter.CurrentBounds`).

What was right in the original finding survives: Win32 takes 99.9% of frame time in
every configuration, so language choice is not the bottleneck. What determines
whether motion is smooth is whether the window manager's thread ever waits on an
application's, and the answer has to be never.

---

## S3 — NativeAOT viability

| Metric | JIT | **NativeAOT** |
| --- | --- | --- |
| Executable size | 0.15 MB | **1.65 MB** |
| Total deploy size | 101.06 MB | **9.84 MB** |
| Files to ship | 194 | **2** (exe + pdb) |
| Startup, median¹ | 47.5 ms | **14.8 ms** |
| Startup, min | 40.5 ms | **13.1 ms** |
| Working set (idle) | 25.21 MB | **9.96 MB** |
| Requires .NET runtime installed | yes | **no** |
| Trim / AOT analysis warnings | — | **0** |

¹ Measured externally over 20 runs of `shubbak-spike ping` (5 warm-up runs
discarded). Self-measurement was abandoned: reading
`Process.GetCurrentProcess().StartTime` drags in the diagnostics stack and its
cost lands inside the reported number, producing an absurd 879 ms.

**Result: PASS.** A single 1.65 MB self-contained executable, ~15 ms cold start,
under 10 MB resident. **Zero `IL####` trim or AOT analysis warnings** across the
entire CsWin32 interop surface.

For comparison, GlazeWM's binary is in the same order of magnitude, and Zebar's
WebView2-based bar uses 40–80 MB per bar.

---

## S4 — WinEvent hook fidelity and volume

S4 does not affect the language decision. It settles a *Taj architecture*
question: whether `EVENT_OBJECT_NAMECHANGE` fires when a browser tab changes —
a title change with no focus change. This is the exact defect in Zebar, whose
title widget goes stale on tab switches because it listens only to
`EVENT_SYSTEM_FOREGROUND`.

90 s interactive session: browser tab switching (Edge and Firefox), app focus
changes, window drag/resize, minimise/restore.

### Result: PASS. Live titles are available essentially for free.

`EVENT_OBJECT_NAMECHANGE` fired **27 times on the foreground window**, against
only **13 `EVENT_SYSTEM_FOREGROUND` events** in the same session. Captured
transitions include tab switches with no focus change in both browsers (titles
replaced with placeholders; one placeholder is one tab):

```
[NAMECHANGE] <Edge: tab B> - Microsoft Edge
[NAMECHANGE] <Edge: tab C> - Microsoft Edge
[NAMECHANGE] <Firefox: tab C> - Mozilla Firefox
[NAMECHANGE] nvim: <project A>  ->  nvim: <project B>
```

**Zebar's approach misses roughly two thirds of title updates.** Since the WM
already runs a global WinEvent hook, adding `NAMECHANGE` and pushing a
`window.title_changed` event costs nothing. This confirms the design constraint
that Taj must consume WM events rather than doing its own Win32.

### Event volume over 90 s

| Event | Total | `OBJID_WINDOW` | /sec | Signal ratio |
| --- | --- | --- | --- | --- |
| `OBJECT_LOCATIONCHANGE` | 11,025 | 5,192 | 122.5 | 47% |
| `OBJECT_NAMECHANGE` | 422 | **28** | 4.7 | **6.6%** |
| `OBJECT_CREATE` | 76 | 73 | 0.8 | 96% |
| `OBJECT_DESTROY` | 66 | 66 | 0.7 | 100% |
| `OBJECT_HIDE` | 64 | 52 | 0.7 | 81% |
| `OBJECT_SHOW` | 63 | 50 | 0.7 | 79% |
| `SYSTEM_FOREGROUND` | 13 | 13 | 0.1 | 100% |
| `OBJECT_CLOAKED` / `UNCLOAKED` | 14 | 14 | 0.2 | 100% |
| `SYSTEM_MINIMIZE*` | 4 | 4 | 0.0 | 100% |
| **TOTAL** | **11,747** | **5,492** | **130.5** | **46.8%** |

WinEvent callback cost (this implementation takes a lock and allocates strings,
i.e. deliberately not optimised): p50 **0.2 µs**, p99 **2.9 µs**, max 0.52 ms.

### Findings that constrain the implementation

1. **`NAMECHANGE` is 93% noise.** Only 28 of 422 events were `OBJID_WINDOW`; the
   rest are child-object/accessibility chatter. Filtering on
   `idObject == OBJID_WINDOW && idChild == 0` must be the *first* statement in the
   callback, before any string or handle work.
2. **`LOCATIONCHANGE` is the firehose** — 122/s while merely dragging one window,
   and it will include every move the WM itself makes. This is why the animation
   engine needs a generation counter to suppress self-inflicted events (S2's
   feedback-suppression requirement), or the WM will fight itself during every
   animated relayout.
3. **Titles flap during a transition.** Firefox emits a bare `Mozilla Firefox`
   between real titles; Edge briefly shows `New tab and N more pages`. Taj must
   **debounce/coalesce** `title_changed` (~50–100 ms trailing edge) or the bar
   will visibly flicker on every tab switch. Zebar's bug conveniently hides this;
   fixing the bug exposes it.
4. **Foreground events can carry an empty title.** Several `[FOREGROUND]` entries
   have no text (task switcher, desktop, transient shell windows). The WM must not
   assume a focused window has a title, or treat empty as "no window".

Rerun with: `pwsh tools/run-p0.ps1` (or `shubbak-spike s4 --seconds 90`).

---

## Decision

**Build Shubbak in .NET 10, published as NativeAOT, with CsWin32
(`allowMarshaling: false`) for interop.**

Rust and Zig are rejected — not because they would perform worse, but because the
measurements show they would perform *indistinguishably*, while costing
substantially more development time. Zig is additionally rejected for an immature
Win32 binding story.

### Constraints this decision imposes

These are load-bearing. Violating them invalidates the measurements above.

1. **The LL keyboard hook callback must never allocate.** No LINQ, no closures, no
   boxing, no string operations. Write a struct to the ring buffer and return.
   All real work happens on the worker thread.
2. **The animation tick loop must never allocate.** Same rules. Pre-allocate all
   per-frame state.
3. ~~**All window moves in a frame must go through a single `DeferWindowPos`
   transaction.**~~ Withdrawn; see the S2 correction above. `DeferWindowPos` cannot
   take the flags a window manager needs, the batch never ran, and against a busy
   target it is the worst of the three options measured. The constraint that
   replaces it: **the window manager's thread must never wait on an application's
   thread.** Every placement is a posted `SetWindowPos` with `SWP_ASYNCWINDOWPOS`,
   and anything that would wait - `BringWindowToTop`, synchronous `ShowWindow`, a
   sent `SetWindowPos` - is a stall of the animation and of keystroke dispatch for
   exactly as long as the slowest application takes to pump.
4. **Callbacks are `[UnmanagedCallersOnly]` static methods** and must not let an
   exception escape — doing so tears down the process. Wrap in `try`/`catch` and
   log to a pre-allocated buffer.
5. **Keep `Shubbak.Core` free of Win32.** This is what makes the layout/tree logic
   testable, and it is what contains the risk: if a future hot path ever does fail
   in managed code, we can replace that specific layer with a native shim without
   touching the WM's logic.
6. **No reflection-based serialization.** Use source-generated JSON. S3's zero AOT
   warnings is a property we must actively maintain.
7. **UI frameworks stay out of AOT projects.** WinUI3/WPF are AOT-hostile, which
   is a further argument for Taj rendering via Direct2D in its own process.
8. **Filter WinEvents on `OBJID_WINDOW` first.** Per S4, `NAMECHANGE` is 93%
   child-object noise and the stream runs at ~130 events/s. The object-id check
   must precede any string, handle, or lock operation in the callback.
9. **The animation engine must suppress its own `LOCATIONCHANGE` events** via a
   generation counter. S4 measured 122/s from a single dragged window; an animated
   relayout of a full workspace would otherwise feed back into the WM continuously.
10. **Taj must debounce `title_changed`** (~50–100 ms trailing edge). S4 shows
    titles flap through intermediate states during a tab switch. Zebar's bug hides
    this; fixing it exposes it.

### Note on fidelity

S2 animates borderless `WS_POPUP` test windows with no content. Real
applications — browsers, Electron apps — are heavier to move, because the cost
of a sent move scales with what each target window does in response to
`WM_WINDOWPOSCHANGED`. The original run also had those windows on the spike's own
thread, where none of that cost exists at all; the correction above puts them on a
thread of their own and makes that thread as slow as the spike is told to. Even so,
the true figure against a real application will be worse than measured here. This
does not change the decision, since the cost is in Win32 and would be identical in
any language — it just means the animation engine's numbers have to come from the
shipping daemon's own `diagnose` report, which is where they now come from.

## Consequences

**Positive**

- Single 1.65 MB executable, no runtime prerequisite, ~15 ms start, <10 MB RSS.
- Fast iteration, strong tooling, and a first-class unit-testing story for the
  layout engine — an area where GlazeWM and komorebi are both weak.
- CsWin32 generates interop from the same Win32 metadata as Rust's `windows`
  crate, so binding ergonomics are equivalent.
- S4 confirms live window titles are available from the WM's existing hook, so
  Taj can ship a window-title widget that is strictly better than Zebar's without
  any extra machinery.

**Negative**

- Two hot paths carry hand-enforced no-allocation discipline. This needs to be
  documented at the call site and ideally guarded by an allocation-counting test.
- NativeAOT constrains library choice permanently (no reflection-heavy
  dependencies).
- We are off the beaten path: no existing .NET tiling WM to borrow from.

**Revisit if**

- Real-window animation benchmarks during P3 show <2× headroom at 144 Hz.
- Any `WH_KEYBOARD_LL` callback is observed above 50 ms in the field.
- A required dependency turns out to be AOT-incompatible.

The fallback is *not* a rewrite. It is a small native shim behind the
`Shubbak.Native` boundary, with `Shubbak.Core` untouched — which is exactly what
constraint 5 exists to preserve.

---

## Addendum: the IPC pipe is a boundary

Decided 2026-08-04, after a review pointed out that `shell-exec` was reachable
from the pipe and asked for a decision rather than a patch.

**The pipe is a trust boundary, not a trusted channel.**

`shell-exec` exists so a keybinding, a rule or a startup command can launch a
terminal. Those are decisions the user writes into their config deliberately.
Nothing about that requires the same capability to be reachable at runtime by
any process that happens to be able to open the pipe.

The pipe is `PipeOptions.CurrentUserOnly`, which scopes it to the *account* and
not to the *integrity level*. Shubbak tells users to run elevated in order to
manage windows belonging to elevated processes, so an ordinary medium-integrity
process running as the same user - a browser child, a postinstall script - could
open the pipe of an elevated daemon and have it start a high-integrity process.
That is an escalation available to anything already running as the user.

The escalation was reasoned from the documented semantics of `CurrentUserOnly`
rather than demonstrated with a test client. The decision does not depend on it:
even without elevation, a window manager accepting arbitrary process launches
over a local pipe is a capability nobody asked it to have.

**What was done.** `ShellExecCommand` is refused on the IPC path unless
`general { allow-shell-exec-over-ipc #true }`. Keybindings, rules and startup
commands are untouched. The refusal names the key, so anyone driving Shubbak as
a launcher on purpose is told how rather than left guessing.

**What was rejected.** An explicit `PipeSecurity` carrying a High mandatory
integrity label when elevated is more correct and more work, and still leaves
the same-integrity case open. It remains the right answer if the pipe ever
carries anything else worth protecting.