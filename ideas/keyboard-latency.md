# Keyboard responsiveness investigation

Started 2026-10-27 at `70e9052`, after the animation work and its CI correction.
The goal is prompt, correctly paired keyboard input during normal desktop use and
demanding workloads. Low callback execution time alone is not sufficient evidence.

## Method

Hardware: Intel i9-14900KF (24 cores, 32 logical processors), NVIDIA RTX 4070, two
4K displays. The native probe and real native daemons run on the same machine.

`spikes/Shubbak.InputProbe` timestamps tagged synthetic F24 edges before SendInput
and again in a separate application's foreground window procedure. Each tag has a
sequence number; missing, duplicated, reordered or wrong-edge events invalidate a
run. The receiver uses preallocated arrays and measures its own allocation. The
benchmark does not record ordinary user input.

The controller uses isolated daemon configuration/state, 98 bindings, and only
test-owned noise windows. It alternates daemon versions and reverses the order on
the second round. The default cases retain 20,000 measured edges per run after
1,000 warmup edges. Requested pacing is one pair every 2 ms; reported event rates
are measured and include the actual cost of injection and waiting.

Separate measurements cover F23 bound to an IPC signal. These are key-down to
subscriber notification, including command dispatch and IPC delivery, not window
input receipt. Another mode runs the actual KeyboardSource and BindingTable in a
separate process with a bounded live heap and forced blocking collections, so the
receiver can observe pauses before the hook enters managed code.

The stopped baseline includes Windows, other resident hooks and application message
delivery. It is not a zero-latency reference. Synthetic input does not establish USB
device-to-photon timing or a particular raw-input game's latency. The graphics
workload reports browser frame cadence separately from keyboard delivery.

## Versions and evidence

- Saved pre-animation binary: SHA-256
  `E12244947466FDE3A808A404DBD92B94B060F13F8E9332A51ABFDD63BCD98E5D`.
- Before keyboard fixes (`70e9052`):
  `D7C09A19989116657655ED97DDEA97762C7DA40C153FAFE5B8DF4C808C2A94A9`.
- `7d42701`: native input/command/GC/graphics probes and orchestration.
- `bf31292`: held-key ownership, input-desktop recovery, repeat and wake-failure fixes.
- `7f800e5`: retain release ownership when a release probe throws; guard the unmanaged
  desktop-event callback.

The local `artifacts/keyboard-*` directories retain JSON summaries, isolated configs
and diagnostic reports. Later runs also retain each tagged event's QPC timestamps.
Controller metadata records the actual binary hashes, sample counts and workload.

## Confirmed correctness defects

Four real receiver-based cases failed on the old hook:

1. A claimed Ctrl+key press, followed by releasing Ctrl while the key remained held,
   leaked an unbound repeat down but swallowed its up. The receiver saw `[down]`.
2. An application's unbound press, followed by Ctrl and a repeat, was claimed in the
   middle of the hold. The receiver again saw `[down]`, rather than `[down, down, up]`.
3. Both directions of a binding change during the hold produced the same defects.

The new byte-sized owner table remembers the original recipient until release. It
replaces the old 256-byte swallow table without growing it. Application repeats pass
directly through; they do not re-read modifiers or look up bindings. A key held before
the hook starts is also left with its existing recipient. Binding reloads no longer
mutate live key bookkeeping from the daemon thread.

Missing releases across input-desktop transitions are recovered by a passive event
hook on the keyboard thread. A real, disposable desktop switch verified recovery;
NotifyWinEvent with a null window did not synthesize that system event and was not
accepted as evidence. Ordinary foreground changes do not clear a held shortcut's
repeat state, which protects non-repeating commands such as close from retriggering.

Failure paths were checked against the application stream too. A throwing repeat
probe must pass the eventual release if it passes the repeat; a failed wake after a
command is published must not also forward its key. The final review reproduced an
unmatched `[up]` when a claimed release probe threw. Keeping its owner until the
probe finishes fixes that case as well.

## Baseline observations

The initial matrix delivered all 440,000 pass-through samples and 80,000 bound-command
samples without integrity failures. Idle medians were roughly 0.5-0.8 ms, including
the stopped baseline. With 28 normal-priority CPU workers, medians were around 1 ms.
The earlier animation changes showed no broad keyboard-delivery regression.

One CPU-loaded run had a 39 ms outlier. Its daemon had zero GC collections, zero hook
faults/drops and a maximum main-loop tick of 0.70 ms. That does not identify the cause;
it rules out a measured main-loop stall or collection as an explanation. The paired
follow-up CPU runs did not reproduce it. Maxima and ranges remain in the evidence;
they are not replaced with the best run.

## Paired native delivery results

Milliseconds, per-run ranges over two reversed-order rounds; 20,000 measured edges
per run. "Before" is the saved `70e9052` binary and "after" is the ownership-fixed
native candidate. The final release-error guard is also exercised by the subsequent
graphics, pressure and normal-session checks.

| Workload | Before median | After median | Before p99 | After p99 |
|---|---:|---:|---:|---:|
| Idle | 0.531-0.539 | 0.537-0.538 | 1.383-1.393 | 1.373-1.467 |
| 28 CPU workers | 0.987-1.015 | 0.891-0.899 | 3.296-3.640 | 3.046-3.131 |
| Animated test windows | 0.563-0.620 | 0.587-0.602 | 2.608-2.953 | 2.805-4.171 |
| Four continuous IPC clients | 0.716-0.726 | 0.719-0.726 | 1.174-1.196 | 1.173-1.581 |
| Hardware WebGL | 0.569-0.590 | 0.572-0.592 | 1.501-1.555 | 1.474-1.561 |
| WebGL + 28 CPU workers | 0.781-0.957 | 0.802-0.946 | 3.073-3.195 | 2.913-3.185 |

The paired idle **stopped** baseline was median 0.506-0.514 ms, p99 1.321-1.370 ms.
The active hook therefore has a small measurable cost; the result is not "zero
overhead". There is no consistent before/after slowdown across these workloads.
Animation and IPC tails varied between rounds, including a higher animation p99 in
one after run; the ranges above retain that result rather than hiding it. These are
observations on this machine, not universal latency limits.

All accepted runs preserved edge counts/order and had no duplicate or wrong-edge
input. Suspended runs also delivered every edge. The earlier isolated 39 ms maximum
did not recur in the paired CPU runs; the after CPU maxima there were 5.17-6.28 ms.

### Bound commands

5,000 measured command notifications per run, two reversed-order rounds. This metric
includes the signal subscriber and its scheduling, not only the daemon's execution.

| Workload | Before median | After median | Before p99 | After p99 |
|---|---:|---:|---:|---:|
| Idle | 0.156-0.293 | 0.190-0.210 | 3.075-3.154 | 1.889-1.895 |
| CPU pressure | 1.781-1.810 | 1.752-1.810 | 5.238-5.599 | 5.456-5.698 |
| Animation | 0.144-0.169 | 0.148-0.158 | 4.108-6.918 | 3.917-6.189 |
| IPC pressure | 0.133-0.138 | 0.137 | 0.369-0.370 | 0.345-0.357 |

There were no lost notifications or bound keys leaked to the application. Under CPU
saturation, a notification had a 31.08 ms maximum in one after run, versus 19.73-21.65
ms before. Ordinary application delivery is the separate table above; this cannot
be reported as a 31 ms keyboard-hook execution time. The measured median/p99 do not
justify adding priority escalation or speculative work to the callback.

### Graphics fixture qualification

The renderer was ANGLE/D3D11 on the RTX 4070, with a 4K framebuffer and 512 shader
iterations in the heavier runs; a sampled GPU load was 74% / 130 W. Its browser
frame cadence was measured separately. Initial combined-load trials exposed fixture
problems: the CPU workers were left active during browser teardown, and Chromium
could background an occluded renderer. Those trials were retained but excluded from
the final mixed-load comparison. Cleanup now signals all workers first, and the
documented `--disable-backgrounding-occluded-windows` testing flag keeps the renderer
active. Cadence recording starts after load startup. The accepted mixed-load checks
maintained approximately 16.7 ms median and 16.8 ms p99 cadence, without GL errors.

### Idle wakeups and bursts

An additional small run used 100 press/release pairs separated by one second, with
no discarded warmup. Stopped/active medians were 1.583/2.038 ms and maxima 9.520/7.787
ms. This checks waking from idle; 200 edges are not enough to claim a stable p99.9.

Eight pairs per injection burst gave stopped/active medians 4.149/4.693 ms, p99
9.881/10.836 ms, and maxima 15.750/24.715 ms over 20,000 measured edges each. The
queueing cost of serially delivering a burst is visible in both baselines. All edges
arrived correctly. This is intentionally more concentrated input than normal typing;
it is not equivalent to a keyboard's USB polling rate.

## Normal session and resources

With the user's normal configuration, five managed windows and all companions
running, 20,000 measured events gave **0.549 ms median, 1.611 ms p99, 2.649 ms p99.9**
and an 8.236 ms maximum. Every event arrived in order; the receiver allocated zero
bytes. The live daemon reported zero dropped queued keys, zero hook faults, zero
collections since restart and zero maximum frame-path allocation over 85 samples.

A ten-second idle snapshot of that session showed a daemon private size of 15.96 MiB
and working set of 34.10 MiB. All four resident processes had no process-CPU counter
increment in that sample: below the counter's resolution, not literally zero CPU.
These are resource observations, not a comparison with a long-lived session's heap.

## GC and measurement overhead

The separate production-hook host completed the corrected full-size pressure runs
without lost input, hook faults or queue drops while forced blocking Gen2 collections
occurred **148 times in each pressured-hook run**. Delivery p99 was 1.40-1.44 ms with
the pressured hook versus 1.35-1.36 ms with an equally pressured process that installed
no hook. Maxima were 7.01-7.48 ms versus 4.02-5.14 ms. The lookup's instrumented p99 was about 0.6 us;
lookup duration is deliberately not substituted for input-delivery latency.

The first instrument reported a fixed 40-byte increment per run. Warming the
allocation-counter API alone did not remove it. Resolving Stopwatch.Frequency and
the timestamp conversion factor before installing the hook did: that first recorded
sample had been charging the observer's lazy clock setup to the hook. Native checks
with the corrected observer report zero measured hook-thread allocation with and
without forced GC. The ordinary regression tests also exercise both claimed and
application-owned held keys and require exactly zero warmed hook-thread allocations.

## Sign-off

The accepted principal datasets comprise **107 runs, 1,620,400 measured application
input edges and 160,000 measured command notifications**, all with zero missing,
duplicate, reordered, wrong-edge or leaked-bound events. Warmup and the explicitly
rejected fixture runs are excluded from those totals. The corrected native hook
allocation runs cover 40,000 measured lookup callbacks and report **0 B** in every
hooked case, both with and without forced collections.

The software path remains responsive in the measured workloads, and the evidence
does not support a speculative scheduler-priority increase, per-keystroke logging,
or a new keyboard-processing architecture. The concrete changes address reproduced
input-stream defects while keeping the hot path allocation-free and using the same
256-byte ownership storage. The passive input-desktop listener adds no per-key
work. Physical USB/raw-input/game-to-photon timing remains outside this synthetic
measurement; no universal guarantee about every game is inferred from it.

All **3619 Release tests passed with coverage**. Hosted x64 and ARM64 build/test,
NativeAOT packaging and installer checks passed for the production fixes in
[40436046342](https://github.com/MoaidHathot/Shubbak/actions/runs/40436046342).
The final installed daemon's SHA-256 is
`BE2F34E51594D3C904A84C8AD4FC7B897B156BDC042A832BE5BC0924414F3F68`.
The normal session and companions were restored, and checks found no remaining
probe/window-helper/private-browser load processes.
