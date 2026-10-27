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
