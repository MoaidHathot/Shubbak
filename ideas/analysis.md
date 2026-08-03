# Analysis

Findings from a review of the tree at `fcd7839`, none of them acted on yet.
Companion to [further-improvements.md](further-improvements.md), and written for
the same reason: so the reasoning survives the conversation it came from.

Everything here cites a file and a line. Where a claim was inferred rather than
measured, the last section says so.

---

## Tests that have never run

`Shubbak.slnx` lists four test projects in its `/tests/` folder — Config, Core,
Native and Taj.Core (`Shubbak.slnx:15-20`). `tests/Shubbak.Wm.Tests` is not among
them. `dotnet test` on the solution has therefore never run it.

What is in it matters. `BindingModeSwallowTests.cs` opens by naming what it
guards:

> the most dangerous code in the program

That is the binding-mode path, where getting it wrong leaves the keyboard inert —
the failure mode in which the user cannot type the keystroke that would undo it.
Two files, twelve tests, none of them ever executed.

There is corroborating evidence that this is an omission rather than a deliberate
exclusion: the other four test projects each have a `TestResults\` directory and
`Shubbak.Wm.Tests` does not.

**The fix is one line.** It leads this document because it is the cheapest finding
here and the one with the worst consequence.

While in there: `SettingFormTests.cs` in that project tests `ConfigLoader` and
would work unmodified in `Shubbak.Config.Tests`, where it belongs. Neither file in
`Shubbak.Wm.Tests` actually tests `WmDaemon`.

---

## The command line's destructive defaults

`shubbak restore --help` restores windows.

`Restore` (`src/Shubbak.Cli/Program.cs:63-124`) reads its flags by scanning argv:

```csharp
bool dryRun = args.Contains("--dry-run") || args.Contains("-n");
bool all = args.Contains("--all");
bool cloakedOnly = args.Contains("--cloaked");
```

`--help` matches none of them, so control reaches the `else` at `:95`, loads the
session, and un-conceals every window it can identify — with `dryRun` false.
Asking for help performs the action.

`shubbak diagnose --help` has the same shape (`:273-283`): `--help` is not `-o`, so
`reason` stays `"manual"` and the command generates and prints a full diagnostic
report.

### Why it happened

There is no argument parser. Three styles coexist in one 638-line file: a `switch`
on `args[0]` (`:28-41`), whole-argv `Contains` scans (`:65-67`), and a manual index
walk for `--flag value` (`:273-274`). None of them has a notion of an unrecognised
argument, so every unrecognised argument is silently a default.

The index walk has its own defect: `for (int i = 1; i < args.Length - 1; i++)` at
`:273` means `shubbak diagnose -o` with no value ignores the flag and prints to
stdout instead of reporting the mistake.

### What to do

A `--help` check at the top of every subcommand handler is the correctness fix and
should not wait on the rest. The rest is UX debt worth clearing at the same time:

- **No `--version`.** It falls through `:40` and is sent to the daemon as a window
  manager command, so with no daemon running the user is told "no window manager is
  running" in answer to a version query.
- **No machine-readable output** outside `query`. `status`, `layouts`, `restore`,
  `inspect`, `check-config` and `config-path` are all human-only.
- **No shell completion** of any kind.
- **Exit codes are documented at `:43-53` and not followed.** `status` returns 1 for
  "not responding" but 2 for "not running", which are the same condition reached two
  ways. `sub` always returns 0 (`:256`), so a script cannot distinguish a clean
  Ctrl+C from a daemon that died mid-stream.
- **Unknown verbs are forwarded to the daemon**, so the Levenshtein "did you mean"
  hint in `CommandParser.cs:494-512` only fires when a daemon is running. With none,
  a typo produces a connection error instead of the good suggestion.

---

## The pipe trusts its clients

The IPC layer is 593 lines and no test project references it. Several of the things
below are the kind only a test would have caught.

### shell-exec is reachable from the pipe

`WmDaemonIpc.cs:28` routes the `command` method to `RunCommandAsync`, which hands
the raw payload to `CommandParser.TryParse` (`:87`). The command vocabulary includes
`shell-exec` (`CommandParser.cs:245`), which reaches `WmDaemon.ShellExecute`
(`WmDaemon.cs:1454-1473`) and starts a process with `UseShellExecute = true`
(`:1463`). There is no allowlist, and no distinction between commands a keybinding
may run and commands a pipe client may run.

The pipe is `CurrentUserOnly` (`IpcServer.cs:110`, `IpcClient.cs:53`), so an
attacker must already be running as the user. That bounds the problem but does not
close it, because `src/Shubbak.Wm/Program.cs:237` tells users to run elevated:

> Run elevated to manage windows belonging to elevated processes;

`CurrentUserOnly` scopes to the account, not the integrity level. A
medium-integrity process running as the same user — a browser child, an npm
postinstall script — can open the pipe of an elevated daemon and have it launch a
**high-integrity** process. That is a UAC bypass, available to anything already
running as the user.

#### Assessment: this needs a decision, not a patch

Two defensible positions:

1. **The pipe is trusted.** Any same-user process can already do a great deal. The
   elevated case is the only real escalation, and a user who runs elevated has
   deliberately opted into a more privileged daemon.
2. **The pipe is a boundary.** A window manager is not an execution service.
   `shell-exec` exists so a keybinding can launch a terminal, which is a config-time
   decision the user made deliberately; nothing about that requires it to be
   reachable at runtime by arbitrary local processes.

Position 2 is the safer default and the cheaper fix: gate `ShellExecCommand` on the
IPC path behind a config key, leaving keybindings and `startup-command` untouched.
The alternative — an explicit `PipeSecurity` carrying a High mandatory integrity
label when elevated — is more correct and more work, and still leaves the
same-integrity case open.

Whichever way it goes, it should be a written decision rather than an accident of
the command parser being shared between the two paths.

### A client can exhaust the daemon

| Resource | Bounded? | Evidence |
|---|---|---|
| Message size | no | `ReadLineAsync` at `IpcServer.cs:216` |
| Live client count | no | `_clients.Add` at `:116`; `ListenerCount = 4` bounds pending accepts only |
| Subscriptions per client | no | `_subscriptions.Add` at `:297`, in a loop over an unbounded split |
| Outbox per client | **yes, 512** | `:196` |
| Idle connection lifetime | no | the only token is `_shutdown.Token` (`:118`) |

A client that streams without ever sending a newline grows a `StreamReader` buffer
until the daemon dies. These also compound — unbounded clients multiplied by
unbounded subscription sets. And `Publish` runs on the daemon thread
(`WmDaemon.cs:2277`) taking one lock per client (`IpcServer.cs:191`), so stalled
clients add work to every tick.

### Backpressure does the opposite of what its comment says

`IpcServer.cs:78-82` states the intent plainly:

> A bar that stops reading must never be able to block the window manager, so a
> client whose buffer has filled is disconnected rather than waited on.

The implementation at `:196-200` does not disconnect. It calls `_outbox.Clear()`,
discarding 512 already-queued events plus the new one, keeps the client connected,
and tells it nothing. A slow bar silently begins showing arbitrarily stale state,
and because there is no epoch or generation counter it cannot detect that it has.

The right shape for a state-mirroring subscriber is to drop, then push a single
`resync` event so the client re-issues `query state`. That turns silent corruption
into something self-healing, and it is a small change.

### The protocol has no version

`IpcRequest`, `IpcResponse` and `IpcEvent` (`IpcProtocol.cs:11`, `:18`, `:25`) carry
no version field, `ConnectAsync` performs no handshake (`IpcClient.cs:49-59`), and
the pipe name is just `shubbak-{username}` (`IpcProtocol.cs:119`).

Adding or removing a *method* degrades gracefully — `WmDaemonIpc.cs:34` returns
"unknown method". Changing a *DTO* does not: `System.Text.Json` ignores unknown
members and deserialises missing ones to `default`, so a renamed or removed field is
silently wrong.

This is not hypothetical, and the source documents it. `WorkspaceInfo.Focused` was
added as a trailing optional parameter (`IpcProtocol.cs:59`), and the remark above
it at `:42-47` spells out the consequence for a bar that does not receive it — it
"has to mark them all identically, which is wrong the moment there is more than one
display". An old Taj against a new window manager gets exactly that, in silence.

A `hello` exchange carrying a protocol version is the correct answer. The cheap
interim is to put a version in the pipe name, which converts silent misbehaviour
into a clean "no window manager is running".

### Smaller things in the same layer

- **No request timeout.** `IpcClient.SendAsync` (`:62-94`) loops until a matching id
  arrives. Only *connect* is bounded, at 2 s (`Cli/Program.cs:162`). The server can
  return without replying (`IpcServer.cs:268`, when the payload deserialises to JSON
  `null`), and every CLI call site passes no token, so `shubbak query` against a
  wedged daemon hangs forever.
- **A reachable deadlock.** Almost everything routes through `WmDaemon.InvokeAsync`
  (`:317-331`), which is drained only by `DrainInbox` from `OnTick` (`:223`).
  Nothing faults the pending `TaskCompletionSource`s on shutdown (`Dispose`,
  `:2284-2304`), so in-flight requests are abandoned — and, given the point above,
  their clients wait forever.
- **Topics are never validated.** `Subscribe` (`IpcServer.cs:284-300`) returns `Ok`
  unconditionally for any string. A bar author who writes `window.focus` instead of
  `window.focused` is told it worked and then hears nothing.
- **`Task.Delay(16)` per client per pass, forever** (`:222`), and the losing timer is
  never cancelled, on an otherwise idle system.
- **The pipe name uses `Environment.UserName`, not a SID** (`:120`), so
  `DOMAIN1\alice` and `DOMAIN2\alice` collide; `ToLowerInvariant` on a username also
  carries the Turkish-I hazard.

---

## The message loop is not running at the rate it claims

`WmDaemon.Run` asks for an 8 ms tick (`WmDaemon.cs:177`). `MessageLoop.Run` delivers
it with `Thread.Sleep` (`MessageLoop.cs:61`), and the comment above it is candid
about the choice:

> MsgWaitForMultipleObjects would be the textbook choice, but a short sleep is
> adequate here and far simpler.

Nothing in the repository calls `timeBeginPeriod` or `NtSetTimerResolution`, and
since Windows 10 2004 timer resolution is per-process. So `Thread.Sleep(8)` sleeps
for roughly one scheduler quantum — **about 15.6 ms, giving around 64 ticks per
second, not 125**.

### What that costs

**The animation gate in ADR 0001 is not being met.** The ADR sets the frame budget
at 6.94 ms and gates on 144 Hz (`docs/adr/0001-language-choice.md:27`, `:112`). The
loop cannot deliver more than ~64 frames per second regardless of how fast the
managed code is. A 140 ms `WindowMove` (`AnimationEngine.cs:41-42`) gets about nine
frames where the design assumed twenty. The measured p99 of 0.262 ms is real; it was
simply measured against a loop being called at half the assumed rate.

**Input handling inherits the same floor.** `DrainKeyboard` and `DrainWindowEvents`
(`WmDaemon.cs:221-222`) run only on a tick, so a keystroke already sitting in the
ring waits up to a full sleep before anything looks at it.

**An idle desktop wakes 64 times a second.** The tick body when nothing is happening
is two uncontended locks, two volatile reads and a handful of branches — tens of
microseconds of CPU per second, which is nothing. The cost is not CPU, it is the
wakeups: 64 per second defeats timer coalescing and deep C-states, which for a
process expected to run continuously is the wrong default.

There is no adaptive behaviour to fall back on. The interval is captured once at
`MessageLoop.cs:38` and never varies, whether or not anything is animating, dirty or
queued.

### What to do

Replace the sleep with `MsgWaitForMultipleObjectsEx` on a wake event signalled from
`KeyboardSource.Enqueue`, `WinEventSource.Enqueue` and `WmDaemon.InvokeAsync`. Then
wait indefinitely when idle, and at a frame interval only while animating.

That single change gives zero idle wakeups, sub-millisecond input handoff and a
genuine 144 Hz animation path at once. The state needed to choose the timeout
already exists — `_animation.IsAnimating` is tested at `WmDaemon.cs:231`.

### Two things making it worse

**Every event dirties the layout.** `Publish` sets `_layoutDirty = true`
unconditionally at `WmDaemon.cs:2280`. `WindowManager`'s own doc says a rejection "is
normal, not exceptional" (`WindowManager.cs:71-74`) — and yet holding a focus key
against the leftmost window produces a `CommandRejected` per repeat, each one forcing
a full `ComputePlacements`, a `GetWindowRect` per visible window, and a commit pass.
Only geometry-affecting events should set it.

**Payloads are built for nobody.** `WmDaemon.cs:2277` evaluates
`StateProjection.Payload(...)` before calling `Publish`, which only checks for
subscribers at `IpcServer.cs:88`. With no bar running, every event still allocates a
`WindowInfo`, two strings for `State.ToString().ToLowerInvariant()`, and a serialised
JSON string, then discards all of it.

---

## What happens when a tick throws

`OnTick` is wrapped, and the intent is right — a daemon that dies leaves every
managed window stranded. The handling is not.

### A failed layout pass is lost permanently

`WmDaemon.cs:227-228`:

```csharp
_layoutDirty = false;
ApplyLayout();
```

The flag is cleared before the work. If `ApplyLayout` throws, the desktop is left in
whatever half-applied state the exception produced, and nothing retries until some
unrelated event happens to set the flag again. `_arriving.Remove(handle)` at `:1558`
has already run for the windows processed before the throw, so on the eventual retry
those windows animate rather than being placed — the exact stutter the mechanism at
`:1549-1557` exists to prevent.

Clearing the flag only on success, or restoring it in a `catch`, is a two-line fix.

### A repeating failure destroys the evidence of itself

The handler at `:237-242` is a bare `Log.Error` with no rate limit, no deduplication
and no suppression. A persistently failing tick — a corrupt node, a dead shell COM
object — throws around 64 times a second. `Log.Error` writes **two** entries each
time (`Log.cs:225-226`: the message, then the stack at Debug level). That is roughly
128 entries per second into a 2048-entry ring (`Log.cs:45`).

Within about sixteen seconds the diagnostic ring contains nothing but the same
repeated failure, which destroys precisely the forensic context it exists to
capture. The ring's own rationale at `Log.cs:26-32` — that `diagnose` should be able
to explain something that has already happened — is defeated in the one situation
where it matters most.

A same-site, same-type suppressor with exponential backoff and an "N occurrences
suppressed" summary preserves both the signal and the history.

### Ctrl+C during startup can leave an unstoppable daemon

`MessageLoop.Stop` sets `_running = false` and posts `WM_QUIT` if `_threadId` is
known (`MessageLoop.cs:73-79`). `Run` sets `_threadId` at `:35` and `_running = true`
at `:36`.

If `Stop` lands between process start and `:36` — and `Program.cs:34-39` wires Ctrl+C
to exactly that — then `_running` is set back to `true`, and `_threadId` was still 0
when `Stop` ran so no `WM_QUIT` was posted either. The loop then runs with nothing
able to stop it.

The window is narrow but not vanishing: startup takes at least a second because
`SettleWorkArea` (`WmDaemon.cs:2202-2238`) sleeps up to 20 × 50 ms waiting for the
bar's appbar strip.

### A config reload can half-apply

`WindowManager.AddWorkspace` throws `InvalidOperationException`
(`WindowManager.cs:216`), in violation of the contract stated at `:71-74` that
operations return `WmResult` rather than throwing. It is called unguarded from
`CreateConfiguredWorkspaces` (`WmDaemon.cs:2028`), which `LoadConfig` calls at
`:2068`.

A reload that throws there leaves the config half-applied: `_config` was already
replaced at `:2057` and `_bindings.Load` already ran at `:2062`, but
`ReconsiderOpenWindows` at `:2083` never runs. The exception surfaces in the tick
handler as a generic "tick failed".

The same method also leaks a buffered event. It calls `Emit(new
WorkspaceCreated(...))` at `:229` but returns `WorkspaceNode` rather than `WmResult`,
so `_pending` is never drained and the event surfaces attached to whatever unrelated
operation next calls `Complete()`. That is the exact cross-contamination
`ActivateWorkspaceCore`'s doc warns about at `:257-275`.

### Blocking work is still on the pump thread

`Log` was moved off this thread deliberately, and `Log.cs:253-259` records why:
inline logging made typing sluggish system-wide. Three equally blocking operations
remain on it:

- `SessionStore.Save`, synchronously, every 30 s (`WmDaemon.cs:1723`)
- `File.ReadAllText` twice in `LoadConfig` (`:2043-2044`)
- `File.ReadAllText` plus up to 2048 formatted log entries in
  `BuildDiagnosticReport` (`:842`, `:850`) — reached from a pipe thread, so an IPC
  `diagnose` stalls the message pump

The pump is what services `WINEVENT_OUTOFCONTEXT` callbacks, and
`WinEventSource.cs:162` drops the oldest event at 4096. Stalling the pump loses
window events.

---

## Silence in the configuration loader

The config loader is one of the strongest parts of the codebase. It emits 27 distinct
diagnostic codes, and several are clearly the product of real debugging pain — the
slash-wrapped-regex warning at `ConfigLoader.cs:809-820` and the "binding mode with
no way out" error at `:639-647` are both excellent.

The gap is structural rather than value-level: **nothing validates that a key or a
section name is recognised.**

`Build` (`:82-103`) only ever pulls named nodes:

```csharp
config = ApplyGeneral(config, document.Node("general"));
config = ApplyGaps(config, document.Node("gaps"));
config = ApplyEffects(config, document.Node("window-effects"));
```

`document.Node` returns null for a miss (`KdlNode.cs:145-151`), and every `Apply*`
opens with `if (node is null) return config;`. So `genral { }`, `window_effects { }`
or `animations { }` produce no diagnostic at all and are silently discarded;
`check-config` prints "ok". The same holds inside a section: `general {
focus-follows-mouse #true }` — a plausible typo for `focus-follows-cursor` (`:117`) —
is accepted in silence.

This contradicts the loader's own stated philosophy. `ParseMatchers` rejects unknown
matchers, and the comment at `:717-719` says exactly why:

> Everything else here is a mistake worth naming. Dropped in silence before, so a
> misspelt target left the rule matching on whatever else was in the block.

`CollectBindings` warns on unexpected children (`:443-448`). The treatment was simply
never extended upward to sections and settings.

### The same omission, three more times

- **`workspace layout="..."` is never validated** (`:409`), while `default-layout`
  next door *is* (`:137-151`, SHB0113) — and the comment at `:132-136` explains
  precisely why silence was wrong there. Same bug, adjacent field.
- **`rule on="..."` falls back silently.** `:845-851` maps anything unrecognised to
  `RuleTrigger.OnManage` through a `_ =>` arm. A rule written `on="titel-change"`
  fires at the wrong time and nothing says so.
- **`workspace monitor=N` is never range-checked** (`:408`), including negatives.

### One parser gap worth naming separately

KDL 2.0 raw strings written `#"..."#` — without the `r` prefix — misparse in silence.
`ParseRawString` is guarded on `Current == 'r'` (`KdlParser.cs:465`), and `#` is a
legal identifier character (`:394-398`), so `#"x"#` parses as three separate things:
a bare token, a string, and another bare token. No diagnostic.

The parser is deliberately a permissive hybrid of KDL 1.0 and 2.0 — `:533-534` notes
that both keyword spellings are accepted — so this is a gap in that intent rather
than a decision. Type annotations (`(u8)123`) and v2 multi-line strings (`"""..."""`)
are also unsupported, but both produce a hard error, which is a reasonable outcome.
Only the raw-string case is silent.

### What to do

A single generic pass: give `KdlNode` a `RecognisedChildren(params string[])` helper,
call it at the end of each `Apply*` and at the end of `Build`, and emit a **warning**
rather than an error so the "loading is total" property documented at `:36-42`
survives. The Levenshtein routine for "did you mean" already exists at
`CommandParser.cs:506-512` and can be lifted as it stands.

Worth fixing while there: `SHB0419` is used for two unrelated errors — "unknown
matcher" at `:721` and "expects true or false" at `:935` — which breaks any tooling
that filters on the code.

---

## A command that reports success and does nothing

`wm-toggle-pause` does nothing at all.

`WindowManager.IsPaused` (`WindowManager.cs:119`) is documented as meaning
"keybindings other than the one that resumes are ignored, and window events are
tracked but not acted on". It is set by `SetPaused` (`WmDaemon.cs:1350-1354`) from
`TogglePauseCommand` (`CommandExecutor.cs:134`), and then read in exactly two places,
both cosmetic: the diagnostic report (`WmDaemon.cs:829`) and the IPC state snapshot
(`StateProjection.cs:66`).

`KeyboardSource.Suspended` (`KeyboardSource.cs:124-128`) exists precisely to back
this feature — its own doc says so — and is **never assigned by anyone**.

So the command reports success, the bar shows a paused indicator, and every
keybinding and window event continues to work normally. Either wire `Suspended` and
gate `HandleWindowEvent`, or remove the command. Reporting success for a no-op is the
worst of the three options.

While in the area, `WmDaemon.EventsProduced` (`:135`, raised at `:2281`) has no
subscribers anywhere in `src`. The comment at `:1427-1431` even says so. It is a
public event on a public class that does nothing.

---

## The logging work that was not finished

[further-improvements.md](further-improvements.md) describes moving log formatting
off the caller and introducing `LogInterpolatedStringHandler` so the compiler can
skip every append until the level has been checked. Both were done. The handler was
only applied to half the API.

`Log.Trace` and `Log.Debug` have handler overloads (`Log.cs:200-210`). `Log.Info`,
`Log.Warn` and `Log.Error` do not — all three take a plain `string` (`:212-227`). So
every interpolated call at those levels builds its string regardless of
configuration, including at `--log-level warn` and `--log-level none`.

The sites that fire per window make it concrete:

- `:773-776` — per managed window, four interpolations plus a `Truncate`
- `:805-806` — per unmanaged window
- `:741-743` — per revived window at startup
- `:1266` — per rejected command
- `:2117-2119` — per released window on config reload

Extending the existing pattern to three more overloads is roughly forty lines and
removes dozens of unconditional allocations. It is the cheapest performance fix in
the codebase.

### The rule engine pays a Win32 tax it does not owe

`HandleWindowEvent` calls `ToAttributes(handle)` on every title change (`:431`) and
every focus change (`:461`). `ToAttributes` (`:1042-1052`) does `GetProcessId`,
`GetProcessPath` — which opens a process handle (`Win32Window.cs:287-288`) —
`GetTitle`, `GetClassName` and a `Path` call. Four Win32 calls, a kernel handle
opened and closed, and four string allocations.

It does this even when the config contains **zero** rules with that trigger, because
`ApplyRules` only discovers there is nothing to do at `:1085-1087`, after the work.
Browsers, terminals and media players change titles continuously.

Computing `_hasTitleChangeRules` and `_hasFocusRules` once when the config loads
(`:2057`) and testing them before the call removes the cost entirely for the common
case.

---

## Measurement did not survive the spike

ADR 0001 establishes the performance properties this project rests on: hook latency
p99.9 of 0.8 µs, zero dropped frames at 144 Hz, managed code at 2.5–5.3% of frame
time. Those numbers were produced by `spikes/Shubbak.Spike`, which contains a
`LatencyStats` computing percentiles from a fixed-capacity sample array, used there
for hook latency, frame time, wake jitter and WinEvent callback latency, and printing
a dropped-frame percentage.

**None of it graduated into the product.** The spike proved the properties once, at
design time. There is now no way to detect a regression in the shipping binary.

### What exists today

There is no metrics infrastructure of any kind — no `EventSource`, no `EventCounter`,
no `System.Diagnostics.Metrics`, no `ActivitySource`. The only matches for
"EventSource" in `src` are `WinEventSource`, which is the Win32 hook wrapper.

Three ad hoc counters exist. Two are unreachable:

| Counter | Declared | Read |
|---|---|---|
| `Log.TotalEntries` | `Log.cs:68` | `DiagnosticReport.cs:62` |
| `Log.Sink.Dropped` | `Log.cs:276` | **nowhere** — `Sink` is a private class, so the `public` is inert |
| `KeyboardSource.Dropped` | `KeyboardSource.cs:114` | **nowhere** |

The second is the counter that would tell you the log is lying to you. The third
means dropped keystrokes are invisible.

Timing is computed and then discarded: `OnTick` derives `deltaMs`
(`WmDaemon.cs:215-219`) and passes it to the animation engine, but nothing
accumulates it. There is no tick duration, no count of ticks over budget, no command
latency, no IPC queue depth.

### `diagnose` has no performance data

`BuildDiagnosticReport` (`WmDaemon.cs:817-851`) assembles environment, a set of
instantaneous gauges (monitor count, managed window count, animation active count,
IPC client count), the window tree, the config file and the recent log. Every one of
those is a gauge or a text dump. There is no timing, no rate, no histogram and no
counter-since-start — not even uptime, and no GC figures, despite
`GCSettings.LatencyMode = SustainedLowLatency` being set at `Wm/Program.cs:25` with no
way to check whether it is helping.

The footer's advice (`DiagnosticReport.cs:161-179`) is to enable trace logging and
reproduce. For a performance complaint — "the animation stutters" — that is the one
thing that cannot work, because trace logging changes the timing being measured.

### What to do, cheapest first

1. **Free.** Expose the two dead counters and add them to the environment section.
   Four lines, and two counters stop lying by omission.
2. **Small.** Lift `LatencyStats` from `spikes/` into `Shubbak.Core/Diagnostics`,
   record tick duration in `OnTick` and command duration in `RunCommandAsync`, and add
   a performance section to `diagnose`: uptime, tick count, p50/p99/max, ticks over
   budget, `GC.CollectionCount(0..2)` and `GC.GetTotalAllocatedBytes()`. This is the
   change that makes a performance bug report actionable.
3. **Optional.** An `EventSource` with counters so `dotnet-counters monitor` works
   live without a restart. Check it against `IsAotCompatible` in
   `Directory.Build.props` before committing to it.

---

## WmDaemon

`WmDaemon.cs` is 2305 lines. Its class doc at `:20` says:

> it is deliberately thin ... Everything it does is translate

That is no longer true, and the gap between the comment and the file is itself worth
fixing, because the comment is what a reader trusts.

The file owns roughly sixteen distinct concerns — process lifecycle, tick scheduling,
keyboard translation, window event routing, cross-thread marshalling, adoption
policy, session reconciliation, the rule engine, command dispatch, process launching,
layout and animation, focus borders and colour parsing, monitor reconciliation,
config loading, diagnostic text rendering, and IPC fan-out — across 20 mutable fields
with no encapsulation between them. `_layoutDirty` alone is written from twelve
places, which is the coupling mechanism between nearly all of them.

Three methods exceed 100 lines: `TryManage` at 190 (`:592-781`, doing eight things,
nesting to depth 5), `ApplyLayout` at 126 (`:1494-1619`) and `HandleWindowEvent` at
122 (`:378-499`).

### It has no tests

Searching the test tree for `WmDaemon` returns nothing. Coverage of the largest file
in `src` is zero, and it cannot easily be otherwise: collaborators are constructed in
field initialisers (`:37-40`) or inside `Run` (`:157-164`), there are around 45 direct
calls to Win32 statics, the only constructor parameter is `sessionPath`, and there is
no `InternalsVisibleTo` for `Shubbak.Wm.Tests` — so even a test that wanted to reach
`RunCommand` or `Inspect` cannot.

### The order to do this in

**Do not start with the decomposition.** Start with the parts that are already pure
and need only to be moved. These are testable today, with fixtures that already exist
in `Shubbak.Core.Tests`:

| Logic | Location | Note |
|---|---|---|
| `TryParseColour` | `:1896-1945` | 50 lines, pure, completely untested |
| `SplitCommandLine` | `:1475-1490` | pure, and buggy: an unterminated quote falls through to a space split, so `"C:\Program Files\x.exe` yields `"C:\Program` |
| `ShouldIgnore` / `ShouldForceManage` | `:1054-1081` | rule *parsing* is tested; rule *evaluation* is not |
| `ColourFor` | `:1781-1791` | four-way fallback, no test |
| `MonitorLayoutChanged` | `:1672-1692` | pure once the `Enumerate()` call is lifted out |
| `CreateConfiguredWorkspaces` | `:1987-2030` | the reconciliation at `:2003-2015` is untested |
| `HandleWindowEvent` routing | `:378-499` | a pure function of five inputs; the tray-app case at `:358-376` cites a real bug and has no test |
| `ResolveTarget` decision | `:1141-1188` | the comment at `:1124-1138` describes two production bugs |
| `DescribeTree` / `DescribeNode` | `:861-909` | belongs next to `DiagnosticReport`, which is tested |

Then add `InternalsVisibleTo`, then introduce interfaces for the three platform
sources so `OnTick`, `TryManage` and `ApplyLayout` can be exercised against a fake
desktop. The nine-way class extraction — rule engine, focus-border painter, monitor
reconciler, layout applier, event router, session coordinator, diagnostics,
dispatcher, process launcher — is worth doing, and would take the file to roughly 400
lines, but it is much safer once any of it is covered.

### Duplication worth collapsing on the way through

- `ShouldIgnore` and `ShouldForceManage` (`:1054-1081`) are structurally identical,
  differing only in the command type tested.
- `Execute` (`:1105-1118`) and `RunCommand` (`:1243-1259`) share the same five-step
  body; the first should simply loop over the second.
- The interval-gate arithmetic appears three times, identically (`:1653-1657`,
  `:1715-1719`, `:1868-1872`).
- `Truncate(Win32Window.GetTitle(handle), 40)` appears twelve times.
- `WindowFilter.Evaluate` is called twice on the same handle on every rejected
  command — `:1170`, then again at `:1200` to build the message — and `Evaluate` is
  about ten Win32 calls including an `OpenProcess`.
- `MonitorSource.Enumerate()` runs twice back to back (`:1674` then `:1951`), and
  `SettleWorkArea` puts that pair inside a 20-iteration loop, so startup can perform
  forty full monitor enumerations.

---

## Unrelated ideas worth keeping

The first three are restated from
[further-improvements.md](further-improvements.md), where they were first written
down. They are recorded in both places; fixing one should retire both entries.

- **Offline `shubbak diagnose`.** It currently requires the running daemon, which is
  backwards: a report is most wanted when the window manager has died. It should fall
  back to environment, binary identity, config-as-parsed and the on-disk log, clearly
  marked as having no live state.
- **`VisualStyle.Opacity` is dead.** Declared, never read. Either wire it up or remove
  it.
- **Scroll handlers are modelled but unreachable.** `VisualNode.OnScrollUp` and
  `OnScrollDown` exist; the bar's window procedure never handles `WM_MOUSEWHEEL`.
  Scrolling the workspace list to switch workspaces is the obvious use.

And the ones this review added:

- **No config hot-reload.** `FileSystemWatcher` appears nowhere in `src` — the only
  matches in the repository are inside prebuilt binaries under `spikes/`. Reload is
  entirely command-driven, so editing `shubbak.kdl` means remembering to press the
  reload key. A debounced watcher on the resolved path, gated by a config key and
  feeding the existing `HostAction.ReloadConfig` path, would close it. Two hazards to
  handle: editors that write-rename, so watch the directory and filter rather than
  holding the file; and never apply a config with errors, for which
  `ConfigLoadResult.HasErrors` is already the right gate.

  The propagation half is already well built — the comment at `WmDaemon.cs:1423-1431`
  explains why the reload announcement goes through `Publish` rather than
  `EventsProduced`, and Taj picks it up at `Taj/Program.cs:271`. Only the trigger is
  missing.

- **No CI.** There is no `.github` directory and no pipeline of any kind; the only
  script in the repository is `tools/run-p0.ps1`. Given `TreatWarningsAsErrors` is
  already on, a build-and-test workflow is nearly free — and it would have caught the
  solution-file omission at the top of this document on the commit that introduced
  it.

- **The README's test count is stale.** It says 459 tests in two places
  (`README.md:23`, `:198`); the tree currently declares 623 `[Fact]` and `[Theory]`
  methods across 58 files.

- **No DPI coverage, and none currently writable.** Searching the test tree for "dpi"
  finds only two prose comments. `MonitorNode.Dpi` and `MonitorInfoDto.Dpi` are never
  asserted, and `TreeBuilder.Monitor` has no DPI parameter
  (`tests/Shubbak.Core.Tests/TreeBuilder.cs:50-59`), so the fixture needs extending
  before such a test can exist.
  Every monitor in every test is 1920×1080 at default DPI. Mixed resolutions and
  monitor hot-plug are likewise uncovered.

---

## How this was gathered

A review of the working tree at `fcd7839`, clean, on 2026-08-04.

**Verified directly**, by reading the file:

- the solution-file omission (`Shubbak.slnx`, whole file)
- `restore --help` reaching the restore branch (`Cli/Program.cs:55-124`)
- `Thread.Sleep(intervalMs)` and the absence of any wait primitive
  (`MessageLoop.cs`, whole file)
- the `shell-exec` path from `CommandParser` through `CommandExecutor` to
  `UseShellExecute = true`
- the test-declaration count: 623 across 58 files

**Reported by exploration and not independently re-read line by line**: most of the
line references inside `WmDaemon.cs`, `WindowManager.cs`, `IpcServer.cs`,
`ConfigLoader.cs` and `KdlParser.cs`. They were consistent with each other and with
the files that were read, but an individual line number may have drifted.

**Inferred rather than measured**, and worth confirming before acting on:

- **The ~15.6 ms tick.** This follows from `Thread.Sleep(8)` plus the absence of any
  `timeBeginPeriod` call in the repository, and from timer resolution being
  per-process since Windows 10 2004. It was not measured on a running daemon. The
  argument for replacing the sleep does not depend on the exact figure, but the claim
  that the ADR's 144 Hz gate is unmet does. **Measure it first.**
- **The elevation escalation.** The code path is confirmed by reading. That a
  medium-integrity process can in practice open the pipe of an elevated same-user
  daemon under `PipeOptions.CurrentUserOnly` was reasoned from the documented
  semantics, not demonstrated with a test client. Worth ten minutes to demonstrate
  before choosing between the two fixes.

Nothing here was measured under load, and no profiler was run. The performance items
are reasoned from the code — which is exactly the situation the "Measurement did not
survive the spike" section is about.
