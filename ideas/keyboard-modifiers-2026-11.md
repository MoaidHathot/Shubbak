# Keyboard modifier correctness and performance

2026-11-04/05, Windows x64, .NET 10.0.14 NativeAOT, 32 logical processors and two
4K displays. Follow-up to `keyboard-latency.md`.

## Changes and evidence

Each correction was committed separately. The regressions cover:

- Canonical chord identity, including modifier order/case/key aliases, mode-local
  duplicates, and the different precedence of context overlays.
- Effective mode exits: shadowed escape bindings, self-loops, closed cycles,
  command order, duplicate mode declarations and side-specific shadowing.
- AltGr versus plain/Shift-only bindings and Dalil's Ctrl editing/navigation actions.
- Real Windows keyboard layouts: German `@`, Polish `ć`/`ń`, US Ctrl+Right-Alt,
  US International dead-key preservation, and all eight modifier sides. Language
  changes while Ctrl+Right-Alt remain held are covered in both directions; keeping
  the layout from modifier-down failed both regressions.
- Captured binding/mode identity through queueing, repeat, release and reload.
  Release bindings now run on main-key release regardless of modifier release order.
- Both ownership directions, already-held keys at hook installation, desktop-switch
  recovery, failed probes/wakes, queue overflow and reserved release delivery.
- Bounded drains re-arming a coalesced wake, and non-repeating holds producing one
  queue entry/wake rather than a stream of redundant work.
- Windows' actual menu command/foreground behavior: ordinary Alt taps, consumed
  Alt/Win chords, either release order, blocking modes and whole input batches.
  An additional real regression found that repeating Win after the main key was
  released re-armed the shell. Suppressing that redundant modifier down fixes it;
  injecting another mask during that callback was too early and did not fix it.
- Caller-owned lists: re-submitting an edited overlay, preserving loaded defaults
  during subsequent overlay compilation, and preserving the published overlay on
  reload. Final review caught and removed an invalid reference-equality shortcut.

The final full solution passed **3,785 cases with coverage**, including the final
list-ownership and held-language-switch corrections. The tree declares 2,893 test
methods (theories produce multiple cases). All 39 documentation snippets and 30
example checks passed; the shipped example and the user's configuration validated.
Release-version references agree. All five programs publish as NativeAOT for x64
and ARM64; ARM64 is cross-built/header-checked here, with runtime tests on x64.

The callback remains allocation-free after warmup. Binding descriptors are built
when configuration changes, not when keys arrive. Side-specific combinations are
expanded there, preserving one dictionary lookup on a fresh press. Releases use the
captured descriptor without another lookup/state read; non-repeating holds bypass
lookup, state reads, queue publication and wakeup. Modifier-only events pass directly.
Layout translation runs off the hook thread, on startup or an explicit cache miss,
using `ToUnicodeEx`'s non-mutating flag. It adds no polling thread or timer.

## Builds and retained measurements

- Baseline: `7bbdda1`, saved before editing. Window manager SHA-256:
  `6B0C31C001FF8384B61A2E24267B5C45FFDDDF8C036E68088A7CA6A075F0DB6C`.
- Profiled candidate: `abe482d`. Window manager SHA-256:
  `708747E576FD89E35580193F50D95050A8DD78C3F3FD6C4AB13DACD649502813`.
- Main receiver/probe: `5768e4d`, SHA-256:
  `5217D7C55D702790868936A977C503E738B4EE23D5E540854679F0D32D4B69EB`.
- The later `75b5621` correction snapshots binding lists during load/reload.
  `e5a112c` samples the current foreground layout for held Ctrl+Right-Alt pairs,
  instead of retaining the layout from modifier-down. The main matrix describes
  the profiled candidate; the final Right-Alt measurements below exercise that
  last change in the final native build. No foreground-layout query is added to
  ordinary typing or left-Alt/Ctrl+Shift shortcuts.
- Final build manifests/hashes are in `final-binaries.json`; the x64 programs are
  deployed together in `dist`. The final x64 version checks and portable manifest
  checks pass. The final window manager's SHA-256 is
  `8C20C4F93A5D26D2ED7F842F7A00AFA94144E04CE753B4ABBBD9AD17001EE789`.

Raw timestamps, JSON summaries, binary hashes, process CPU/private-memory counters,
diagnostic reports and coverage are retained under
`artifacts/keyboard-review-20261104/`. `summary.json` aggregates accepted cases.
Version order is reversed on the second round. Builds/tests did not run alongside
measurements. Synthetic events are tagged and checked at a real foreground receiver;
bound events are checked separately for expected notification count and key leakage.
Their signal payloads are identical, so that fixture does not independently establish
per-notification identity or ordering.

Several early runs lost foreground to other applications and were rejected. The
receiver now requires 750 ms of stable foreground **before** timing starts; it never
repairs foreground during a measurement. Any subsequent change still invalidates
the run. Partial matrices and the short smoke run are excluded from the totals below.

## Application input delivery

Milliseconds, per-run ranges over two reversed-order rounds, 20,000 measured edges
per case after 1,000 warmup edges. The CPU follow-up uses 10,000 edges per case.

| Workload | Before median | After median | Before p99 | After p99 |
|---|---:|---:|---:|---:|
| Idle | 0.563–0.564 | 0.560–0.565 | 1.460–1.536 | 1.412–1.418 |
| 28 CPU workers | 1.102–1.118 | 1.224–1.270 | 3.638–3.808 | 3.608–3.832 |
| CPU follow-up | 1.213–1.297 | 1.142–1.186 | 3.558–3.592 | 3.561–3.589 |
| Two animated windows | 0.634–0.650 | 0.647–0.648 | 4.127–4.215 | 4.284–4.352 |
| Four continuous IPC clients | 0.836–0.845 | 0.834–0.847 | 1.481–1.539 | 1.469–1.599 |

The stopped idle baseline was median 0.531–0.539 ms, p99 1.395–1.448 ms. The hook
has a small measurable cost. The first CPU comparison's higher candidate median did
not persist in the reversed-order follow-up. Some tails are higher, including the
animation-delivery p99; these are retained rather than described as improvements.

## Bound command delivery

Key-down to signal notification, including dispatch and IPC. This is not callback
execution time. Main cases retain 5,000 notifications per run; the longer animation
follow-up retains 10,000. Again, two reversed-order rounds.

| Workload | Before median | After median | Before p99 | After p99 |
|---|---:|---:|---:|---:|
| Idle | 0.167–0.175 | 0.170–0.186 | 1.899–2.290 | 1.842–2.149 |
| CPU | 1.930–2.028 | 1.958–1.976 | 4.829–5.311 | 5.661–5.672 |
| Animation | 0.140–0.141 | 0.134–0.141 | 3.099–3.207 | 3.771–3.925 |
| Animation follow-up | 0.142–0.172 | 0.133–0.139 | 3.245–6.411 | 2.908–3.616 |
| IPC | 0.139–0.141 | 0.140–0.140 | 0.387–0.388 | 0.379–0.389 |
| Alt, idle | 0.400–0.481 | 0.404–0.415 | 1.380–2.008 | 1.345–1.389 |
| Alt, CPU | 2.626–2.738 | 2.564–2.659 | 5.578–5.783 | 5.099–5.763 |
| Ctrl+Shift, idle | 0.924–0.931 | 0.876–0.889 | 2.179–2.306 | 2.197–2.232 |
| Ctrl+Shift, CPU | 3.429–3.479 | 3.522–3.556 | 6.203–6.607 | 6.639–6.732 |

The final `e5a112c` native build was also measured with Ctrl+Right-Alt, 5,000
notifications per run, two runs: median **0.911–0.934 ms**, p99 **2.400–2.406 ms**,
p99.9 **4.502–6.624 ms**, maximum **10.744–13.943 ms**, with no input-integrity
failures. The fixture binds both the genuine chord and AltGr to the same test signal
so it need not change the user's layout. These final-path checks have no old-build
counterpart: the old parser cannot express the sided/AltGr bindings.

The initial animation-notification tail increase also did not persist in the longer
comparison. CPU-loaded tails varied; the measurements do not establish that every
percentile is faster, or identify scheduler/IPC tails as time spent in the hook.
Animation diagnostics retained zero frame-path allocation and approximately 60 Hz
cadence in both builds.

The Alt fixture counts `SC_KEYMENU` but prevents entering a modal menu loop, so the
comparison is not dominated by different application menu behavior. Across the four
baseline Alt runs there were **22,000 unwanted menu requests**, including warmup;
the candidate produced **zero**. Actual menu/Start behavior is covered separately
by the receiver tests.

## Allocation and resources

The production-hook pressure host recorded **0 B** over 20,000 measured lookup
callbacks across four hooked runs. Two of those runs each performed **83 forced
blocking Gen2 collections**. All input arrived, and hook faults/drops stayed zero.
Lookup median was 0.3 microseconds, p99 1.2–1.4 microseconds; these numbers are not
substituted for the end-to-end tables above. Receiver tests also verify zero warmed
hook allocation for claimed/unclaimed holds and repeated masked Alt chords.
The current-layout sampling path also passes its warmed allocation regression.

The normal-session comparison completed, per build, 100 palette activations, 40
configuration reloads, and input delivery before suspension, during suspension and
after resumption. A background application added a window during the first pass,
so its initial RAM snapshots are not used as a controlled comparison. A second
idle-only comparison verifies identical managed window handles and unchanged config
throughout both reversed-order rounds, 30 seconds per case:

| Resource | Before | After |
|---|---:|---:|
| Window manager private memory | 14.88–15.47 MiB | 14.80–15.11 MiB |
| Four resident programs, total private memory | 40.77–41.80 MiB | 40.55–41.34 MiB |
| Four programs' total CPU time per 30 s idle | 0–15.625 ms | 31.25–125 ms |

The last row is below 0.42% of **one core** for the whole candidate stack, with a
15.625 ms process-CPU counter quantum. It is not literally zero CPU, and a short
desktop sample includes background title/context/clock activity even with unchanged
window handles. There is no new periodic keyboard work. The memory ranges overlap.
The reload pass showed no accumulating handle/thread growth; private-byte snapshots
move with collection timing and are not a measurement of retained objects alone.

Normal-session input p99 was 1.689 ms before and 1.708 ms after. Palette foreground
readiness median was 3.143/3.147 ms, p99 6.994/6.208 ms. These are representative
checks, not a long-tail guarantee from only 100 palette operations or a perfectly
controlled comparison across that background window's arrival.

## Outcome

The accepted principal datasets contain **596,000 application input edges** and
**210,000 command notifications** across 78 cases. All tagged application edges
passed the missing, duplicate, reordered and wrong-edge checks. Notification runs
matched their expected counts and had zero leaked bound key events. Warmup, smoke
and rejected cases are excluded. The normal session and four resident processes
were restored.

The demonstrated improvements are corrected input semantics, removal of redundant
held-key work and second binding lookups, prompt bounded-queue continuation, and
allocation-free modifier/menu handling. Overall latency and memory remain in the
same measured range. The tables explicitly retain slower tail/CPU observations;
they do not support claiming a universal speedup, zero overhead, physical USB latency
or raw-input game-to-photon timing.
