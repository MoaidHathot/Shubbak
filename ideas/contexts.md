# Contexts

A design note for the work that starts at tag `pre-contexts` (`73fc04a`). Written
before the code so the reasoning survives the conversation it came from, in the same
spirit as `further-improvements.md`. Decisions here were made deliberately; the ones
that turn out wrong should be changed here first.

---

## What this is for

Shubbak knows every top-level window, every monitor, and a fair amount about the
session - and acts on almost none of it beyond placing windows. The cases that
motivated this:

- A talk from a laptop alone, then the same laptop on a projector, then docked to two
  monitors for a remote session. Each wants different gaps, borders, bar, and
  workspace-to-monitor homes, and the keyboard to be a little safer while on stage.
- Sharing a screen or presenting from PowerPoint, Teams, or whatever replaces them
  next year - with the rule that the window manager must not be the one deciding what
  "presenting" looks like.
- Demos that need the same arrangement of windows back after they have been dragged
  around.

The generalisation is a **context**: a named, level-triggered condition that, while
it holds, layers overrides on the configuration. Several may hold at once. The rig
(laptop / projector / docked) and the activity (presenting / meeting / normal) are
orthogonal, so contexts compose rather than exclude.

## The line

> **Shubbak observes the desktop, not the applications.**

A fact is built in if the window manager needs it to place windows, or if Windows
exposes it about the *session* in one cheap call. Anything about what an application
is *doing* - with hardware, with the network, with a calendar - comes from outside,
over the pipe, as an external context.

| Built in | External |
|---|---|
| window present / focused / title / class / process / native-fullscreen | camera or microphone in use |
| workspace active / focused | Teams presence, Zoom state, OBS recording |
| monitor identity, count, Win+P topology | Stream Deck buttons, calendar, time of day |
| remote session | battery, power source, network |
| user-notification state (presentation mode, full-screen app, game) | anything with a vendor SDK |

Camera and microphone are on the right despite being cheap to read, because "it is
only a hundred lines" is true of every item in that column and is how a window
manager grows a weather widget. The test is *topic*, not cost.

The symmetry that makes the line cheap to hold: **facts come in through
`context --set`, effects go out through `context.changed`.** Everything the window
manager *changes* is its own state, so effects are all built in. Everything a bar or
a palette does with contexts is consumption, which the pipe already serves.

### Why not in-process plugins

NativeAOT rules them out: no runtime assembly loading, a native DLL would share a
process with the thing holding every window's fate, and an embedded scripting
language is a new surface plus a dependency. The pipe *is* the plugin architecture -
Taj and Dalil are its two plugins - and the question was never "build a plugin
system" but "which primitive is the pipe missing". It was missing retained state a
client can set: `signal` is fire-and-forget, so a provider had no way to durably say
"a meeting is on" to a bar that connects later.

### Making the external route not-harder

- A context with no `when` is external by definition. Nothing else about it differs.
- `context --set x --ttl 5s` for pollers: a crashed provider's fact expires. A window
  manager must never be left believing you are in a meeting.
- `context --set x --lease` for push providers: the pin dies with the pipe
  connection. No heartbeat traffic.
- Plain `--set` stays until cleared, like a binding mode. Pins do not survive the
  daemon.
- Policy stays in the user's config. Providers supply booleans; the user composes them
  with built-in facts (`when { context "meeting"; monitor present="dell-left" }`). A
  provider never needs to know what "meeting" should *do*.
- Attribution replaces load-time validation. The loader cannot know whether a provider
  will ever set `meeting`; `shubbak contexts` says `external - set by ayn.exe
  (pid 1234) 1.2 s ago, expires in 3.8 s`, or `never set`.

The reference provider is a fifth optional executable - working name Rasid, راصد,
"observer"; shipped as **Ayn**, عين, "eye", because the second word is one everybody
uses and the first is not - that watches the camera and microphone consent store with
`RegNotifyChangeKeyValue` and sets a context with a lease. It exists as much to
dogfood the API as to detect meetings: if it is painful to write, the API is wrong,
and that should be found here rather than by the first person to try.

## Vocabulary

`context`, not `mode`. Mode already means two things in this project - binding modes
and palette modes - and a bar showing "mode: presenting" beside "mode: resize" would
be a bug report waiting to happen. `scene` was considered and kept for saved
arrangements, where it means what OBS users expect.

## Semantics decided up front

- **Layered.** Any number active; overrides cascade in declaration order, later wins.
  The base config is layer zero.
- **Level-triggered.** Active exactly while `when` holds. No stuck state, no exit rule
  to forget. Within a `when`, children AND; several `when` blocks OR. Every condition
  is negatable with `!`.
- **Pins beat detection.** Explicit beats inferred. `--auto` hands a context back to
  its `when`.
- **Linger.** Deactivation waits for the condition to be false for `linger` ms
  (default 250). PowerPoint creates and destroys several windows while a slideshow
  starts; without this the context flaps and `on-enter`/`on-exit` fire twice.
- **Rules scoped to a context apply to events after activation.** Activating does not
  re-run rules over existing windows; `on-enter` is the place for bulk actions.
- **Workspace homes are re-applied on topology change and context change only.** A
  manual `move-workspace` sticks until the next such event, so re-homing never fights
  the user mid-session.
- **Order on a flip:** `on-exit` (old effective config) → apply effective config →
  publish `context.changed` → `on-enter`.
- **Not overridable:** `hide-method`, logging, `allow-shell-exec-over-ipc`. The first
  is unrecoverable if wrong, the other two are not about the desktop.
- **Suspended freezes contexts.** Re-evaluated on resume.

## Performance rules

The daemon's own instrumentation sets the bar. Baseline at `pre-contexts`, measured
on the 0.9.0 build from `dist\` after 11 h 36 m of ordinary use, two monitors,
nineteen workspaces, seven managed windows, six IPC clients:

| | |
|---|---|
| Tick interval | p50 262 ms, p99 266 ms (~4 Hz idle) |
| Tick duration | p50 0.03 ms, p99 1.09 ms |
| Allocated per tick | p50 0 B, p99 720 B |
| GC | gen0 6, gen1 6, gen2 6 in 11.6 h; 99 MB allocated in total |
| CPU | 21.0 s in 11 h 36 m, about 0.05% of one core |
| Working set / private | shubbak-wm 41.3 / 26.2 MB; taj 41.1 / 28.7; dalil 17.1 / 8.3 |
| Binaries | shubbak-wm 5.62, shubbak 4.51, taj 4.71, dalil 4.76 MB |

Rules, each pinned by a test where one can be written:

1. **No new wake-ups.** `IdleWait` is untouched. Evaluation runs on window, focus,
   workspace and monitor events, and after the existing 2 s probe, behind a dirty
   flag. `linger` and TTL expiry are checked on whatever tick comes next, which idle
   means up to 250 ms late. Fine.
2. **Zero allocation when nothing changed.** Facts live in a mutable board, not a
   record rebuilt per tick. A `ContextEvaluatorAllocationTests` in the style of
   `TickAllocationTests`.
3. **The keyboard hook does one lookup, as today.** A bindings overlay is merged into
   the default table when a context flips and published as one `Snapshot`. The
   callback's work does not change by a single comparison.
4. **No window catalogue.** One `HashSet<nint>` of currently-matching handles per
   `window app=` condition. Matchers run cheap-first: cached process identity, then
   class, then title regex, and the title is read only if a matcher needs it.
5. **Probes behind existing gates.** `QueryDisplayConfig` only when
   `MonitorLayoutChanged`; `SHQueryUserNotificationState` inside
   `RefreshDisplayPreferences`; new topics behind `HasSubscribers`.
6. **Measure, then write it down.** `diagnose` and the working set before and after
   each phase, recorded in the CHANGELOG entry that ships it.

Expected: idle CPU unchanged, event costs in microseconds, daemon memory up by tens of
kilobytes, binary up by 200-400 KB. The one material cost is a *process*: a NativeAOT
executable is 4-6 MB resident, a persistent PowerShell host is 60-100 MB. In-process
detection is always cheaper in memory; what a process buys is isolation from the one
that owns every window, any language, and no AOT constraints.

## Phases

Each shippable alone, in this order.

0. **Publish what is already known.** `window.native_fullscreen`, `wm.environment`
   (remote session, user activity), `binding.fired` (bound chords only - never raw
   keys; the pipe is per-account). Optional fields on the state snapshot. These are
   the fact stream the evaluator will consume, and a keycast overlay for talks becomes
   a small external subscriber.
1. **Named monitors and re-homing.** EDID friendly name, device path, internal-panel
   flag, Win+P topology via `QueryDisplayConfig`. `monitor "name" { ... }` matchers,
   workspaces bound by name, re-homing when a monitor returns (today they stay where
   they were pushed), `move-workspace --monitor`, bars that follow hot-plug.
2. **Contexts core.** Config, pure evaluator, `context` verb with `--set/--clear/
   --toggle/--auto/--ttl/--lease`, effects (gaps, effects, animation, bindings
   overlay, rules, workspace homes, on-enter/on-exit), `context.changed`, `query
   contexts`, `shubbak contexts` with reasons and attribution.
3. **Taj and Dalil.** `rule use="..." context="..."`, `{{ contexts }}`, palette status
   line and `from="contexts"`.
4. **Ayn.** The reference provider.
5. **Saved arrangements.** `arrangement --save/--restore` per workspace, including the
   container tree and ratios, which the session file deliberately does not record.

## What was measured after each phase

Filled in as phases land. Same procedure as the baseline: `shubbak diagnose` after a
comparable stretch of use, `Get-Process` for the working set, `dist\` for sizes.

### Phase 0, and the display-topology probe from Phase 1

Shipped together: the three events, the session fields on the snapshot, monitor
identity on `MonitorNode` and `MonitorInfoDto`, and Taj subscribing to a named list
instead of `*`.

**Definitive now** - binaries, NativeAOT, Release:

| | before | after | delta |
|---|---|---|---|
| shubbak-wm.exe | 5.62 MB | 5.67 MB | +56 KB |
| shubbak.exe | 4.51 MB | 4.54 MB | +32 KB |
| taj.exe | 4.71 MB | 4.75 MB | +34 KB |
| dalil.exe | 4.76 MB | 4.79 MB | +32 KB |

The estimate was 200-400 KB for the daemon; it came in at a quarter of that, most of
it the display-configuration P/Invokes and the new records.

**Indicative now, comparable later** - the daemon six and a half minutes after a
restart, so the histogram still contains startup adoption, a deliberate full-screen
round trip and a first `diagnose`, whereas the baseline's window was a quiet stretch
after eleven hours:

| | baseline | after 6 m 35 s |
|---|---|---|
| Tick interval | p50 262 ms, p99 266 ms | p50 163 ms, p99 265 ms |
| Tick duration | p50 0.03 ms, p99 1.09 ms | p50 0.01 ms, p99 1.34 ms |
| Allocated per tick | p50 0 B, p99 720 B | p50 0 B, p99 11,456 B |
| GC | 6 / 6 / 6 in 11.6 h | 0 / 0 / 0 |
| Working set, shubbak-wm | 41.3 MB (11 h old) | 31.6 MB (fresh) |

Reading it honestly:

- The idle tick is what rule 1 protects and it reads the same: tens of microseconds,
  nothing allocated at the median.
- The interval median fell because the measuring woke the pump: every CLI call is an
  IPC request and every request ends the wait early. `IdleWait` is untouched and no
  timer was added, so a quiet stretch will read 4 Hz again.
- The p99 column is not a like-for-like sample until the 4,096-tick window rolls past
  the restart, and nothing periodic was added that could move it: the topology probe
  runs only inside `SyncMonitors`, the activity read is one `out` parameter on the
  existing 2 s probe, and `binding.fired` is built only for a subscriber - which, now
  that Taj names its topics, is nobody unless someone is tailing.
- The all-time drain maximum of 693 KB is the first `diagnose` report, built on the
  daemon thread. The baseline's maximum excluded its own report because the histogram
  is read before the report is written. A diagnostic, not a tick cost.

Re-measure with the same two commands after a day and replace this table.

**Things found on the way that change later phases:**

- **Two of the same monitor report the same friendly name.** This machine has two
  `DELL U3219Q`s; they differ only in the device path (`DELA124…UID4355` and
  `DELA133…UID4357`). So `monitor "left" { name ~= "U3219" }` is not enough to name
  one of them, and the Phase 1 config must make `path` a first-class matcher rather
  than a fallback. The daemon logs both at startup so the path is one copy away.
- **The shell reports a browser's F11 as `fullscreen-app`.** `QUNS_BUSY` fires for an
  ordinary window covering the screen, not only for games, so `wm.environment` is a
  real signal for the presenting context and not just for stand-down. It lags the
  window's own event by up to the 2 s probe.
- **Dalil does not leave on `wm.shutdown`.** Its log showed nothing received at the
  moment the daemon exited; the bar left, the palette stayed. The topic is documented
  as best-effort and `shubbak dalil-exit` closes it, but a palette attached to nothing
  is worth a look before Ayn copies the same reconnect loop.
- **A test process is not DPI-aware.** The probe reported `2560x1440 @ 96` for panels
  the daemon sees as `3840x2160 @ 144`. Harmless here - the join is by device id - but
  any future test asserting geometry against the daemon's numbers will need
  `EnableDpiAwareness` first.

### Phase 1

Named monitors, re-homing, `move-workspace --monitor`, `shubbak monitors`, bars that
follow displays, and the session remembering connector paths.

**Decisions made while building it, beyond the plan:**

- **`path` is a first-class matcher, not a fallback.** Forced by the finding above:
  two `DELL U3219Q`s. The `shubbak monitors` output matches on the shortest tail of
  the path that no other attached display shares - `UID4355` - rather than the whole
  hundred characters, widening leftwards only if that is not unique.
- **A quoted number is a position.** `monitor="1"` and `monitor=1` mean the same thing
  unless a monitor is literally declared as `"1"`. Workspace names are written both
  ways in the example config; the value type is not a signal of intent.
- **`monitor="DISPLAY2"` is accepted.** The old `SHB0431` said device names had never
  worked. They are positional and reused by Windows, but sometimes they are all a
  person has, and refusing them sent people back to indices. `MonitorReference` in
  Core resolves the positional spellings against the tree; declared names are the
  host's to resolve onto `MonitorNode.Names`, and the state machine reads them off the
  node. It never sees a definition; the config never sees a display.
- **Re-homing is a level, not an edge.** `RehomeWorkspaces` runs after every monitor
  sync and every reload, moves nothing that is already home, and produces no events
  when nothing moves - so it costs a few comparisons on a topology change and nothing
  otherwise. A hand-moved workspace stays until the next such event; the alternative
  (re-home on every tick) would fight the user.
- **Bars are keyed by device name, driven by the WM's monitor list, not by
  `WM_DISPLAYCHANGE`.** One party watches the displays; the bar agrees with it rather
  than racing it. The five parallel lists indexed by position became one record per
  bar, which also removed a latent off-by-one: a bar whose window failed to create was
  skipped while its index was not.
- **`SHB0428` now covers workspace properties.** `bind-to-monitor=1` - GlazeWM's name
  for the same thing - was accepted and ignored.

**Diagnostic codes used:** SHB0315-0316 (parser), SHB0438-0443 (loader). SHB0431 is
retired; nothing pinned it.

**Not done, deliberately:**

- The bar does not relocate on DPI-only changes; the daemon's `MonitorChanged` fires
  but `MonitorsDiffer` looks only at identity and rectangle. A DPI change without a
  rectangle change means the panel's scaling moved; the bar's height is in logical
  pixels and GDI handles it. Revisit if a bar looks wrong after a scaling change.
- `RemoveMonitor` still migrates to the *first* surviving monitor rather than to a
  workspace's home if that is attached. Re-homing runs right after and corrects it, so
  the only cost is a second `workspace.moved` event for those workspaces during an
  undock. Folding the two would save the event and complicate `RemoveMonitor`; not
  worth it yet.

**Measured.** Binaries, NativeAOT Release, against the Phase 0 build:

| | Phase 0 | Phase 1 | delta |
|---|---|---|---|
| shubbak-wm.exe | 5.67 MB | 5.74 MB | +68 KB |
| shubbak.exe | 4.54 MB | 4.61 MB | +68 KB |
| taj.exe | 4.75 MB | 4.80 MB | +53 KB |
| dalil.exe | 4.79 MB | 4.85 MB | +57 KB |

Cumulative since `pre-contexts`: daemon +124 KB, the others +85 to +100 KB. The CLI
grew as much as the daemon because it gained the report writer and the daemon's
`MonitorDefinition` matching arrived via `Shubbak.Config`, which the CLI links too.

The Phase 0 build ran for 1 h 49 m on a busy development desktop - builds, test runs,
dozens of transient console windows - before being replaced. Its tick interval was
p50 251 ms, p99 266 ms, tick duration p50 0.02 ms, p99 1.87 ms; three GCs in each
generation. Allocation p99 was 46 KB per tick against the baseline's 720 B, and the
breakdown says where: `drain` p99 46 KB with `publish` p99 only 5.5 KB, so the
allocation is window-event handling for the windows the tooling kept opening and
closing (14 ignored windows at the time of the report against 3 in the baseline), not
the event stream. Not a like-for-like sample, and the idle tick at the median reads as
it always has. The Phase 1 build a minute and a half after starting: p50 0.02 ms,
0 B, no GCs. A quiet day on this build is what would settle the p99 column; measure
it before starting Phase 2.

**Live verification done:** `shubbak monitors` on the two-Dell desk, `move-workspace
--monitor` by device name, index and declared name, both refusals, the both-flags
parse error, a `--replace` start with monitor definitions naming both displays and
binding workspace 9 to `dell-right` (created there), then `move-workspace --monitor
dell-left` followed by `wm-reload-config` re-homing it with the `back on` log line and
the view preserved. Taj came through the daemon restart with two bars and an idempotent
reconcile. **Not exercised live:** an actual unplug or replug, and a bar relocating
after a resolution change - the code paths are covered by tests at the unit level and
the reconcile ran as a no-op against the real list, but the first real dock will be
the first real test. Watch `taj.log` for `has gone` / `has arrived` lines.

**Found on the way:** each bar's connection reports `config.reloaded` separately, and
with the pump-thread `Wake()` that Phase 1 added the loop reloaded once per display.
Coalesced to one reload per 250 ms. Before the `Wake()`, both reports usually landed
before the loop's next natural pass, so this was a latent shape rather than a new one.
Dalil still did not leave on `wm.shutdown` during either restart.

### Phase 2

The contexts core, with the extension primitive. Everything the plan listed shipped:
the config section, the pure engine, the `context` verb with `--ttl` and `--lease`,
the seven kinds of effect, `context.changed`, the snapshot field, `query contexts`,
`shubbak contexts`, and the attribution.

**How it is shaped, and why:**

- **The engine lives in `Shubbak.Wm`, not the state machine.** Which contexts hold is
  decided from facts the tree has never held - windows it does not manage, the
  session, what a client asked for - so the tree is one input rather than the owner.
  `ContextChanged` is raised by the daemon exactly as `SuspendChanged` is; the state
  machine did not learn a new concept. The engine is still headless: no Win32, time as
  a parameter, `WindowRegistry` and `RootNode` read through a struct of references.
- **Deltas, not resets.** `ReadGaps`, `ReadEffects` and `ReadAnimation` now read the
  top-level sections and a context's blocks alike into override records, which are
  applied onto whatever is underneath. The top-level result is byte-for-byte what it
  was (411 loader tests unchanged), and a context's `gaps { inner 0 }` means "inner
  zero, everything else as it was". `window-effects` was the odd one out - rebuilt from
  scratch, border defaulting to off when unspecified - and layering it changed nothing
  for a file whose defaults underneath are off and unset.
- **The hook still does one lookup.** `BindingTable.SetOverlay` merges the overlay into
  the default dictionary when a context flips; `IsBound` and `Resolve` are untouched.
  The overlay does not reach inside a binding mode, where the mode's table is the
  whole keyboard as it always was. Rule three, pinned by `BindingOverlayTests`.
- **Zero allocation when nothing changed, and nothing looked at when nothing is dirty.**
  `ContextEngine.Evaluate` returns a static empty list without work unless a fact
  changed or a linger or time-to-live is due; when it does run and finds nothing
  changed it allocates nothing - pinned by two tests, one of which caught a real
  `foreach` over an `IReadOnlyList` (a boxed enumerator, 72 bytes) on the first run.
  The daemon calls it every tick after the drains: two comparisons on a quiet desktop.
  Rules one and two.
- **No window catalogue.** One `HashSet<nint>` per `window` condition, one bool per
  `focused` condition; `fullscreen` reads the managed windows' identities with no
  syscall. Attributes are read only when a condition exists, once per event, and the
  title is the last thing read. Rule four. Title-change storms are the accepted cost,
  paid once by whoever wrote a `window` condition.
- **The fixed point.** A context may refer to one declared after it, so detection is
  run to a fixed point over the effective set and diffed once against the state before
  the call. A context that flipped and flipped back inside the loop reports nothing,
  which is the truth about it. The loader removes cycles, so the loop converges in at
  most one pass per context; the cap is a guard against a loader bug.
- **Pins are host state with an origin.** `RunCommand` gained an optional
  `CommandOrigin`, set ambiently for the duration of one command, and only `WmDaemonIpc`
  supplies one - only for a payload containing a `context` verb, so `focus` pays
  nothing for it. The pipe server gained a client-aware handler overload, a per-connection
  id (a counter, because handles are reused), and `ClientDisconnected`; a lease is a pin
  that names a connection id and dies when the notice arrives.
- **Toggle pins to the opposite of what it is now.** Not "toggle the pin": a detected-on
  context toggled goes to pinned-off, and toggled again to pinned-on. Predictable from
  the outside, which is what a key wants.

**Decisions made while building it, beyond the plan:**

- **`--ttl` and `--lease` are refused with `--auto`** (`SHB0320`), since there is no pin
  for them to govern, and accepted with `--toggle`, applying to whichever pin it turns
  out to make.
- **Bare numbers in `--ttl` are seconds; `linger` is milliseconds.** Different units in
  different places, deliberately: a time-to-live is a heartbeat, a linger is a debounce.
- **A quoted `--ttl` under a second is fine** (`--ttl 500ms`), and zero or negative is
  refused (`SHB0319`).
- **Pins survive a reload for contexts the new file still declares**, as the binding
  mode does; a pinned context the file no longer declares is logged and dropped.
- **Suspended freezes the contexts.** A `context --set` while suspended is accepted and
  logged as taking effect on resume; the window sets are re-read on resume because the
  hooks were down.
- **A destroy event can be missed** while the hooks are down, so the 2 s monitor sync
  prunes dead handles from the window sets - one `IsWindow` per remembered handle,
  only while any is remembered.
- **Attribution is best-effort.** The pid comes from `GetNamedPipeClientProcessId`, the
  name from the process path cache; a process that has exited by the time it is asked
  about is described by its pid.
- **`WindowMatcher.ToString()` now spells targets as the config does** (`process=`
  rather than `processname=`), which `inspect`'s failed-matcher list and a context's
  condition text both show to a person.

**Diagnostic codes used:** SHB0317-0320 (parser), SHB0444-0451 (loader). Unknown
settings inside a context reuse SHB0428.

**Not done, deliberately:**

- **Taj and Dalil do not act on contexts yet.** That is Phase 3: `rule ...
  context="presenting"` on the bar, `{{ contexts }}`, the palette's status pill and
  `from="contexts"`. Both processes already receive `context.changed` if they subscribe
  and read `contexts` off the snapshot.
- **A context cannot override `hide-method`, logging, or `allow-shell-exec-over-ipc`.**
  The first is unrecoverable if wrong; the other two are not about the desktop.
- **`query bindings` does not list overlay bindings**, and `binding.fired` reports
  `mode: null` for one. A follow-up could report them under a `context:<name>` mode.
- **Activating a context does not re-run its rules over existing windows.** `on-enter`
  is the place for bulk actions; the alternative churns every window on every flip.

**Measured** (release, NativeAOT, same SDK as Phase 1):

| binary | Phase 1 | Phase 2 | delta |
|---|---|---|---|
| shubbak-wm.exe | 5.74 MB | 6.05 MB | +320 KB |
| shubbak.exe | 4.61 MB | 4.83 MB | +218 KB |
| taj.exe | 4.80 MB | 5.01 MB | +216 KB |
| dalil.exe | 4.85 MB | 5.08 MB | +234 KB |

Roughly 220 KB of that is shared by all four, since all four link `Shubbak.Config`
and `Shubbak.Ipc`: the section parser and its records, the three report DTOs and the
snapshot field in the source-generated JSON, the verb in Core. The daemon carries
about 100 KB more for the engine and its integration. That is the largest single step
in the feature, and it is the price of a closed, checked vocabulary rather than a
string bag: every condition and every effect is a type with a parser and a report.

Runtime, on the desk, with three contexts declared - one `fullscreen app=` (so
window attributes are read on every window event), one external, one `monitors
count=` - over 12 minutes that included F11 toggles, two reloads, a dozen pins and a
lease: tick p50 0.02 ms (unchanged from the baseline and Phase 1), p99 1.79 ms,
allocation p50 0 B, zero collections in any generation. Working set 33.5 MB (Phase 1
settled at 36.6 MB after 1h14m; both are within the run-to-run spread). The p99 is a
busy testing window and not a like-for-like against Phase 1's settled 1.01 ms; the
p50 and the zero collections are the numbers rules one and two ask for. A daemon with
no `contexts` section pays two comparisons per tick and nothing else, since the
engine holds no conditions to read attributes for.

Verified live: detection of a full-screen Firefox on F11, with the report saying
`firefox is full-screen`; the 300 ms linger holding at 150 ms after leaving and gone
by 1.35 s; `on-enter` and `on-exit` firing once each in the fixed order, visible on
the wire as the on-exit `signal` arriving before `context.changed`; gaps collapsing to
zero and coming back on `--auto` (window rect 0,34 3840x2126 pinned, 5,39 3830x2111
released); a 4 s `--ttl` expiring by itself with `expires in 3.5 s` in the report; a
`--lease` from a held pipe attributed to `pwsh.exe (pid …)` and released the moment
the pipe closed; the bindings overlay through the real keyboard hook - a key that
is unbound in the base file did nothing, fired the overlay's command while the
context was pinned, and did nothing again after `--auto`; the refusals for an
undeclared name and for `--lease` from the command line, each with its hint.

**Found on the way:** Dalil kept its process across a `--replace` for the fourth time
and reconnected on its own; still not this feature's problem. Nothing else.

### Phase 3

The two consumers. Small by design: the pipe already served everything they needed,
which was the claim the symmetry in "The line" made, and this phase is the test of
it. Neither process learned a new query beyond `contexts` for the palette's
completions, and neither parses a `context.changed` payload.

**How it is shaped, and why:**

- **Both re-read the snapshot on `context.changed` rather than patching from the
  payload.** The payload names the one context that flipped; the snapshot lists every
  context in force in declaration order, which is the order they cascade in. A list
  assembled from arrivals would drift from it in order and, after a missed event, in
  content. Contexts flip seconds or minutes apart; one `query state` each time costs
  nothing worth saving, and it means the bar, the palette and `shubbak status` never
  disagree about the order.
- **The bar raises `ContextsChanged` before `ActiveWorkspaceChanged` on the same
  refresh.** The workspace handler is what picks the profile, and it reads the
  contexts the bar was last told about. The other order picked a profile with stale
  contexts and corrected it a moment later - two log lines and a rebuild for one
  flip.
- **A bar rule's `context=` is an attribute like `workspace=` and `monitor=`, and
  every attribute has to hold.** No negation: first match wins, so "not presenting" is
  written by putting the context's rule first and the general rule after it. The
  loader reads the declared names off the same document - names only, from the
  `contexts` node, so the bar's loader never disagrees with the window manager's about
  what a context means - and warns (`TAJ0020`) with a guess for a rule on one nobody
  declared. A warning and the rule kept, since the rule is correct as written and can
  never match, which is worth saying but not worth losing the bar over.
- **`{{ contexts }}` is one value, joined with a comma and a space.** The way
  `shubbak status` spells the same list. Empty when none hold, so the widget hides.
  Not one value per context: a `when value=` on the joined string matches only when
  exactly one holds, but a profile rule covers "change the bar while presenting"
  properly and per-context keys would have been a second way to say the same thing.
- **The palette's pill precedence moved into `WmStatus.Pill()`.** Offline, suspended,
  paused, binding mode, contexts - each quieter than the one before, and the box has
  room for one. It lived inline in the renderer, untestable; now it is a method on the
  record with a test per step, and the renderer calls it.
- **`from="contexts"` offers every declared context, held or not.** The action is how
  the others are turned on. `CompletionSources` gained `Contexts`, appended and
  optional like `Monitors` before it; the composer maps `--clear`, `--toggle` and
  `--auto` to the context name, and `--set` already meant "this verb's own argument".
  Dalil's `WmStatus` gained `Contexts` the same way. Nothing that constructed either
  had to change.
- **Dalil says so when the window manager leaves.** It stays and reconnects, which is
  deliberate and documented in 0.9.1-palette - a restarted window manager without a
  palette is what that avoids. But it did so silently: no line at any level, so a log
  read as an event that never arrived, and the note above called it a problem four
  times. One `Info` line in `Dispatch`, and the behaviour is unchanged.

**Not done, deliberately:**

- **No palette mode for contexts.** A mode is a prefix character, a place in the tab
  ring, a help entry and a test that every mode reaches its own rows; the verb with
  completion and a `param from="contexts"` cover toggling, and `shubbak contexts` is
  where the reasons are. If a list with reasons in the palette turns out to be wanted,
  `query contexts` already returns everything it would show.
- **No `when of="contexts"` per-context matching on the bar.** See above.
- **Taj does not validate `context=` against the window manager.** It validates
  against the file, which is the same file. A context declared in the file and
  refused by the window manager's loader is reported by that loader.

**Measured** (release, NativeAOT, same SDK):

| binary | Phase 2 | Phase 3 | delta |
|---|---|---|---|
| shubbak-wm.exe | 6.05 MB | 6.05 MB | 0 |
| shubbak.exe | 4.83 MB | 4.83 MB | +2 KB |
| taj.exe | 5.01 MB | 5.01 MB | +4 KB |
| dalil.exe | 5.08 MB | 5.08 MB | +7 KB |

The daemon is untouched, which is the point of the phase. The bar and the palette
grew by the size of a rule attribute, a pill precedence and a completion source.

Runtime cost lands on the two consumers, not the daemon: each bar makes one extra
`query state` per context flip - two bars, so two queries per flip, seconds or
minutes apart - and the palette makes one extra `query contexts` per open. Idle
cost is nothing: neither process does anything between events it did not do before.
Working sets a minute after a restart on the real config: shubbak-wm 27.3 MB, taj
21.8, dalil 19.0; before the restart, after 38 minutes and 51 minutes respectively,
taj was 39.3 and dalil 20.2, which is the ordinary spread of a bar that has painted a
while and a palette that has been opened.

The Phase 2 daemon, measured on the real configuration - no `contexts` section -
just before this restart, 38 minutes up: tick p50 0.02 ms, p99 1.01 ms, allocation
p50 0 B, one gen0 collection. Identical to Phase 1 settled. That is the "two
comparisons per tick" claim from Phase 2, measured.

Verified live, with a temporary copy of the real configuration carrying three
contexts, `rule use="presentation" context="video"`, a `{{ contexts }}` widget beside
the binding-mode pill, and an action asking `from="contexts"`: `check-config` on a
rule naming an undeclared context printed `TAJ0020` with a caret under the name and
`Declared: video, meeting, two-screens`; `context --set video` switched both bars to
`presentation` once each, the log line reading `in context video, two-screens`, and
`--auto` switched both back once; the palette opened on the temporary configuration,
read its lists including the new query with no warning, took `context --tog` and
closed. The bar's widget and the palette's pill were not inspected by eye - the
values behind them are the ones the log lines and the snapshot showed, and the
drawing is the same code that draws the binding mode.

**Found on the way:** the daemon's `--config` does not reach the processes its
`startup-command` launches; they resolve their own path. Obvious once seen - each
process reads the file itself - but the Phase 1 and Phase 2 live runs were made with
the daemon on a temporary copy and the bar and palette on the real file, which did
not matter then and would have hidden everything here. Restarted both with
`--config` by hand. Worth a line in the README's testing notes if anyone else does
this; not worth plumbing, since the answer for a real user is "edit the file".

### Phase 4

Ayn, the reference provider. Built under the working name Rasid (راصد, "observer")
and renamed before release: راصد is a dictionary word, عين - eye - is one everybody
uses, and the icon was already an eye. Everything below reads Ayn; the commit that
added it reads Rasid. The design note said it exists as much to dogfood
the pipe as to detect meetings: if it was painful to write, the pipe was wrong. It
was not painful. The whole provider is a config record, a state machine of two
booleans per device, a registry reader, and one connection that sends two commands.

**How it is shaped, and why:**

- **Two projects, like the bar and the palette.** `Ayn.Core` is pure: the `ayn`
  section, the consent-store model, and `Provider`, which takes readings and a clock
  and hands back commands. The registry and the pipe live in `Ayn`, the host, and
  are the two things a test cannot have. Thirty-one tests cover the debounce, the
  flicker, the lost lease, the rename under a running watcher, and the spelling of
  every command; the host is a hundred and fifty lines of wiring.
- **The consent store, not a device API.** Windows writes `LastUsedTimeStart` and
  `LastUsedTimeStop` for every program that opens a camera or microphone through the
  capture pipeline, packaged or not, under
  `CapabilityAccessManager\ConsentStore\{webcam,microphone}`, so that the settings
  page can say "currently in use". A start with no stop is the device open. It is the
  same signal the shell's own privacy indicator reads, it names the program, and it
  costs a registry read. Both hives are watched; the machine's is where a few services
  and older installers land.
- **Woken, not polled.** `RegNotifyChangeKeyValue` on each key with an event, the
  whole subtree, names and values both. Re-armed before the read on every wake, or a
  change landing between the two would be missed. Between wakes the process holds no
  timer, unless a change is waiting out its settle time, in which case it sleeps
  exactly that long. The one P/Invoke in the process; `Microsoft.Win32.Registry` does
  the reading, as the CLI's autostart already did.
- **Settle, on the provider's side.** A call opens and closes the camera several
  times while it enumerates devices. The window manager's `linger` covers the way
  down; nothing covered the way up, and a provider that set and released three times
  in a second would make the window manager's log look like a fault. So a change is
  believed after it has held for `settle` (500 ms by default), and a change that
  reverses itself inside that time is never reported at all. A test pins each.
- **`--lease` up, `--auto` down.** The lease is what makes a crashed watcher
  harmless. Handing back with `--auto` rather than `--clear` leaves no pin, so the
  report says `nothing has set it` and a context somebody else also sets is theirs
  again rather than held off by us.
- **Two connections, because one cannot do both jobs.** A subscribed connection
  carries nothing else, by the protocol's rule, and the connection that holds a lease
  must stay open. So commands go over one client, opened on first use and kept, and
  `config.reloaded` and `wm.shutdown` arrive over a second that reconnects for as
  long as the process runs - the palette's pump, minus the payloads. **This is the
  pipe's one rough edge for a provider:** the second connection exists only to learn
  that the file was reloaded and that the window manager left. A protocol that let a
  subscribed connection also send would make a provider one connection, and that
  would be a v3 change or an appended capability; noted, not done.
- **The lost lease is a state, not an event.** When the events connection ends the
  loop is told, drops the commands client, and tells the provider to forget what was
  asserted. The next flush holds again whatever is still in use, at once - the settle
  time was served the first time round - and nothing for a device that went quiet,
  since there is no pin left on the new window manager to hand back. A daemon
  restart with the camera on was measured at 400 ms from the shutdown notice to the
  new pin.
- **Refusals are said once and treated as sent.** The likeliest one - a context the
  file does not declare - would repeat on every change until somebody declares it. A
  reload or a reconnect asks again.
- **`ayn-exit` through a named event.** The bar and the palette are stopped with
  `WM_CLOSE`; a process with no window has nothing to close, so it holds
  `Local\shubbak-ayn-stop-<SID>` open and the CLI sets it, then waits for the
  instance mutex to be released so a script that stops and restarts does not race.
  `IpcProtocol.StopEventNameFor` sits beside `InstanceMutexNameFor`; a naming
  helper, not a wire change.
- **`--report`.** What Windows says is using each device right now, the same reading
  the watcher acts on, for the person wondering why a meeting was or was not
  noticed. On this machine: fourteen programs have used the camera, twenty-six the
  microphone, none open.
- **Zero is a value.** The first cut used `TimeSpan` with zero for "unsaid" and a
  test for `settle 0` caught it: zero means believe at once, and a person can mean
  that. Nullable now.

**Decided along the way:**

- **`camera #false` turns a device off; `camera ""` is a slip** (`AYN0002`), because
  an empty name is far more likely a mistake than a decision.
- **Ayn stays when the window manager leaves**, as the palette does, and says so at
  Info. The restarted window manager's startup command finds it already running.
- **No live re-read of the file except on `config.reloaded`.** The daemon only
  publishes that when it accepted the file, which is the right gate.
- **Not started on this desk yet.** The real configuration declares no `camera`
  context; adding one and the `startup-command` is the user's call.

**Not done, deliberately:**

- **Which program** is in the log (`camera in use by ms-teams.exe`) and not in the
  context. A context is a boolean by design; the report attributes the pin to
  `ayn.exe`, and the log has the rest.
- **No Teams, Zoom, OBS or calendar.** Each is a vendor SDK or a scrape, and each is
  the same shape as this: a held connection and two commands. The README says so.

**Measured** (release, NativeAOT):

| binary | Phase 3 | Phase 4 | delta |
|---|---|---|---|
| shubbak-wm.exe | 6.05 MB | 6.05 MB | 0 |
| shubbak.exe | 4.83 MB | 4.86 MB | +30 KB |
| taj.exe | 5.01 MB | 5.01 MB | 0 |
| dalil.exe | 5.08 MB | 5.08 MB | 0 |
| ayn.exe | - | 4.43 MB | new |

The daemon, the bar and the palette are untouched. The CLI carries `Ayn.Core` for
`check-config` and the `ayn-exit` verb. The watcher is the smallest of the five:
no GDI, no window, one P/Invoke.

The watcher idle, published build, thirty seconds after start on the real
configuration: working set 14.4 MB, private 5.5 MB, six threads, 0.02 s of CPU - all
of it start-up - and no timer running. That is the "one material cost is a process"
line from the performance rules, measured: fourteen megabytes resident for a fact the
window manager will never read itself, against tens of kilobytes had it been built in.
The design holds that the megabytes are the right price for the topic staying outside;
this is what they come to.

Verified live: `ayn --report` read both stores; with a temporary configuration
declaring `camera`, `microphone` and `meeting { when { context "camera" } }` and
`settle 300`, opening the Windows Camera app produced `context --set "camera" --lease`
and the window manager's report read `pinned: set by ayn.exe (pid 49020), leased to
that connection`, with `meeting` composed from it, 1.06 s after the app was launched
including the app's own start; closing it produced `context --auto "camera"` and both
went. A `--replace` of the daemon with the camera on: `the window manager is shutting
down`, `went away; holding nothing until it is back`, reconnected, held again - 400 ms
end to end. `shubbak ayn-exit` stopped it and the log said `ayn stopped`; a
second call said `no watcher is running`.

**Found on the way:** the README's test count, which CI checks against the tree, had
been 1576 since before Phase 0 and the tree said 1749; every push since had been
failing that one check. Corrected to the counted figure (1774 with this phase) and
the project count to ten. The `Dalil does not leave on wm.shutdown` caveat under Phase
0 is resolved: deliberate, documented, and since Phase 3 logged.
### Phase 5

Saved arrangements. The last of the five, and the one the motivating list ended with:
demos that need the same arrangement of windows back after they have been dragged
around. Everything else in this note is about facts arriving; this is the one thing
that is a record.

**How it is shaped, and why:**

- **It records what the session file deliberately does not, and the reason is the
  moment each is applied.** The session file says which workspace a window belongs to
  and is applied while windows arrive in whatever order Windows enumerates them at
  start-up - no order to rebuild a tree in. An arrangement is asked for by name when
  every window is already here, and so it can record the shape: containers, layouts,
  ratios, and which window sits in each leaf. Both identify a window by process and
  class, with the title hashed and the path kept, for the reasons the session file
  already gave.
- **Capture is pure and lives in Core beside the session store.** Only what tiles: a
  floating window is in the tree and takes no part in dividing it. A container left
  with one child by that omission is recorded as the child at the container's share,
  which is what the tree would have done itself. A `stackalloc` for the ratios and a
  handful of small allocations per save, which happens when somebody presses a key.
- **Restore is a state-machine operation with three decisions written down.** Only
  the windows on the workspace, and only the ones that tile: an arrangement says how
  a workspace is divided, not which windows belong on it, and pulling a window in
  from another workspace because it matched would take something the user put there
  on purpose. A recorded window that is not open is left out and its share goes to
  its siblings; a container left with one child becomes the child. A window the
  arrangement never knew stays after the rebuilt tree, with the share it would have
  had as one more child - so the recorded pair keep their 0.6 / 0.4 inside two
  thirds and the stranger has a third. Nothing is hidden and nothing is moved off the
  workspace; the worst a restore can do to a window it does not know is put it last.
- **Refused rather than done as a no-op when nothing recorded is here.** A restore
  that quietly rearranged nothing would leave the person pressing the key again.
- **Matching is greedy in recorded order, best score first, each window once.**
  Process and class must agree; the title hash and the path each add one. The
  ambiguity is between windows of one program, and those two are there to settle it;
  a test pins that two Firefox windows swapped by hand come back to their own sides.
- **`ContainerNode.SetRatios` is new and internal.** Setting a recorded set of shares
  one at a time through `SetChildRatio` scaled each earlier one as the next was
  applied; the whole set at once, then normalise, is what a rebuild needs and what
  nothing else did.
- **A host action, like `context`, with the answer routed back.** Two of the three
  actions touch a file and the third needs what the file says, so the executor hands
  all three to the host; the daemon intercepts them in `RunCommand` as it does the
  pin, so the pipe reply carries "no arrangement called X, saved: a, b" rather than a
  bare success. The store is loaded once at start and rewritten on save or delete;
  the file is `arrangements.json` beside the session file, written the same atomic
  way.
- **One new topic, `arrangement.restored`, inert.** The `LayoutChanged` raised beside
  it is what moves the windows; this is the account: placed, missing, kept. A restore
  that found only some of its windows is a success with a number in it, and the
  number is what a script or a log wants. The daemon logs the same line.
- **`ArrangementInfo` on the wire carries milliseconds, not a `DateTimeOffset`.** The
  first cut carried the struct and every client grew 62 KB for the ISO-8601 converter
  it pulled in; as a `long`, like every other time on the wire, half of that came
  back. The other half is the verb itself, which every client parses.

**Found on the way:**

- **The session file's title hash never survived a restart.** `string.GetHashCode` is
  seeded per process; a hash written by one window manager could never match in the
  next, so the tiebreaker it exists to be had never broken a tie across a restart.
  FNV-1a now, shared by both files; a test pins the number for one string so the
  algorithm cannot drift.
- **`system-state` did not know "away".** The Native tests first ran on a locked
  machine at five in the morning and said the probe had failed. It had answered
  `QUNS_NOT_PRESENT`, and the mapping called that `Unknown`. It is `Away` now, with a
  wire name, and a full-screen Store app (`QUNS_APP`) is `FullScreenApp` rather than
  `Unknown` too. The snapshot said `activity = away` the moment the daemon restarted.
- **Under the lock screen, window-targeted commands are refused.** The real foreground
  window is the lock app, which is unmanaged, and the gate that stops a command
  running against a window the user is not looking at stops all of them. Correct, and
  it meant the live shaping of a tree by `split`, `move` and `resize` could not be
  driven while the screen was locked; the nested shape was verified by writing an
  arrangement to the file and restoring it instead, which exercises the same rebuild.
- **A copy over a just-exited daemon's binary produced a file of the right length and
  the wrong hash**, and Windows refused to start it as corrupt. Re-copied and
  verified by hash before starting. The deploy step now compares hashes.

**Measured** (release, NativeAOT):

| binary | Phase 4 | Phase 5 | delta |
|---|---|---|---|
| shubbak-wm.exe | 6.05 MB | 6.26 MB | +209 KB |
| shubbak.exe | 4.86 MB | 4.90 MB | +40 KB |
| taj.exe | 5.01 MB | 5.04 MB | +30 KB |
| dalil.exe | 5.08 MB | 5.12 MB | +38 KB |
| ayn.exe | 4.43 MB | 4.46 MB | +30 KB |

The daemon carries the store, its JSON context, the rebuild and the DTO. The three
processes with no arrangement code of their own carry the verb - record, parser
case, catalogue entry - and the `ArrangementInfo` DTO, at about 30 KB each; the
palette adds the completion.

The Phase 4 daemon, measured on the real configuration just before this restart, 48
minutes up: tick p50 0.02 ms, p99 0.76 ms, allocation p50 0 B, p99 648 B, no
collections in any generation. The lowest p99 of the series, and not a speed-up:
those 48 minutes were the middle of the night with the screen locked, so almost no
window events reached the daemon, whereas the baseline was 11 hours of daytime use.
A p99 is a measure of the busiest ticks in the window, and this window had none. The
claim the series can make is no regression; a like-for-like figure needs a working
day on this build. Nothing in this phase runs on the tick: an arrangement is saved or
restored when a key is pressed, and the store is read once at start.

Verified live, on an empty workspace with three Notepad windows so nothing of the
user's was touched: `arrangement --save demo` wrote the file with process, class,
hash and path and no title; `--restore demo` on a tree that had grown a
single-child container flattened it, with `layout.changed` and
`arrangement.restored {"placed":3,"missing":0,"kept":0}` on the wire; a hand-written
nested arrangement - `[0.6 | splitv 0.4 [0.3 / 0.7]]` - loaded on restart and
restored onto the three flat windows gave exactly that tree, 2292 px beside 1528,
630 above 1471; every refusal named the case (`No arrangement called "nope". Saved:
demo, nested.`; `Nothing tiles on workspace "5"`); `--delete` twice said so the
second time; `arrangements` listed and then said `no arrangements saved`. The
Notepads were closed and the focus returned to where it had been.

### Phase 6

Not in the plan: asked for after the five shipped. Icons on the bar for a meeting,
for the camera, for the microphone and its mute, and a mute button. It is the first
thing built *on* the series rather than *as* it, and so the first test of whether the
pieces compose without new ones.

**What it took, and where each piece went:**

- **Two facts the watcher did not have, and a name scheme for facts.** The mute is a
  system fact - the default microphone's endpoint mute, the switch the Sound settings
  toggle - which is the same topic as the two the watcher already supplied, so it went
  to Ayn and not to the daemon. With three facts the flat names would not do:
  `camera`, `microphone` and then what for the mute? Facts are `subject-state` now -
  `camera-in-use`, `microphone-in-use`, `microphone-muted` - so the ones about one
  device sort together and a `speaker-muted` slots in without renaming; the `ayn`
  section nests by subject the same way. The names were unreleased, so the rename
  cost nothing but the user's file.
- **Core Audio by hand.** `IMMDeviceEnumerator`, the default communications capture
  device with the console device as fallback, `IAudioEndpointVolume` for `GetMute`
  and `SetMute`, and the two callbacks - `IAudioEndpointVolumeCallback` for the mute
  and `IMMNotificationClient` for the default device changing - as objects we *are*:
  a pointer to a vtable of `[UnmanagedCallersOnly]` functions, allocated once, freed
  after unregistering. Raw vtables as the shell's view collection is reached, because
  the callbacks are the reason and no generator makes those. Each callback does one
  thing: sets an event the loop already waits on. The loop reads the endpoint rather
  than remembering what the callback said, so there is one copy of the truth.
- **The mute is never settled.** The settle time exists because a call opens and
  closes the devices while enumerating them. A mute changes when a person presses a
  key, and that person wants the icon now. `Fact.Settles()` says which is which.
- **A signal is how the bar mutes.** `signal "ayn" "microphone" "mute" | "unmute" |
  "toggle-mute"`: subject then verb, so the next subject slots in and the line reads
  as a sentence. The window manager carries the words without reading them, exactly
  as it carries the palette's; Ayn subscribes to `signal` on its events connection,
  parses the arguments in Core where a test can see, and the loop does the deed. It
  does not then update the provider: the endpoint's own change notification is the
  next wake, so a request from the bar and a change made in the Sound settings take
  one path. **The window manager never learned the word "mute".** That was the test
  of the line, and it passed.
- **One bar widget per context.** `{{ contexts }}` was a joined list, which can be
  read but cannot light one icon. Now every context has a source of its own,
  `context.<name>`, set to the name while it holds and written empty once when it
  drops - the empty write is what hides the widget. A new filter, `then:X`, is the
  other half of `default:X`: something when there is a value, nothing when there is
  not. A widget and a `when` block take a `font=`, so one widget draws from Segoe
  Fluent Icons beside text in the profile's face. `TAJ0021` points out a
  `context.x` the file does not declare, with a guess, the way `TAJ0020` does for a
  rule.
- **Policy stayed in the file, and got sharper.** A meeting is the microphone being
  open: Teams and its kind keep the device open for the whole call, muted in-app or
  not, so it is the reliable "in a call" signal where the camera would miss every
  audio-only call. "Muted, but only while in a meeting" is
  `when { context "meeting"; context "microphone-muted" }`. And the first cut of the
  bar showed a transparent microphone beside the red one, still holding its width -
  fixed with no code, by a twin context with the condition negated,
  `when { context "meeting"; !context "microphone-muted" }`, so exactly one of the
  two holds during a meeting and the bar shows exactly one glyph. The composition
  the design promised, doing the work a `when` colour could not.

**Found on the way:**

- **`ayn --report` opened the live log and truncated it under the running watcher.**
  Every process opens its log with rotation on start; a second instance of the same
  process, even one that only prints and exits, rotates the first one's file out
  from under it, and the first one's later writes left a hole of zero bytes. A
  report opens no log file now. The same would bite any of the five run twice.
- **KDL v2 spells a Unicode escape `\u{E722}`,** and the config parser said so with
  the caret under the offending `\u` - the first time the file talked back to its
  own author's assistant. Recorded because it will happen to everyone who pastes a
  glyph code from the Fluent Icons page.
- **The Debug build is not the measurement.** 41.8 MB resident and 0.31 s of CPU in
  two minutes, most of it the JIT; the published build below is a different program
  in that respect, and the numbers in this note are always from `dist\`.

**Measured** (release, NativeAOT):

| binary | Phase 5 | Phase 6 | delta |
|---|---|---|---|
| shubbak-wm.exe | 6.26 MB | 6.26 MB | 0 |
| shubbak.exe | 4.90 MB | 4.90 MB | +10 KB |
| taj.exe | 5.04 MB | 5.05 MB | +6 KB |
| dalil.exe | 5.12 MB | 5.12 MB | 0 |
| ayn.exe | 4.46 MB | 4.52 MB | +66 KB |

The daemon and the palette did not change by a byte of source; the CLI carries the
new section's loader for `check-config`; the bar carries a filter, a key and a
warning; the watcher carries Core Audio.

The watcher idle, published build, on the real configuration: working set 17.7 MB,
private 6.8 MB, five threads, 239 handles - and **0 ms of CPU over 120 seconds**,
measured against the palette's 0 ms, the bar's 359 ms (its half-second clock) and
the daemon's 94 ms (its tick). A first sample had shown 15 ms in a minute; that was
the tail of the toggles just made and the log writer flushing them, and a clean
window showed none. There is no loop: the process waits on eight handles - stop,
the window manager leaving, a reload, a signal, the endpoint's callback, and one per
watched registry key - with no timeout unless a change of use is waiting out its
settle time.

Verified live, and this time by eye: the bar was photographed. With `meeting` pinned
a green microphone glyph appeared between the second clock and the layout icon;
`signal ayn microphone toggle-mute` turned it into a red pill with the mic-off glyph
1.2 s later (the round trip itself was measured at 160 ms from signal to
`microphone-muted` holding, most of it Core Audio's notification); toggling back
brought the green one back; `--auto` on the meeting removed both. `ayn --report`
said `microphone mute: muted` while it was. Refusals: `signal ayn camera mute` and
`signal ayn microphone louder` each logged one line naming what is accepted.
---

## The series, in one table

Five phases, tag `pre-contexts` (`73fc04a`) to the commit that ships this section.

| phase | commit | what | daemon binary | tick p50 / p99 | tests |
|---|---|---|---|---|---|
| baseline | 73fc04a | 0.9.0 in `dist\`, 11 h 36 m up | 5.62 MB | 0.03 / 1.09 ms | 1576 methods (README) |
| 0 | 3fe9ca1 | publish what was known: `window.native_fullscreen`, `wm.environment`, `binding.fired` | 5.67 MB | 0.01 / 1.34 ms (fresh) | |
| 1 | ffd7028 | named monitors, re-homing, topology probe, `move-workspace --monitor` | 5.74 MB | 0.02 / 1.01 ms (1 h 14 m) | |
| 2 | fe5d512 | contexts core: config, engine, `context` verb, effects, reports | 6.05 MB | 0.02 / 1.01 ms (38 min) | 1749 declared |
| 3 | e6da993 | bar `rule context=`, `{{ contexts }}`, palette pill, `from="contexts"` | 6.05 MB | daemon untouched | +22 |
| 4 | 69f9b83 | Ayn, the reference provider; a fifth executable | 6.05 MB | 0.02 / 0.76 ms (48 min) | 1774 |
| 5 | 941866f | saved arrangements; `away`; a stable title hash | 6.26 MB | 0.02 / - (2 min) | 1800 |
| 6 | this | bar icons per context, `then:`, `font=`; Ayn: three facts, mute, signals | 6.26 MB | daemon untouched | 1812 |

Working set of the daemon across the series: 41.3 MB at the baseline after 11 h, 45.6
MB after 38 min on Phase 2, 27-33 MB in the first minutes after each restart; the
spread of a process that has or has not yet painted much, not a trend. The bar and
the palette are within a few megabytes of where they started. Ayn, when it runs, is
14.4 MB resident and holds no timer.

What the six performance rules came to: no new wake-up was added in any phase; the
context engine allocates nothing when nothing changed and is pinned by a test; the
keyboard hook does one lookup as before; there is no window catalogue; every new
probe sits behind an existing gate; and every phase's numbers are above. Read the
tick column as "unchanged", not "faster": the p50 moved by one unit of the counter's
resolution and the p99 tracks how busy the desk was in each window - the baseline was
a full working day, the later figures were quiet evenings and one locked night. The
series added nothing to the tick that is paid when its features are unused, and that
is all the column shows. The one
prediction that was wrong was the binary: 200-400 KB expected for the whole feature,
640 KB delivered for the daemon, most of it the closed, typed vocabulary of
conditions and effects and the JSON for their reports - the price of a config that
talks back rather than a string bag.