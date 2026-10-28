# Performance pass, 2026-10-28

Starting revision: `1e7a905`. Compatibility scope is the user's configuration, the
bundled programs and examples. Preserve keyboard input and visible behavior while
reducing work; commit and compare each step. See `keyboard-latency.md` for the preceding
correctness work and the limits of synthetic application input measurements.

## Baseline and method

The installed five native executables and session were saved under the approved temp
directory `shubbak-performance-20261028/baseline`. Native benchmark probes are retained
under `artifacts/performance/`; output folders are never overwritten. Other software
is running on this PC. Timings are interleaved, background CPU is recorded, and builds
and tests are kept outside measurement windows. Allocation counts and deterministic
geometry checks complement noisy wall/CPU time.

The fresh equal-work probe warms 1,000 operations then measures 20,000 operations.
Initial four rounds (32 nested mixed-state windows, all built-in layouts):

| Work | Allocated bytes/operation | CPU ms/operation | Wall seconds |
|---|---:|---:|---:|
| 4 KiB pipe request/reply | 72,675–72,685 | 0.247–0.250 | 3.56–3.70 |
| Complete layout | 5,992 | 0.0031–0.0047 | 0.095–0.097 |
| Layout + inverse resize | 14,342 | 0.0125–0.0133 | 0.252–0.255 |

Layout CPU samples are short relative to process-counter resolution; later comparisons
use longer loops. Both layout cases produced checksum `-7434308080712238633` in every
round. These are observer-inclusive operation costs, not keyboard callback timing.

The IPC result justifies addressing its character-at-a-time reader before client
refresh consolidation: the reader's async machinery is paid once per character.
The native keyboard baseline additionally covers idle, animated windows and paced
IPC queries in four rounds, retaining raw edges and process resource deltas.

## Buffered IPC and publication

`BoundedLineReader` reads UTF-8 chunks, handles split characters/frames and enforces
the advertised byte limit (the old reader counted characters). Large fragments are
returned after the line. Pending reads are cancelled/joined on disconnect. The event
publisher uses a client snapshot rebuilt on connection changes; its writer swaps two
bounded queues and signals an empty-to-nonempty transition rather than leaving one
empty wake per event after a burst. The four acceptors' capacity check/add is atomic.

Four reversed-order native pairs, 20,000 identical 4 KiB exchanges per run:

- Allocation: **72,678–72,684 -> 53,936–53,941 B/op** (about 26% less).
- CPU: **0.249–0.295 -> 0.156–0.171 ms/op**.
- Elapsed: **3.67–4.13 -> 2.00–2.04 s**.
- All replies validated; all 53 IPC tests passed, including fragmented UTF-8,
  multibyte limits, burst/resync, concurrency, malformed requests and disposal.

The fresh keyboard baseline itself varied: animation p99 3.14–5.44 ms, and one paced
IPC run had a 76 ms maximum. These pre-optimization tails are retained. Background
work is real, so a later isolated maximum cannot by itself establish a regression.

## Companion state reads

Taj's display models share one subscription and command connection (two pipes total,
instead of four on this two-monitor PC). A ready event burst is applied immediately,
then refreshed once, with a 64-event batch bound. No debounce timer is added. A real
pipe regression queues 50 changes while a read is held, verifies 2–4 total reads
rather than 50 per display, independent monitor output, and continued updates after
the original display's view is removed. Icon requests/cache are shared and generation
checked so an old completion cannot repopulate a removed/reused handle's entry.

Dalil reads `palette`/`palette-all` once, replacing nine/ten sequential queries. Desktop
discovery stays off the loop; the loop captures coherent values and serialization
continues on the pipe task. Static command/layout descriptions are cached. `bar-state`
omits the unused full window list. Workspace counts use one allocation-free traversal.

Four paired native query runs versus the **preceding IPC step**, 200 palette reads:

- Median data-read latency: **0.931–0.974 -> 0.614–0.669 ms**.
- Daemon CPU: **156–203 -> 125–141 ms** per 200 reads.
- Client allocation: **75,715–75,721 -> 57,690–57,934 B/read**.
- p99: before **1.45–1.62 ms**; after **1.06–2.17 ms**, retaining the first after
  run's slower tail. These are data-read times, not complete palette paint latency.

Empty-desktop bar queries intentionally remove the window-list saving: 10,000 reads
had roughly the same ~6.8 KiB client allocation each. The benefit there is fewer reads
per action, demonstrated by the multi-display burst test; it is not claimed as a
per-query allocation win with no windows to omit. The full coverage suite passed
**3,632 cases** including real-daemon compact/palette state consistency and updates.

## Direct UTF-8 framing

Requests/replies serialize straight into a per-connection UTF-8 buffer, eliminating
the intermediate UTF-16 envelope strings. Events retain batching and wire order;
buffers above 16 KiB are discarded after writing so one large report does not remain
retained on an idle connection. The existing JSON wire format and example scripts
continue to work. A structured-payload protocol rewrite is unnecessary for this gain.

Four paired runs against the **state-read step**, 20,000 equal 4 KiB exchanges:

- Allocation: **53,936–53,941 -> 36,467–36,473 B/op**, another 32% reduction;
  approximately **50% below the original baseline**.
- CPU: **0.156–0.171 -> 0.084–0.094 ms/op**.
- Elapsed: **1.99–2.01 -> 1.395–1.404 s**.
- 56 IPC and 20 Taj integration tests pass, including reused-buffer Unicode escaping,
  large-then-small messages, event/reply order and releasing oversized buffer capacity.

## Layout and inverse resize

Layouts consume read-only weight spans. Mixed tiled/floating arrangements no longer
allocate a temporary container or change live node parentage/ratios. Inverse sizing
uses scratch weights instead of repeatedly mutating/restoring the tree. Common
scratch fits a bounded stack budget; deeper/wider layouts rent value-only buffers.
Tree membership checks and non-tiled traversal no longer create iterator objects.
The native placement commit similarly uses bounded scratch instead of new lists.

Four paired native runs, **200,000 operations each**, versus the UTF-8 step:

| Work | Before B/op | After B/op | Before elapsed | After elapsed |
|---|---:|---:|---:|---:|
| 32-window complete layout | 5,992 | **0** | 0.950–0.979 s | 0.191–0.193 s |
| Layout + inverse resize | 14,342 | **0** | 2.597–2.619 s | 0.427–0.430 s |

Every before/after geometry checksum matched (`78454299066233573`). The after runs
had exactly zero total measured allocation and zero collections. This is roughly
5x/6x faster for these pure operations, **not** a claim that whole-window animations
are 5x faster: native window positioning/composition are separate costs.

The coverage-enabled suite passed **3,641 cases**. New guards verify zero warmed
layout/resize/unchanged-native-commit allocation, mixed and nested output, and that
the live tree remains unchanged even *during* a layout/inverse-size calculation.

## Scaling correctness follow-up

Wider checks reproduced an existing normalization defect: a fixed 1% minimum cannot
sum to one with 128 or 256 children (observed totals 1.28 and 2.56), and inverse
resizing stopped yielding an answer. The floor is now feasible for the sibling count;
normalization reserves floor shares before distributing the rest. Tests also cover
lopsided inputs, where dividing and then individually clamping could add extra mass.

The first correct implementation cost ~5–10% in the new microbench. An ordinary-tree
fast path removed that cost: three fresh paired 200k-operation runs matched geometry,
kept 0 B/op and measured layout 0.193–0.195 s vs 0.193–0.199 s, inverse sizing
0.429–0.431 s vs 0.427–0.461 s. The separate 1,024-window probe measured approximately
30 us/layout and 31 us/layout-plus-resize, with zero warmed allocation. All 784 core
cases pass. This fix is recorded separately from the original allocation refactor.

## Idle logging

The logger's writer now waits on its existing signal without the 250 ms polling
timeout. Enqueue publishes before signaling; file rotation/shutdown still drain
synchronously. Ring-only logging skips the unused sink queue/formatter when neither
console nor file output is enabled. All 35 logging tests pass, including delivery of
successive bursts without manually flushing and exactly zero allocation for warmed
constant ring-only messages. This removes timeout wakeups in all resident programs;
its CPU/RAM contribution will be reported from the final whole-stack measurement.

## Clock/source idle work

Taj's minute clocks now schedule the next displayed boundary instead of checking
twice a second: one ordinary timer firing per minute for each of the user's two
minute-format clocks. Second precision, fractions, quoted/escaped literals and
configured slower sampling are respected. Windows time/settings/power messages
request a coalesced refresh; stood-down clocks stay stopped until resume. Histories
retain the configured cadence, including when a sampling observer attaches later.

Unchanged samples use TryFormat and reuse the published string. The deterministic
clock/timer tests verify boundary timing, jump/resume, sampling semantics and exactly
zero warmed allocation across 1,000 unchanged samples. Source fan-out now uses a
model snapshot rebuilt on attach/detach instead of an array per value change. All
637 Taj.Core and 20 Taj integration cases pass. Ayn's source loop was also reviewed:
it already waits on registry/audio/power/theme events with deadlines only for pending
work; it benefits from the shared IPC/logger changes without adding polling.

## Fairness under replenished IPC load

The daemon inbox previously drained until empty, so a producer replenishing it could
extend the same turn indefinitely. It now takes only the initial batch (at most 32),
yielding after two milliseconds between items, and re-signals the loop when work
remains. FIFO and completion behavior are preserved; keyboard/window drains and due
frames get another turn. A single slow operation is still indivisible. The production
inbox is exercised by replenishing-producer, slow-item and fault/FIFO tests; all 276
WM tests pass. Loaded native input/throughput checks are part of the final matrix.

Focus-triggered geometry validation also repairs windows that move themselves while
Shubbak is not subscribing to location-change floods (see WindowCommitterDriftTests).
Skipping the whole pass would remove that behavior. The allocation-free geometry/
commit paths retain the repair and overlapping-window stacking semantics.

## Companion responsiveness follow-up

A controlled real-pipe test reproduced Taj clicks waiting behind a held icon reply
on the command/state connection. Icon fetching now uses a short-lived, round-cancelled
connection while still sharing one in-flight request/cache across bars. The test
holds the icon response and verifies the click completes independently. Real icon
discovery has a bounded foreign-window wait, but that wait should not reach clicks.

The keyboard-language source also constructed CultureInfo and uppercase label strings
on each 250 ms poll. A bounded 32-language cache preserves known/unknown labels and
measures zero warmed allocation for repeated label lookups. The necessary foreground
layout reads still detect per-application language changes. All 26 Taj tests pass.

## Diagnostic allocation audit

Percentile reporting allocated a sorted 4,096-double copy for every percentile,
including repeated reads of the same median in one report. A reproduced allocation
test measured **6,558,400 B** for 100 median/p99 pairs. Sorted scratch is now rented
and returned, with the original rolling samples untouched; the same warmed test
measures **0 B**. The report computes its repeated tick median once. The existing
bounded diagnostic sample rings are retained for evidence quality. All 787 core
tests pass, including rolling percentile correctness and allocation guards.

## Final source review

Two additional deterministic regressions were confirmed red before correction:
an old source's already-captured callback could overwrite its replacement after a
reload; and the clock string-reuse optimization confused an initial empty output
with an uninitialized null. Source identity/publication are now serialized with
replacement, and string reuse requires a previously initialized value. All 639
Taj.Core and 26 Taj integration cases pass after these fixes.

The first final full-suite run encountered Windows' lock screen and is excluded for
desktop validation. After the user unlocked the PC, Native (290), Dalil (102) and
end-to-end (7) all passed with coverage; the other projects had already passed. CI
for the committed optimization set passed x64 and ARM64 in run 40610633992.

## Animation qualification and startup race

Reviewing a suspiciously *good* final animation result found an invalid workload:
`final-keyboard-baseline/animation-current-r1-pass` managed no test windows and recorded
no animation frames. Its input data is retained, but excluded from animation evidence.
The fixture now waits for helper HWNDs, verifies adoption, checks frame samples and
observes real foreign-window geometry changes. Helper exit/output is retained too.

That investigation exposed a real startup gap: desktop adoption/context seeding ran
before window hooks were installed. A concurrent-startup regression created twelve
foreign windows and failed waiting for all twelve to be adopted. Subscribing before
the snapshots fixes it; all eight end-to-end scenarios then passed with coverage.
The corrected animation comparisons are run with ready windows for both versions.

## Native input verification

Four paired rounds per workload against the saved original native daemon; lower is
better. Ranges are per-run p99 in milliseconds, not an average of pooled samples.

| Workload | Original p99 | Optimized p99 |
|---|---:|---:|
| Idle | 1.41–1.59 | 1.39–1.50 |
| Paced IPC queries | 1.51–1.98 | 1.47–1.61 |
| Animation, corrected/qualified fixture | 4.11–4.51 | 4.04–4.28 |
| CPU pressure, follow-up | 3.53–3.98 | 3.71–4.08 |
| CPU + hardware graphics load | 2.52–3.20 | 2.27–3.05 |

Every corrected animation case observed both target windows and hundreds of real
geometry changes. The first CPU series had somewhat higher optimized tails; the
follow-up's paired differences changed sign, with the stopped baseline also shifting
under ambient load. These results do **not** justify claiming that every latency
metric improved. The typical software delivery remains sub-millisecond while idle,
and roughly 1–1.4 ms under the CPU loads. Occasional larger maxima remain in the raw
data. The keyboard callback and binding lookup implementation were not changed.

The clearest responsiveness gain is under **24 continuously querying clients**:
bound key-to-signal p99 was **2.046–9.888 ms before vs 0.997–1.264 ms after** over four
paired runs. Medians were about 0.2 ms in both. All notifications arrived and no
bound input leaked through. Saturation is intentionally measured separately from
fixed-work CPU/allocation comparisons; it can process different request counts.

The native production-hook GC checks again reported zero warmed hook-thread bytes,
faults and dropped events, including 77 forced full collections in each pressured
hook run. No input-integrity failure occurred in the completed native matrices.

The preceding-step comparison (`a37817e` vs `4b96a49`) also retained correct delivery;
its animation p99 ranges were 4.61–5.42 vs 3.38–4.75 ms, paced IPC 1.75–2.26 vs
1.93–1.95 ms. This is a different time window and is not mixed into the baseline
table. Latest implementation CI passed both architectures in run 40614392106.

## Normal four-program session

The normal configuration was measured in baseline/current/current/baseline order,
with 120-second idle samples, 20,000 application edges, 100 palette activations,
40 explicit reloads, suspension and resume per session. All input checks passed.
Ambient processes remained active; the first baseline had six managed windows and
the other sessions seven, so these are whole-session observations, not an isolated
CPU attribution experiment.

| Observation | Original | Optimized |
|---|---:|---:|
| Application-delivery p99 | 2.25–2.30 ms | 2.08–2.17 ms |
| Palette foreground median | 3.08–3.13 ms | 3.09–3.20 ms |
| Palette foreground p99 (100 opens/run) | 5.48–5.59 ms | 6.54–7.55 ms |
| Taj CPU per 120 s idle | 156–188 ms | 63–78 ms |
| All four residents' CPU per 120 s idle | 250–375 ms | 141–234 ms |
| Combined idle private memory | about 41–42 MiB | about 41 MiB |
| Daemon private memory after 40 reloads | 37.6–38.1 MiB | 28.6–28.7 MiB |

The palette's typical foreground readiness is similar, with higher p99 in the small
initial samples; faster data acquisition is not presented as universally faster
activation. Resume-with-no-discarded-warmup also retained larger isolated spikes in
both versions. Exact distributions are retained under normal-stack-comparison.
Handles/threads did not grow per reload. The daemon reported no hook faults/dropped
keys. Frame allocation p99 remained zero; the whole-session maxima included 1,328 B
in both versions, so cold cache growth is not described as an allocation-free frame.

The reload audit also identified extra work in Ayn: it reparsed the entire unchanged
file on every accepted daemon reload. A last-text parsed-result cache preserves the
reconfiguration/device-observation path. File comparison uses decoded chunks to avoid
another large string on a cache hit, detects same-length/same-mtime edits, and retains
at most one source of 256 Ki characters. Encoding and I/O-error behavior are tested.
Native retention follow-up compares this separately against the preceding Ayn build.

Two reversed-order native pairs of **60 unchanged-config reloads** confirmed the Ayn
change: private memory after the sequence was **9.15–9.16 MiB vs 28.55–28.85 MiB**.
Handles stayed 245–250 and threads remained bounded. This is measured private memory,
not an assertion that the earlier growth was an unreachable-object leak. CPU totals
were small/noisy, so no standalone Ayn CPU speedup is claimed. A parse-only intermediate
candidate reached ~15.3 MiB; decoded chunk comparison removed the remaining large
unchanged-read strings. All 19 Ayn integration and 134 Ayn.Core cases pass, including
same-stamp edits, UTF-16 BOM preservation, missing files and oversized-cache limits.

## Fractional frame deadlines

The frame review found a remaining quantization cost: the production message wait
rounded a fractional 60 Hz deadline up to whole milliseconds. A reusable high-
resolution waitable timer now participates in the same message/wake wait while
pacing; it is cancelled on idle/exit. There is no busy wait or additional worker.
Unsupported Windows falls back to the existing timeout behavior.

Four paired native animation/input runs against the preceding validated daemon:

- Median frame interval **17.42–17.45 -> 16.97–16.99 ms** (about 57.3 ->58.9 Hz).
- Frame-interval p99 **18.74–19.15 -> 17.49–17.90 ms**.
- Daemon CPU **22.8–28.0 ->18.3–25.4 ms per measured second**, overlapping ranges.
- Zero frame allocations, collections, hook faults, dropped keys or input-integrity
  failures in the candidate runs. Application-delivery tails remain mixed; this is
  a frame-pacing improvement, not a claim that all keyboard percentiles improve.
- All 12 message-loop cases passed, including immediate wake/stop during pacing and
  zero warmed wait allocation. The first attempt had no foreground access and was
  excluded; the successful dataset is timer-comparison-retry.

The complete Release suite with coverage subsequently passed **3,676 cases**, with
zero failures or skips, including the final timer and configuration-cache changes.
