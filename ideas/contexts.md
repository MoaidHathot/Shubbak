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
  will ever set `meeting`; `shubbak contexts` says `external - set by rasid.exe
  (pid 1234) 1.2 s ago, expires in 3.8 s`, or `never set`.

The reference provider is a fifth optional executable (working name **Rasid**, راصد,
"observer") that watches the camera and microphone consent store with
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
4. **Rasid.** The reference provider.
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
  is worth a look before Rasid copies the same reconnect loop.
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
