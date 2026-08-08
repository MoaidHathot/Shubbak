# Analysis 2

Findings from a review of the tree at `08ec686`, none of them acted on yet.
Companion to [analysis.md](analysis.md), which was written at `fcd7839`, and to
[further-improvements.md](further-improvements.md). Written for the same reason
as both: so the reasoning survives the conversation it came from.

The review was asked to look hardest at two things — **animation** and
**keyboard handling** — and the document is ordered accordingly. Everything here
cites a file and a line. Where a claim was inferred rather than measured, the
last section says so.

## What the previous review retired

Four commits sit between `fcd7839` and here, and between them they closed most
of what [analysis.md](analysis.md) opened with. Recorded so that document can be
read without re-litigating settled ground:

| Finding | Now |
|---|---|
| `tests/Shubbak.Wm.Tests` not in the solution | fixed — `Shubbak.slnx`, and CI runs the solution |
| `Thread.Sleep(8)` giving ~64 ticks/s | fixed — `MsgWaitForMultipleObjectsEx` plus `timeBeginPeriod`, `MessageLoop.cs:134-151`, `:194-219` |
| Backpressure clearing the outbox in silence | fixed — `IpcServer.cs` |
| No protocol version | fixed — `IpcProtocol.cs:143` |
| Config sections and keys accepted in silence | fixed — `ConfigLoader.cs:107-125`, SHB0427/SHB0428 |
| `--help` performing the action | fixed — `Cli/Program.cs` |
| No CI | fixed — `.github/workflows/build.yml` |

Two are unchanged and are restated below with current line numbers, because a
finding that survives a round of fixes is worth more than one that has only been
made once: `wm-toggle-pause` still does nothing, and `MessageLoop`'s new frame
interval turns out not to be honoured for a reason nothing in that work touched.

---

## The animation loop paces itself against its own echo

This is the largest finding in the review, and it is a one-line fix.

`WinEventSource` subscribes to `EVENT_OBJECT_LOCATIONCHANGE`
(`WinEventSource.cs:64`). Every such event takes a lock (`:163`), enqueues
(`:171`), and signals the message pump (`:174`). The daemon then throws it away.
`WmDaemon.cs:583-587` is an empty `break`, and its comment is unambiguous about
why:

> The firehose: S4 measured 122/s from a single dragged window, and every move
> we make ourselves echoes back here. Ignored entirely - MoveSizeEnd is the
> event that actually carries intent.

The decision to ignore it is right. The problem is that we are still subscribed,
and the sentence "every move we make ourselves echoes back here" describes a
feedback loop rather than a nuisance.

### Why it is a loop rather than a cost

`WINEVENT_SKIPOWNPROCESS` (`WinEventSource.cs:89`) skips events for windows
belonging to *this* process. Every managed window belongs to some other process,
so it does not apply to any of them. Each `DeferWindowPos` the animation path
issues therefore produces a `LOCATIONCHANGE` that comes straight back to us.

The pump waits with `MsgWaitForMultipleObjectsEx(..., QS_ALLINPUT,
MWMO_INPUTAVAILABLE)` (`MessageLoop.cs:145-150`). Both a queued message and a
signalled event end the wait. So:

1. `AdvanceAnimation` commits a frame for N windows (`WmDaemon.cs:1872`).
2. N `LOCATIONCHANGE` callbacks arrive, each taking a lock, queueing, and
   calling `_loop.Wake` (`WmDaemon.cs:184` wires `WorkQueued = _loop.Wake`).
3. The pump wakes immediately rather than waiting out its 7 ms.
4. Another frame is committed. Return to 1.

`FrameInterval` (`WmDaemon.cs:271`) is documented as "Roughly 144 Hz, the rate
ADR 0001 gates the animation path on". In practice it is a **ceiling that is
never reached**: while animating, the loop free-runs at whatever rate
`EndDeferWindowPos` permits, and each pass is a full `BeginDeferWindowPos` /
`DeferWindowPos` × N / `EndDeferWindowPos` transaction — the work ADR 0001
measured at 94.6% of frame time
(`docs/adr/0001-language-choice.md:133-142`). The loop is self-limiting only by
the cost of the work it is doing, which is the definition of a spin.

It also costs on the drag path, where the same firehose is 122 events per second
per dragged window (`WinEventSource.cs:165-168`), each waking a pump that will
discard it.

### The fix, and why the obvious half of it does not work

Delete `WinEventSource.cs:64`. `Start` installs one hook per subscription
(`:85-92`), so removing the entry removes exactly one hook and the entire
stream, and nothing downstream loses anything — the only consumer is the empty
`break`.

**Removing only the `WorkQueued?.Invoke()` would not help.** `WINEVENT_OUTOFCONTEXT`
callbacks are delivered through the hooking thread's *message queue*, and the
wait uses `QS_ALLINPUT`. The arrival of the callback ends the wait whether or not
anything signals the event. The subscription is what has to go.

Worth doing at the same time: `WindowCommitter.IsSelfInflicted`
(`WindowCommitter.cs:130-145`) exists to suppress exactly these echoes and has
**zero callers** anywhere in `src` or `tests`. The `_driving` set it reads is
nevertheless maintained under a lock on every animation frame
(`WindowCommitter.cs:624-647`, adds at `:645`, removes at `:634`). Once the
subscription is gone, both are dead and can follow it.

---

## A frame that consumes the whole animation

`OnTick` computes the raw wall-clock gap since the previous tick and passes it
straight to the engine (`WmDaemon.cs:289-293`, then `:313`):

```csharp
long now = Stopwatch.GetTimestamp();
double deltaMs = _lastTickTicks == 0
    ? 0
    : (now - _lastTickTicks) * 1000.0 / Stopwatch.Frequency;
_lastTickTicks = now;
```

Nothing clamps it, here or in `AnimationEngine.Tick`, which adds it to
`track.Elapsed` unconditionally (`AnimationEngine.cs:221`).

The idle wait is 250 ms (`WmDaemon.cs:281`), and it is right that it is — the
comment at `:273-280` explains that a monitor being unplugged signals nothing.
But consider the ordering within a single tick:

1. The pump has been idle. The wait ends after up to 250 ms, so `deltaMs` is up
   to 250.
2. `ApplyLayout` runs (`:309`) and calls `Retarget`, which creates the track
   with `Elapsed = 0` (`AnimationEngine.cs:195`).
3. **On the same tick**, `AdvanceAnimation(deltaMs)` runs (`:313`) and adds up
   to 250 ms of elapsed time to a track that is one line old.

`WindowMove` is 140 ms by default (`AnimationEngine.cs:66-67`). `progress` hits
1 on the first frame (`AnimationEngine.cs:223`), `Interpolate` is skipped
entirely for `track.To`, and the window teleports.

The failure is not uniform, which is what makes it hard to report. It fires when
the tick that starts the animation follows a long wait — which is to say, on the
**first action after the desktop has been idle**, and almost never during a burst
of activity. From the outside that reads as "the animations are flaky", or "it
animates when I'm using it and doesn't when I come back to it", neither of which
points at a delta.

Clamping what is handed to the engine — `Math.Min(deltaMs, FrameInterval * 2)`
or similar — is one line and is what every fixed-step loop does for exactly this
reason. Starting tracks on the following tick would also work and is more
invasive.

`_tickInterval.Record(deltaMs)` at `:295` should keep the unclamped figure: the
distribution of real wait times is a diagnostic, and losing it would hide the
next version of this problem.

---

## Raising a window it decided to animate

`Placement.Raise` is honoured in exactly one place: inside
`WindowCommitter.Commit`, which collects handles at `:299` and calls `RaiseAll`
at `:331` and `:357`. `RaiseAll` is deliberately outside the
`DeferWindowPos` batch, and `:362-375` explains why.

`ApplyLayout` never gets there for an animated window:

```csharp
// WmDaemon.cs:1694-1700
if (_animation.Retarget(placement.Window.Handle, current, placement.Rect, kind))
{
    // Animated: the tick loop drives the geometry from here.
    continue;
}

_commitScratch.Add(placement);
```

`CommitFrame` — the path an animated window actually takes
(`WindowCommitter.cs:584-649`) — has no notion of `Raise` at all.

What that costs is not cosmetic. `LayoutEngine` sets `Raise: true` in two
places, and `LayoutEngine.cs:16-26` documents it as the *entire* visible effect
in one of them:

- `LayoutEngine.cs:220` — every non-tiled window, which is fullscreen and
  maximised.
- `LayoutEngine.cs:191` — the focused window when the layout overlaps, which is
  monocle.

So with animation on, which is the default (`AnimationEngine.cs:36`), **entering
fullscreen or monocle does not raise the window** whenever the rectangle also
moves. It works only when the move is zero or below `MinimumAnimatedDistance`,
because those are the cases `Retarget` returns false for
(`AnimationEngine.cs:175-187`) and the placement falls through to `Commit`.

The obvious fix — raise before starting the animation — is also the correct one:
a window that is about to travel to the front should be in front while it
travels, not after.

---

## Curves nobody reads

### Two of the four animation kinds are never constructed

`AnimationKind` has four members (`AnimationEngine.cs:11-24`), `ProfileFor` maps
all four (`:88-95`), `AnimationOptions` carries a tunable profile for each
(`:63-73`), and `ConfigLoader` parses all four keys
(`ConfigLoader.cs:369-372`).

Grep for `AnimationKind.` across `src` returns six lines. Four are the
`ProfileFor` switch arms. The other two are the only construction sites in the
program:

```
WmDaemon.cs:1669:  AnimationKind kind = current.IsEmpty ? AnimationKind.WindowOpen : AnimationKind.WindowMove;
WmDaemon.cs:1691:  kind = AnimationKind.WindowOpen;
```

`LayoutChange` and `WorkspaceSwitch` are never passed to `Retarget` by anything.
Both are nevertheless advertised in the config that everyone starts from —
`docs/shubbak.example.kdl:105-106`:

```kdl
layout-change    duration=180 curve="ease-out"
workspace-switch duration=120 curve="ease-out"
```

Setting either has no observable effect, and nothing says so. This is precisely
the class of silent failure the config loader was built to eliminate, occurring
in the loader's own accepted output — and the loader now warns about a *misspelt*
key while accepting a correctly spelt one that does nothing.

Both have obvious call sites. A layout command reaching `ApplyLayout` is a
layout change; a workspace activation is a workspace switch. The daemon knows
which it is by the time it reaches `:1669` — it simply does not carry the
information that far.

### `cubic-bezier` cannot be written in a config

`Easing.cs:14-16` states the reason the type uses CSS control points:

> Cubic bezier control points use the CSS convention, so curves can be copied
> straight from any easing reference or design tool.

`Easing.CubicBezier` (`:47-48`) has **zero callers** in `src` or `tests`.
`ConfigLoader.Profile` resolves `curve=` through `Easing.TryParse`
(`ConfigLoader.cs:397`), which handles the six named curves (`Easing.cs:54-59`)
and nothing else. The documented reason for the design decision is unreachable
by the people it was made for.

### Turning animation off leaves it on

`LoadConfig` swaps the options wholesale (`WmDaemon.cs:2198`):

```csharp
_animation.Options = _config.Animation;
```

`AnimationEngine.Tick` (`:209-248`) never consults `Options.Enabled`; it is read
only in `Retarget` (`:183`). So a reload that sets `enabled #false` stops *new*
animations and lets every in-flight track run to completion.

`AnimationEngine.Clear()` exists for this — its summary is "Stops everything,
e.g. when animation is turned off" (`:273-278`) — and is called by nothing.

---

## The timer is raised for the wrong reason

`NextTimeout` decides both the wait and whether to hold the 1 ms system timer
(`WmDaemon.cs:257-268`):

```csharp
if (_animation.IsAnimating || _layoutDirty)
{
    _timerResolution.Acquire();
    return FrameInterval;
}
```

`TimerResolution` is documented as being held "only while something is actually
moving", and `MessageLoop.cs:186-192` explains the cost of holding it longer —
process-wide, defeating timer coalescing and the deeper idle states.

`_layoutDirty` is not "something is moving". It is set by almost everything:
`Publish` sets it unconditionally for any result carrying events
(`WmDaemon.cs:2438`), as do `Execute` (`:1231`), `RunCommand` (`:1378`),
`TryManage` (`:873`), `TryUnmanage` (`:901`), `SyncMonitors` (`:2120`) and every
branch of `HandleUserMove`. With `animation { enabled #false }` in the config,
the daemon still calls `timeBeginPeriod(1)` on every dirty tick — for a feature
the user has turned off.

The two questions are genuinely different and want separating: `IsAnimating`
decides the timer, and `IsAnimating || _layoutDirty` decides the wait.

### Nothing is gated on anything

There is no gating of animation of any kind. Searched across `src` and found
absent: `SPI_GETCLIENTAREAANIMATION`, `SystemParametersInfo`,
`SM_REMOTESESSION`, `GetSystemMetrics`, `DwmFlush`,
`DwmGetCompositionTimingInfo`, any power API, and any refresh-rate query —
`MonitorInfo` (`MonitorSource.cs:21-26`) carries `DeviceId`, `Bounds`,
`WorkArea`, `Dpi` and `IsPrimary`, and no refresh rate.

Three consequences worth naming separately:

- **Windows' own animation setting is ignored.** A user who has turned off "Show
  animations in Windows" has already answered this question, in the place the
  operating system asks it. Reading it is one call, and honouring an
  accessibility setting the user has explicitly set is not a preference.
- **`FrameInterval` is 7 ms regardless of the panel.** On 60 Hz that is
  something over two frames of work for every frame displayed; on 240 Hz it is
  under half the available rate. The value is a `static readonly` with no config
  key (`WmDaemon.cs:271`).
- **Remote desktop is not detected.** Every animation frame over RDP is a
  screen-region update on the wire.

---

## What the animation path is not tested for

`tests/Shubbak.Core.Tests/AnimationEngineTests.cs` is good: 16 tests, all
driving `Tick` with a caller-supplied delta, so entirely deterministic. It
covers monotonicity, retarget blending, the negligible-distance short circuit,
compaction, the final-frame flag, `ease-out-back` overshoot being clamped, and
every named curve being anchored at both ends.

What is not covered is everything outside the engine.

**`WindowCommitter.CommitFrame` has no tests at all.** Grep across `tests` finds
`Commit(`, `Revive` and `IsConcealed`, and no call to `CommitFrame` anywhere.
ADR 0001 calls the batching in it "the highest-leverage implementation detail in
the entire project" (`docs/adr/0001-language-choice.md:280-281`) on the strength
of a control group that dropped 33–42% of frames without it. It is untested in
the shipping codebase.

Its failure path deserves a test of its own. A single `DeferWindowPos` returning
null — a window destroyed mid-animation, which `CommitFrame` never guards
against, unlike `Commit` at `WindowCommitter.cs:278` — invalidates the whole
`HDWP` and drops the entire frame to N individual `SetWindowPos` calls
(`:613-621`). That is the unbatched control group, reached silently, at the
moment a window closes.

**The tick path allocates above 128 windows.** `WmDaemon.cs:129-132` states the
ADR 0001 constraint 2 rationale for the pre-allocated scratch buffer, and
`:1864-1865` reallocates it:

```csharp
if (_frameScratch.Length < _animation.ActiveCount)
    _frameScratch = new AnimationFrame[Math.Max(_animation.ActiveCount * 2, 128)];
```

Rare, amortised, and still a literal violation of the constraint the three lines
above it cite. ADR 0001 asks for an allocation-counting test here
(`docs/adr/0001-language-choice.md:329-330`); `LogAllocationTests` shows the
pattern already exists for the logger.

Also missing: any test that a long delta cannot collapse an animation, and any
test that `Raise` survives being animated.

**`LogCategory.Animation` is declared and never emitted.** `LogLevel.cs:56-57`
declares it, and `:35` names it in the guidance as where to look when motion
stutters. Nothing writes to it. The only runtime visibility into animation is
`- **Animating**: {count}` in the diagnostic report (`WmDaemon.cs:923`), which is
an instantaneous gauge.

---

## AltGr, and a config that ships the trap

On most European layouts, AltGr is not a key. Pressing it generates
**LeftControl followed by RightAlt** — the control is synthetic, and Windows
injects it.

`ReadModifiers` reads the merged virtual keys (`KeyboardSource.cs:327-337`):

```csharp
if (IsHeld(VIRTUAL_KEY.VK_MENU)) modifiers |= KeyModifiers.Alt;
if (IsHeld(VIRTUAL_KEY.VK_CONTROL)) modifiers |= KeyModifiers.Control;
```

`VK_MENU` and `VK_CONTROL` do not distinguish left from right, so AltGr reports
`Control | Alt`. Any binding written `alt+ctrl+X` therefore matches AltGr+X, and
the hook swallows it (`KeyboardSource.cs:293-300`).

The shipped example config contains two:

```
docs/shubbak.example.kdl:282:    bind "alt+ctrl+t"  { tag --clear }
docs/shubbak.example.kdl:287:    bind "alt+ctrl+n" { scratchpad --name terminal }
```

On a German, French, Polish, Spanish or Nordic layout, a user who starts from
the shipped config loses AltGr+T and AltGr+N **in every application on the
machine**. The characters simply do not appear.

This is the same shape as the bug
[further-improvements.md](further-improvements.md) opens with, and the
description there applies unchanged:

> The failure mode is unusually cruel. Nothing about the symptom points at a
> window manager.

### The fix, and what is already in place for it

The standard approach is to detect the synthetic control. Windows marks the
injected LeftControl, and `KeyEvent` **already carries that flag**:
`KeyboardSource.cs:289` reads `LLKHF_INJECTED` into `IsInjected`. Grep for
`IsInjected` across the repository returns three lines — the doc comment
(`:25`), the record parameter (`:30`), and that assignment. Nothing has ever
read it.

Wiring it has a second benefit worth deciding on deliberately rather than
inheriting: today any process that calls `SendInput` can drive Shubbak's
bindings, because injected keystrokes are indistinguishable from typed ones all
the way through.

Distinguishing left from right — the more complete fix, since it also makes
`ralt` bindable — is a larger change and is covered under the key vocabulary
below.

---

## `wm-toggle-pause`, one review later

Restated from [analysis.md](analysis.md) with current line numbers, because
nothing has changed and the shape of the problem is worth keeping visible.

`KeyboardSource.Suspended` (`KeyboardSource.cs:116-128`) exists to back this
feature. Its own doc says so:

> Backs `wm-toggle-pause`. Suspending rather than unhooking matters: the binding
> that resumes has to keep working, and re-installing a hook later can fail if
> the desktop has changed.

It is read in the hook callback (`:259`) and **assigned by nobody**. A
case-insensitive grep across `src` and `tests` returns the field declaration
(`:101`), the property (`:124-128`) and that one read.

The command reaches `WindowManager.SetPaused` (`WindowManager.cs:1368-1372`)
from `CommandExecutor.cs:134`, which sets `IsPaused` (`WindowManager.cs:119`).
`IsPaused` is read in two places, both cosmetic: the diagnostic report
(`WmDaemon.cs:922`) and the IPC state snapshot (`StateProjection.cs:66`).

`DrainKeyboard` (`WmDaemon.cs:332-362`) does not check it. `HandleWindowEvent`
does not check it. So the command reports success, the bar shows a paused
indicator, and every keybinding and window event continues to work.

The bar displaying a state the window manager is not in is worse than the
command not existing. Three options, unchanged: wire `Suspended` and gate the
event handler, remove the command, or — the one nobody would choose —
leave it.

---

## A reload that loses the mode, and cannot get it back

`BindingTable.Load` rebuilds the tables and drops the active mode
(`BindingTable.cs:55-57`):

```csharp
_default = defaults;
_modes = modes;
_activeMode = null;
```

Nothing tells the state machine. `WindowManager.BindingMode`
(`WindowManager.cs:113`) keeps whatever it had. So after
`wm-reload-config` while in a mode called `pause`:

- the lookup table is back on the default bindings, which is the safe half;
- `WindowManager.BindingMode` still says `pause`;
- `diagnose` still says `pause` (`WmDaemon.cs:921`);
- the bar still says `pause` (`StateProjection.cs:65` → `Taj/WmConnection.cs:287`).

Three surfaces reporting a mode the keyboard is not in.

It gets worse when the user tries to fix it the obvious way.
`SetBindingMode` short-circuits on an unchanged name
(`WindowManager.cs:1358-1365`):

```csharp
if (string.Equals(BindingMode, mode, StringComparison.Ordinal)) return Complete();
```

Pressing the key that enables `pause` finds `BindingMode` already `"pause"`,
emits no `BindingModeChanged`, so `Publish` never reaches
`_bindings.SetMode` (`WmDaemon.cs:2408-2412`), so the table never changes. **The
mode cannot be entered again** until the user first runs
`wm-disable-binding-mode`, which appears to do nothing and is the thing that
fixes it.

`Load` should either restore the mode it found active or tell the state machine
it did not. The second is more honest, and the reload path already has a
`BindingModeChanged` to emit.

### The same seam, one step earlier

`BindingTable.SetMode` returns false for a name it does not know
(`BindingTable.cs:69`). `WmDaemon.cs:2410` discards the result.

So `wm-enable-binding-mode --name typo` sets `WindowManager.BindingMode` to
`"typo"`, logs `binding mode 'typo' active` through `ReportBindingMode`'s
`declared is null` branch (`WmDaemon.cs:1346-1350`), leaves the lookup table on
the defaults — and reports success. Three components, three different beliefs.

There is also no load-time validation that a name in `wm-enable-binding-mode`
matches a declared mode, which is where this should be caught. The loader
already does harder versions of this check: SHB0425 refuses a swallowing mode
with no way out (`ConfigLoader.cs:753-761`), which is a far more sophisticated
piece of reasoning than comparing a string against a list of declared names.

### A note on the memory model

`_default`, `_modes` and `_activeMode` (`BindingTable.cs:27-32`) are plain
fields, written from the daemon thread by `Load` (`WmDaemon.cs:2177`, `:2201`)
and `SetMode` (`:2410`), and read from the hook thread by `IsBound`
(`KeyboardSource.cs:291`).

This is benign in practice and unsound in principle. The writes are
reference-sized, so no torn read is possible, and the dictionaries are never
mutated after publication, so a stale read costs at most one keystroke resolved
against the previous config. But there is no release fence on the write and no
acquire on the read; it works because of the hardware model rather than the
language one. `IsBound` and `Resolve` both read `_activeMode` into a local
(`:92`, `:129`), which is the one deliberate defensive touch in the file.

`further-improvements.md:157-160` already proposes the change that would close
this properly — a frozen, immutable table swapped by reference — for a different
reason. It would close this too.

---

## A flag that outlives its keystroke

`_swallowed` is a 256-entry array recording which keys had their press
swallowed, so the matching release can be swallowed too
(`KeyboardSource.cs:74-81`). The reasoning at `:272-274` is correct and the
mechanism is right:

> A key-up must be swallowed if its key-down was, or the application is left
> believing the key is still held.

It is written only from the hook thread, needs no synchronisation, and is
**never bulk-reset**. There is no `Array.Clear` anywhere, and neither
`BindingTable.Load` (`:37-58`), `SetMode` (`:61-73`) nor
`WmDaemon.LoadConfig` (`:2172-2229`) touches `_keyboard` at all.

Most paths are safe. Auto-repeat re-sets an already-set flag and one release
clears it. Starting the daemon with a key held is fine, because the array starts
false and the release takes the pass-through branch (`:282`).

The leak is a **missed release while the flag is set**. A low-level hook does not
receive input on the secure desktop, so Ctrl+Alt+Del or a UAC prompt landing
between the press and the release strands the flag. So does a foreground process
at a higher integrity level taking the release, and so does Windows unhooking us
for exceeding `LowLevelHooksTimeout` (`:37-40`).

The symptom arrives later and looks unrelated. The next time that key is pressed
while *unbound* — after a mode change, after a reload that removed the binding,
after leaving a swallowing mode — the press passes through to the application
and the release is swallowed at `:276-280`. The application sees a key that went
down and never came up: stuck, and auto-repeating. The flag then clears, so it
happens once and does not reproduce.

Clearing the array on mode change, on config reload, and on hook reinstall is a
few lines and removes the whole class. `Array.Clear` on 256 bytes is not a cost
that needs discussing.

### One inefficiency in the same callback

`ReadModifiers()` runs at `:268`, before the `if (!isKeyDown)` branch at `:274`.
Every key-up therefore performs four `GetAsyncKeyState` calls whose result is
discarded. Roughly half of all events pay it for nothing. This is not a
correctness problem and the callback is measured at well under a microsecond,
but the file's own rule is that nothing in it should do work it does not need
to, and moving one line satisfies it.

---

## Every repeat is a command

Windows delivers auto-repeat as repeated `WM_KEYDOWN` with no intervening
`WM_KEYUP`. The callback treats each one as a fresh press: it probes (`:291`),
enqueues (`:295`) and re-marks the swallow flag (`:296`). `DrainKeyboard` then
resolves and executes each one (`WmDaemon.cs:338-361`).

There is no repeat flag, no debounce, no coalescing and no rate limit anywhere
in the path. `KeyEvent` (`KeyboardSource.cs:26-30`) carries no timestamp and no
repeat bit, and `KBDLLHOOKSTRUCT.time` is never read.

Holding `alt+h` therefore fires `focus --direction left` at the hardware repeat
rate. For `focus` and `resize` that is exactly what the user wants and is why
holding the key feels right. For others it is not: `close`, `wm-exit`,
`toggle-floating` and `toggle-fullscreen` all repeat, and one of them repeating
is unrecoverable.

Repeat also compounds a finding from the previous review. Holding a focus key
against the leftmost window produces a `CommandRejected` per repeat, and
`Publish` sets `_layoutDirty` unconditionally (`WmDaemon.cs:2438`), so each one
forces a full layout pass.

### There is no way to ask for the other behaviour

`ParseBinding` (`ConfigLoader.cs:619-649`) reads `node.Argument(0)` for the key
and the child block for the commands. It reads **no properties at all**, and
`WarnAboutUnknown` is applied to top-level sections and to the children of
`general`, `gaps`, `window-effects`, `animation` and `logging`
(`ConfigLoader.cs:86`, `:176`, `:311`, `:340`, `:359`, `:421`) — never to `bind`
nodes.

So the natural thing to write:

```kdl
bind "alt+q" repeat=#false { close }
```

parses cleanly, produces no diagnostic, and is silently ignored. Warning on
unrecognised properties of a `bind` node is worth doing regardless of whether
`repeat` is ever implemented, and is the same generic pass the loader already
applies five levels up.

A sensible default would be per-command rather than global — movement and
resizing repeat, everything else does not — with the property available to
override it either way.

---

## The keys you cannot bind

`KeyParser` accepts four modifiers (`KeyParser.cs:104-107`), 28 named keys
(`:21-59`), F1–F24 (`:190-196`), letters, digits, and eleven punctuation
characters (`:62-75`). Everything else is `SHB0203 Unknown key` (`:120-128`).

Absent, by category:

| Category | Missing |
|---|---|
| Numpad | `VK_NUMPAD0-9`, `VK_MULTIPLY`, `VK_ADD`, `VK_SUBTRACT`, `VK_DECIMAL`, `VK_DIVIDE`, `VK_NUMLOCK` |
| Media | mute, volume up/down, next/previous track, stop, play/pause |
| Browser | all seven of `VK_BROWSER_BACK` … `VK_BROWSER_HOME` |
| Locks and system | `VK_SNAPSHOT`, `VK_CAPITAL`, `VK_SCROLL`, `VK_PAUSE`, `VK_APPS` |
| ISO layouts | `VK_OEM_102` — the key beside left shift on every non-US physical keyboard — and `VK_OEM_8` |
| Left/right modifiers | `lalt`, `ralt`, `lshift`, `rshift`, `lctrl`, `rctrl`, `lwin`, `rwin` |

The numpad omission is the one that will be reported first: a numpad is the
obvious hardware for nineteen workspaces, and `bind "alt+numpad1"`,
`"alt+kp1"` and `"alt+num1"` all fail.

### Left and right cannot be added to the parser alone

`KeyModifiers` has four flags (`KeyboardSource.cs:11-19`) and `ReadModifiers`
queries the merged `VK_MENU`, `VK_CONTROL` and `VK_SHIFT` (`:331-333`), ORing
`VK_LWIN` and `VK_RWIN` into one bit (`:334`). Even if the parser accepted
`ralt`, nothing downstream could tell. It needs the flags widened and
`ReadModifiers` reworked to read the left/right virtual keys — which is also
the complete fix for AltGr, since it makes `ctrl+alt` and `ralt` distinguishable
at the point of comparison rather than requiring the injected-flag heuristic.

### Punctuation is bound to a US layout

`s_punctuation` (`KeyParser.cs:62-75`) hardcodes the US OEM assignments: `;` is
`0xBA`, `[` is `0xDB`, and so on. Those virtual keys sit under different
physical keys on other layouts, so `bind "alt+;"` binds a different key
depending on where the user lives, with nothing in the diagnostics to say so.

The same applies to letters, in the other direction: `bind "alt+a"` binds
`0x41`, which is the physical **Q** on AZERTY. Whether that is right depends on
whether the user means "the key labelled A" or "the key where A is on QWERTY",
and there is currently no way to express either — the behaviour is positional by
accident rather than by decision.

`MapVirtualKey` is listed in the CsWin32 manifest
(`src/Shubbak.Native/NativeMethods.txt:114-115`) and never called. The generated
binding for the thing that would answer this question already exists.

### There is no `KeyParserTests.cs`

No file in `tests` calls `KeyParser.TryParse` directly. SHB0201 (empty),
SHB0202 (two non-modifier keys), SHB0203 (unknown key) and SHB0204 (modifiers
with no key) are never asserted. `SplitParts` (`:155-179`) is the trickiest code
in the file — it exists specifically so `alt++` and `alt+-` work, and
`:150-154` records that the second appears in the author's own config — and it is
covered only sideways, through
`tests/Shubbak.Config.Tests/PunctuationWorkspaceBindingTests.cs`, which asserts
consequences rather than the mapping.

For a parser whose failure mode is a binding that silently binds the wrong
physical key, that is the wrong place to have no tests.

---

## Settings that parse and do nothing

Three settings in `general` are read, stored, and never consulted:

| Setting | Parsed | Stored | Read by |
|---|---|---|---|
| `focus-follows-cursor` | `ConfigLoader.cs:184` | `ShubbakConfig.cs:83` | nothing |
| `cursor-jump` on monitor focus | `ConfigLoader.cs:187` | `ShubbakConfig.cs:86` | nothing |
| `cursor-jump` on window focus | `ConfigLoader.cs:188` | `ShubbakConfig.cs:89` | nothing |

`ToWmOptions()` (`ShubbakConfig.cs:171-185`) projects `OuterGap`, `InnerGap`,
`InitialWindowState`, `ToggleWorkspaceOnRefocus`, `FollowWindowOnMove` and
`DefaultLayout`. None of the three appear, and nothing else in `src` reads the
properties. The only test is `ConfigLoaderTests.cs:33`, which asserts the parsed
value rather than any behaviour.

`focus-follows-cursor` cannot work as the program is built. There is no
`WH_MOUSE_LL` hook anywhere — grep across `Shubbak.*` finds no mouse hook, no
`mouse_event`, and no window-procedure mouse handling outside the bar's own
`BarWindow.cs`. Mouse-driven behaviour comes entirely from
`EVENT_SYSTEM_MOVESIZESTART` / `END` (`WinEventSource.cs:70-71`), which fire
only around a drag. `cursor-jump` is closer — it needs `SetCursorPos`, which is
not in the manifest — but it is the same shape of promise.

**Decision recorded:** delete all three from the schema, rather than build a
mouse hook to satisfy them. The reasoning, in the style of
[further-improvements.md](further-improvements.md)'s "deliberately not done":

A second low-level input hook is not a small addition. It is a new callback on a
new latency-critical path, running for every mouse movement on the machine —
more events than the keyboard hook sees by a wide margin — for a feature that is
a convenience. The keyboard hook earned its complexity because keybindings are
the entire interface; `focus-follows-cursor` has not made that case, and the
project has already paid once for putting an input hook somewhere it could
affect the whole system.

Deleting them costs three lines of the example config and makes
`check-config` honest. Should the case for a mouse hook be made later, the
settings can come back with an implementation attached.

The animation keys are the opposite decision, and are recorded that way: wire
`layout-change` and `workspace-switch` to their call sites. The profiles exist,
the engine supports them, the call sites are identifiable, and the example
config already documents behaviour the user is entitled to expect.

---

## Smaller things

- **`pass-through` as a child node emits a spurious warning.**
  `ParseBindingModes` calls `CollectBindings(child, ...)` first
  (`ConfigLoader.cs:744`), whose `default:` arm warns SHB0404 "Unexpected
  'pass-through' inside keybindings" (`:557-562`), and only then reads it as a
  setting (`:747`). The form is supported and documented, and
  `tests/Shubbak.Config.Tests/SettingFormTests.cs:35` covers it — but asserts
  the parsed value, not the absence of warnings, so the noise goes unnoticed.

- **`WindowActions.IsKeyDown` is dead** (`WindowActions.cs:104-106`). Declared,
  never called.

- **The README's test count is stale again.** It says 635 in two places
  (`README.md:23`, `:198`); the tree declares 657 `[Fact]` and `[Theory]`
  methods. The previous review found the same figure wrong at 459, which
  suggests the number wants deriving rather than maintaining — or removing.

- **`KeyboardSource.Dropped` is now surfaced, in one place.**
  `WmDaemon.cs:945` puts it in the diagnostic report, closing
  `analysis.md:533`. It is still never logged and never sent over IPC
  (`StateProjection.cs:60-70`), so it is visible only to someone who already
  suspected it. In fairness it is close to unreachable: it needs 1024
  unconsumed *bound* keystrokes, and the pump is woken per keystroke.

---

## How this was gathered

A review of the working tree at `08ec686`, clean, on 2026-08-04, asked to
concentrate on animation and keyboard handling.

**Verified directly**, by reading the file and by grep across `src` and `tests`:

- `EVENT_OBJECT_LOCATIONCHANGE` subscribed (`WinEventSource.cs:64`), enqueued,
  signalling the pump (`:174`), and discarded (`WmDaemon.cs:583-587`)
- `AnimationKind.LayoutChange` and `WorkspaceSwitch` never constructed — all six
  matches for `AnimationKind.` in `src` enumerated
- `Placement.Raise` honoured only inside `Commit`, and animated placements
  `continue` before reaching it (`WmDaemon.cs:1694-1700`)
- `deltaMs` unclamped from `Stopwatch` through to `track.Elapsed`
- `AnimationEngine.Clear()` and `Easing.CubicBezier` having zero callers
- `KeyboardSource.Suspended` never assigned; `IsPaused` read only by `diagnose`
  and `StateProjection`
- `FocusFollowsCursor` and both `CursorJump` properties absent from
  `ToWmOptions()` and unread elsewhere
- `IsInjected` occurring exactly three times, none of them a read
- `SetBindingMode`'s short-circuit (`WindowManager.cs:1360`) and
  `BindingTable.Load` clearing `_activeMode` (`:57`)
- the two `alt+ctrl` bindings in `docs/shubbak.example.kdl`
- the absence of `RegisterHotKey`, any `WH_MOUSE_LL` hook,
  `SPI_GETCLIENTAREAANIMATION`, `SM_REMOTESESSION`, `DwmFlush` and any power or
  refresh-rate query anywhere in `src`
- the test-declaration count: 657

**Reasoned from the code and not reproduced**, and worth confirming before
acting:

- **The free-running frame rate under echo load.** The mechanism is certain —
  the subscription, the wake, and `QS_ALLINPUT` are all read directly — but the
  *rate* the loop actually reaches while animating was not measured. The
  argument for removing the subscription does not depend on the figure; the
  claim that the 7 ms interval is never honoured does. `_tickInterval`
  (`WmDaemon.cs:295`) already records exactly what is needed to settle it, and
  it is now in the diagnostic report. **Measure it first.**
- **The `_swallowed` leak.** The array is demonstrably never bulk-reset, and the
  consequence of a stale flag follows from reading `:276-280`. That the secure
  desktop and higher-integrity foreground processes actually withhold the
  release was reasoned from documented low-level hook behaviour, not
  reproduced.
- **The AltGr swallow.** That AltGr generates LeftControl + RightAlt and that
  `GetAsyncKeyState(VK_CONTROL)` therefore reports held is documented behaviour
  and the code path is confirmed by reading, but it was not demonstrated against
  a running daemon on a non-US layout. Ten minutes with a German layout would
  settle it, and it is the finding with the widest blast radius.

Nothing here was measured under load, and no profiler was run. Every
performance claim is reasoned from the code — which is the situation
`analysis.md`'s "Measurement did not survive the spike" section is about, and the
`LatencyStats` work since (`Shubbak.Core/Diagnostics/LatencyStats.cs`) is what
makes the first of the three above answerable now.
