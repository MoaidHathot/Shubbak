# .NET 11 NativeAOT performance investigation

Started 2026-11-13 at `b73e6a0`, following `performance-2026-10.md` and
`keyboard-modifiers-2026-11.md`. Goals: lower CPU/allocation/resource usage across
the five programs, more consistent animation delivery, and preserved keyboard
correctness and responsiveness.

## Research and method

The source baseline uses SDK 10.0.404 / runtime 10.0.15; the installed binaries were
also saved before testing. The candidate SDK is 11.0.100. Measurements use native
published executables with their hashes retained. PDBs are retained separately.
Raw evidence is under `artifacts/net11-20261113/` in the original checkout.

Relevant upstream work:

- [Runtime Async](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/runtime#runtime-async):
  directly awaited Task/ValueTask helper chains can avoid intermediate tasks and
  reuse continuations. NativeAOT is supported. Application opt-in is evaluated
  separately from upgrading the framework libraries. Async iterators and observed
  task objects still have costs.
- [Windows process-output pipes](https://github.com/dotnet/runtime/pull/125643):
  stdout/stderr reads use overlapped I/O, directly relevant to Taj's ProcessSource.
- [The .NET 11 performance review](https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-11/):
  UTF-8 JSON escaping/scanning, NativeAOT dispatch, local Process API trimming,
  metadata allocation, compiler optimizations and threading changes.
- [NativeAOT optimization tradeoffs](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/optimizing)
  and [diagnostic support](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/diagnostics).

The delivered Windows NativeAOT binaries default to the Windows thread pool.
Portable-pool scheduling and dynamic-PGO results therefore cannot simply be
transferred from a JIT benchmark. The daemon already uses workstation/background
GC, a dedicated allocation-free keyboard hook, asynchronous window positioning,
bounded IPC turns and a high-resolution frame timer.

Other software, including another OpenCode session, remains active. Timed runs do
not overlap this investigation's builds/tests. Controllers record ambient process
CPU and reverse comparison order. Some baseline rows include unrelated compiler
activity; retain it and judge changes through interleaved comparisons. Allocations,
private committed memory, working set, threads, handles, native file size, frame
intervals and input delivery are separate measurements.

Windows kernel CPU sampling is unavailable without elevation in this session.
Native diagnostic variants explicitly enable EventSourceSupport; ordinary timing
variants keep the release setting. EventPipe sampled-thread stacks are wall-time
samples, not kernel CPU samples. CPU totals come from process counters; stack and
GC/allocation traces locate work. Native symbols require the matching PDB and
captured module load addresses; unresolved stacks cannot establish attribution.

## Baseline and benchmark qualification

All **3,785 Release test cases passed with coverage**, including native keyboard,
foreground and end-to-end tests. All five programs published as x64 NativeAOT.

The equal-work probe warms 1,000 operations and validates every measured reply or
geometry checksum. The additional escaped payload repeats nested JSON, Arabic text
and supplementary Unicode. It has the same 4,096-character payload in every variant;
its encoded wire size is larger than the plain ASCII case.

Four initial 100,000-exchange native runs:

| Work | Allocated B/exchange | CPU ms/exchange | Measured seconds |
|---|---:|---:|---:|
| Plain 4 KiB request/reply | 36,470-36,471 | 0.0875-0.0930 | 6.96-7.31 |
| Escaped/Unicode request/reply | 168,125-168,127 | 0.2450-0.2578 | 21.89-22.74 |

The source-resource probe runs **the real Taj ProcessSource**, using one frozen
native child program across variants. Each child reports its pid then keeps stdout
open without output. All children must become ready and exit during disposal. Four
baseline runs with 16 sources had **23 parent threads**, **7.19-7.43 MiB private
memory**, and **0-15.625 ms CPU over 15 seconds idle**. The CPU result is near the
counter's quantum, not proof of zero work. Source disposal cost **462-467 ms** and
substantial allocation from process-tree termination; it is recorded separately
from idle resources.

The query controller previously always compared the historical nine-query palette
path with the consolidated query, even when both binaries supported consolidation.
Runtime comparisons now use the same query on both sides; the old experiment is
explicitly selected with `-LegacyPrevious`. Worktree runs can select the installed
session directory and frozen window helper. The keyboard controller takes the same
desktop lease as the native tests and restores only a session it stopped.

Initial native keyboard qualification delivered all **120,000 measured edges**
across stopped/active idle, animation, four continuous IPC clients and CPU-pressure
cases, with no integrity failures. Active p99 was 1.475 ms idle, 4.829 ms with
animation, 2.087 ms under IPC and 1.784 ms under CPU pressure. These are one-round
baseline observations; final decisions use paired versions and reversed order.
The animation case verified two adopted foreign windows and actual geometry changes.
Layout and inverse resize retained zero warmed allocation and identical checksums.

**Step review:** the baseline validates the existing behavior and identifies useful
IPC and process-source workloads. It does not yet establish a product improvement.
The next comparisons isolate the .NET 11 upgrade and application Runtime Async.

## Framework upgrade qualification

The framework-only candidate targets .NET 11, with application Runtime Async still
off. The IPC package retains `net10.0` and adds `net11.0`; its transport tests run
on both. The cross-layer event-topic completeness check moved to the daemon tests
so the compatibility suite does not depend on the .NET 11 core. Packaging validates
both assets, their XML documentation and exact assembly hashes; release signing
covers both. The two example clients still target .NET 10.

The full suite passed **3,840 cases with coverage** (55 additional compatibility
cases), with zero warnings during build and zero failures/skips. All five programs
published for x64 and ARM64. ARM64 is cross-built locally; it is not executable on
this x64 machine. All 39 documentation snippets and 30 example checks passed, and
the dual-target NuGet package validated. New analyzer findings were resolved by
appending diagnostic text without intermediate strings and forwarding cancellation
to the new TextWriter overload where the hold is still running.

The first native-size result is a cost, not a saving:

| Program | .NET 10 bytes | .NET 11 bytes | Change |
|---|---:|---:|---:|
| Window manager | 7,126,528 | 7,225,856 | +1.39% |
| CLI | 6,007,296 | 6,134,784 | +2.12% |
| Taj | 5,972,992 | 6,102,016 | +2.16% |
| Dalil | 6,119,424 | 6,230,528 | +1.82% |
| Ayn | 5,298,688 | 5,422,592 | +2.34% |

Runtime comparisons determine whether that trade is justified. Later application
async and code optimizations are measured against this separate framework variant.

### Profile attribution

Native EventPipe stacks resolve against captured ASLR module bases and the matching
PDBs. The escaped workload shows buffer clearing/copying and UTF-8/JSON processing
alongside the pipe and worker waits. All NativeAOT samples are classified External
by this sampler, so they are not treated as kernel CPU percentages. A separate
GC-verbose native trace records collections and suspension durations; this runtime
does not provide allocation type identities in that trace.

A matched-source CoreCLR allocation trace supplies type attribution: strings and
byte arrays dominate, with smaller constructor-metadata, cancellation and async
objects. Its GC thresholds and thread pool differ from NativeAOT, so its timings
and collection count are not substituted for the native comparisons. The native
equal-work counters independently establish the bytes/exchange in the baseline.
Collection logs, traces, module maps, PDB/binary hashes and summaries are retained.

### Framework-only comparison and step review

Four reversed-order native pairs (`upgrade-*`):

- Plain IPC allocation is essentially unchanged (36,470-36,471 -> 36,430-36,432 B/op).
  CPU is **higher**, 0.0872-0.0927 -> 0.0911-0.1005 ms/op; this is a regression to
  resolve before selecting the final build.
- Escaped IPC CPU is 0.2500-0.2680 -> 0.2402-0.2622 ms/op, and allocation falls only
  about 430 B/op. These small framework savings leave the large buffers/strings.
- Layout still allocates zero and has overlapping elapsed ranges. Inverse resizing
  also keeps zero allocation, with 1M-operation elapsed 2.156-2.190 -> 2.104-2.113 s.
- Sixteen quiet sources use **12-21 rather than 23 parent threads**, and
  **6.60-7.01 rather than 7.32-7.38 MiB private memory**. Idle CPU remains near the
  counter's quantum. Disposal regresses **416-432 -> 694-706 ms**, with higher CPU.
  Upstream source inspection identifies changed process-tree relationship checks;
  each source still enumerates the whole machine when it is stopped.
- The desktop auto-locked before the keyboard comparison. The first, stopped-daemon
  foreground prerequisite failed and produced **no accepted keyboard row**. The
  failure is retained, not counted as either an input regression or a pass.

The framework migration is a separately reproducible foundation, **not yet the
accepted final performance configuration**. The next steps must resolve the IPC
and source-disposal regressions and complete unlocked desktop comparisons. The
full correctness suite passes; runtime and resource acceptance remain open.

## Subscribed write cancellation

A real pipe with a 4 KiB input buffer accepts a subscription, then stops reading.
Cancelling a 128 KiB write reproduces the defect on **both .NET 10 and .NET 11**:
the next request tries to flush the abandoned buffer instead of failing promptly.
The new regression failed with a two-second timeout on both targets.

Failed subscribed writes now mark the connection broken and close it. The check is
under the request semaphore, so a queued request cannot inspect another in-flight
request's temporary state. Cancelling only a queued request or an already-written
request's reply wait still preserves the connection. Those controls pass as well.
All **59 IPC cases pass on each target**. No additional async wrapper or work is
introduced on the successful write path.

**Step review:** this is a reproduced correctness fix needed before changing async
lowering and buffer lifetime. It is not claimed as a throughput optimization.

## Runtime Async measurement qualification

The first `runtime-async-*` comparison is **excluded as an async A/B experiment**:
both executables had SHA-256
`F8667118775675CB60BFBE39464ED405A8214339C82F847BB4AEEE66897ED787`.
Roslyn's incremental inputs did not notice the changed compiler Features property.
The nearly identical allocation counts therefore measured the same lowering.

The build now records compiler features in a write-only-when-different intermediate
input. Changing the switch invalidates compilation, while repeating the same build
does not. Subsequent comparisons must verify method implementation flags and state
machine counts as well as native hashes. Existing .NET 10 compatibility builds keep
compiler-generated async regardless of the switch.

The qualified builds have **20 vs 2** IPC state-machine types, and ConnectAsync's
implementation flags are **0x0 vs 0x2000**. The .NET 10 asset remains at 20 / 0x0.
Native hashes differ; the probe is 8,014,848 vs 7,971,328 bytes. Both variants use
the same source, including the cancellation correction and worker abstraction; the
IPC workload does not start workers. All nine selected async/companion suites pass
with the verified lowering.

Four reversed-order 100,000-exchange pairs (`qualified-async-*`):

| Work | Compiler async B/op | Runtime Async B/op | Compiler CPU ms/op | Runtime CPU ms/op |
|---|---:|---:|---:|---:|
| Plain IPC | 36,430-36,432 | 35,860-35,866 | 0.0827-0.0878 | 0.0797-0.0850 |
| Escaped IPC | 167,695-167,704 | 167,150-167,153 | 0.2238-0.2339 | 0.2247-0.2272 |

Plain IPC elapsed was 6.545-6.649 vs 6.434-6.511 seconds; its CPU was lower in
each pair. Escaped CPU/elapsed ranges overlap, with one pair slightly slower.
Layout/resize keep zero allocation and matching geometry. An isolated slower
layout control row is retained rather than attributed to async lowering.

**Step review:** enable Runtime Async on .NET 11. Its reproducible allocation saving
and modest plain-IPC improvement justify it, while the much larger string/buffer
cost remains the next target. No broad animation or resident-RAM gain is inferred
from this async experiment alone.

## Owned script workers

The source-disposal profile identifies Process.Kill(entireProcessTree), repeated
process enumeration, OpenProcess and start-time/parent checks. In .NET 11 those
relationship checks do more queries, explaining the framework-only regression.

The native host now supplies a worker factory to the portable Taj/Dalil logic.
Each worker starts suspended using .NET 11, is assigned to its own kill-on-close
Windows job, and is resumed only after assignment. Closing that job stops the
family without discovering unrelated processes. The handle is non-inheritable;
parent death closes it too. Ordinary application launching does not use this factory.

Six native cases verify explicit stop/disposal, grandchildren, independent worker
groups, suspended execution, failed-launch handle cleanup and abrupt owner death.
Taj's real source and Dalil's output/error semantics have integration coverage.
That work also reproduced an existing double-dispose exception in SourceBase;
guarding before virtual cleanup fixes it, with a portable regression test.

Four reversed-order native pairs with 16 sources, one frozen child binary and the
same runtime-async probe (`worker-jobs`):

| Observation | Process-tree scan | Job ownership |
|---|---:|---:|
| Dispose 16 sources | 528-553 ms | **1.29-1.75 ms** |
| Total parent CPU for the cycle | 484-594 ms | **31-47 ms** |
| Total parent allocation for the cycle | 31.97-32.52 MiB | **0.70-0.71 MiB** |
| Private memory after disposal | 24.72-25.21 MiB | **6.41-6.81 MiB** |
| Startup | 69.86-72.32 ms | 68.08-85.99 ms |

Every child became ready and exited. The lifecycle totals include startup,
15-second idle and cleanup, including observation overhead. Idle resources overlap:
jobs add one handle per worker and the Windows pool retains varying free workers;
this is not claimed as an idle-RAM improvement over the framework-only build.

**Step review:** retain job ownership. It resolves the demonstrated .NET 11 cleanup
regression, substantially reduces reload/exit work and retention in this workload,
and strengthens the existing worker-lifetime contract. Keyboard and full-stack
combined validation remain required.

## UTF-8 write buffers

The writer now reuses its Utf8JsonWriter, retains at most 16 KiB per connection,
and borrows temporary buffers up to 128 KiB. Larger requests are not retained in
that pool. The larger temporary cap is necessary because Utf8JsonWriter can ask
for about 74 KiB to encode 4 KiB of quote characters even though the wire frame is
smaller. The initial 64 KiB cap failed the warmed allocation check (7,378,400 B for
100 frames); 128 KiB passes with zero warmed allocation in that memory-stream case.

The rented memory is returned only after WriteAsync completes, including failure.
The JSON writer drops its borrowed span before a buffer can be returned. A held-
write regression rents and overwrites competing pool buffers while I/O is pending;
the captured frame still round-trips. Existing large/small, Unicode, ordering,
malformed-frame, cancellation and subscriber tests also pass: **61 cases per IPC
target**, plus the shared companion suite.

Four reversed-order native pairs against the preceding qualified async build:

| Work | Before B/op | After B/op | Before CPU ms/op | After CPU ms/op |
|---|---:|---:|---:|---:|
| Plain IPC | 35,864-35,868 | 35,624-35,626 | 0.0794-0.0842 | 0.0844-0.0862 |
| Escaped IPC | 167,148-167,156 | **87,305-87,308** | 0.2313-0.2356 | **0.2258-0.2325** |

Escaped elapsed falls 21.015-21.200 -> 20.448-20.881 seconds for 100,000 exchanges.
Its allocation falls about 48%; decoded strings remain a substantial cost. Plain
allocation improves only slightly and its CPU is higher in these pairs. The final
combined baseline comparison must account for that trade rather than reporting all
IPC as faster. Native stack/GC evidence is retained in `native-writer-escaped-profile`.

**Step review:** retain the large allocation reduction provisionally and verify the
combined plain/escaped workload and resident-memory effect before delivery.

A follow-up uses one linked cancellation source with its own deadline, rather than
a timeout source linked through a second source, and separates the buffer's inline
capacity check from growth. All 61 IPC cases per target still pass. Plain exchanges
drop another **~215 B/op** (35,622-35,627 -> 35,406-35,412), and escaped exchanges
drop to ~87,095 B/op. Three plain pairs have equal/lower CPU, while one candidate
row is slower (0.1087 vs 0.0908 ms/op); it is retained for the final paired rerun.
The change is justified as an allocation reduction, not a universal CPU speedup.

## NativeAOT speed preference experiment

`OptimizationPreference=Speed` was compared with the balanced default, with
different native hashes and identical workloads. It grows the probe from
7,972,352 to 8,492,032 bytes (**+6.5%**). Four pairs of 1M-operation layouts improve
0.948-0.961 -> 0.846-0.863 s. Inverse resize usually improves, but one speed row is
slower; plain IPC CPU ranges overlap, and escaped IPC has only two pairs with a
small elapsed improvement.

Keep the balanced default. The clear gain is in a sub-microsecond pure-layout
operation already allocating zero; it does not demonstrate enough whole-process
benefit to justify broadly growing resident code. No JIT-only/dynamic-PGO result
is attributed to the NativeAOT binaries.

## Keyboard upgrade qualification

After unlocking, the framework-only comparison completed **200,000 measured
application edges** in reversed order, with no missing, duplicate, reordered or
wrong-edge input. Per-run p99 ranges (milliseconds): idle 1.348-1.490 -> 1.349-1.372,
animation 3.745-3.778 -> 3.694-3.710, IPC 1.572-1.595 -> 1.578-1.752, CPU pressure
1.643-1.744 -> 1.641-1.651. The IPC tail is higher in one candidate row; this remains
in the evidence. These preliminary runs do not replace the final combined matrix.

## Combined correctness and packaging

The assembled candidate passes **3,869 Release cases with coverage**, with zero
failures/skips, including the native foreground/keyboard tests, the process-family
tests, both IPC targets and the real-daemon scenarios. Both architectures publish
as NativeAOT, including the uiAccess variants. The release script validates native
headers, versions, manifests, MSIs, archives and the two IPC package assets. Local
MSI installation/ICE validation requiring elevation is left to the hosted checks.

The frame experiment and final native performance matrices are evaluated separately
from this correctness result.

## Frame deadline phase correction

The high-resolution clock now retains its deadline phase across small wake errors.
It corrects at most the smaller of one millisecond or 10% of a period; longer stalls
rebase, avoiding adjacent catch-up frames. Input/IPC wakes cannot emit a frame early.
The millisecond-timer fallback retains its prior policy. Actual elapsed time still
advances the animation, and reset/rate-change/overdue cases have deterministic tests.

Two foreign native window processes record their own applied rectangle changes
into preallocated storage and write it after the run. The same helper and input
probe are frozen across versions. Geometry is clipped to measured input timestamps;
no long intervals are discarded. Initial comparisons have two reversed-order rounds
per workload, including combined animation+IPC and animation+CPU pressure.

| Target/workload | Mean interval before | Mean interval after | RMS period error before | After |
|---|---:|---:|---:|---:|
| 60 Hz, animation | 16.950-16.953 ms | **16.668 ms** | 0.501-0.547 ms | **0.389-0.521 ms** |
| 60 Hz, animation+IPC | 15.701-15.703 ms | **16.667 ms** | 1.060-1.079 ms | **0.423-0.477 ms** |
| 60 Hz, animation+CPU | 16.968-16.971 ms | **16.666-16.669 ms** | 0.593-0.646 ms | **0.455-0.579 ms** |
| 144 Hz, animation | about 7.17-7.18 ms | **about 6.95 ms** | about 0.54-0.55 ms | about 0.52-0.56 ms |
| 144 Hz, animation+IPC | about 5.98 ms | **about 6.94 ms** | about 1.07-1.09 ms | **about 0.46-0.51 ms** |

The IPC rows correct **overspeed**, not just late frames: the previous one-millisecond
early allowance made sustained IPC request more repaints than the configured rate.
Their absolute p99 intervals increase with the corrected period, so they are not
presented as lower latency. Plain 60 Hz geometry p99 improves from 18.00-18.40 to
17.76-17.97 ms. There are no intervals over twice the period in the initial geometry
matrix; the candidate has no under-half-period bursts.

One initial 144 Hz input case had higher p99 (5.897 vs 3.818 ms) and a 20.575 ms
maximum. Its daemon had zero collections, hook faults and dropped keys; the data
does not identify the cause. A four-pair follow-up retains another slower first
candidate p99 (4.320 vs 3.906), followed by three lower candidate p99 values
(3.785/3.737/3.687 vs 3.907/3.846/3.750). The measurements do not establish that
every tail is faster. All **320,000 measured input edges** in these frame comparisons
pass integrity checks. Raw slower cases and ambient CPU are retained.

**Step review:** retain the deadline correction for the demonstrated cadence
accuracy and reduced overspeed. Keep the final combined keyboard/load matrix as
the acceptance check for the delivered build; geometry cadence is not a claim
about physical display presentation latency.

## Hosted verification

[Run 43486256144](https://github.com/MoaidHathot/Shubbak/actions/runs/43486256144)
passed for candidate `81b0086`: x64 build/tests, native packaging and MSI
install/uninstall, followed by native ARM64 tests, execution of the cross-built
programs, and ARM64 MSI install/uninstall. The ARM64 job retains its documented
foreground-test exclusion; the unlocked local x64 suite ran those tests.

## Normal resident session

The final candidate and the original .NET 10 source baseline were deployed in
baseline/current/current/baseline order with the normal configuration. Each session
included 120 seconds idle, input delivery, 100 palette activations, 40 reloads,
suspension and resume. All operations and input-integrity checks passed; the window
set was stable within each idle sample. Results are in `final-normal-stack`.

| Observation | Original | Candidate |
|---|---:|---:|
| Four residents' idle private memory | 43.61-49.23 MiB | **42.71-42.90 MiB** |
| Four residents' idle working set | 99.54-104.89 MiB | **98.73-98.78 MiB** |
| Four residents' CPU per 120 s idle | 93.75-437.50 ms | 203.12-250.00 ms |
| Normal-session input p99 | 1.88-2.14 ms | **about 1.71 ms** |
| Palette foreground median | about 1.80-1.82 ms | about 1.77-1.78 ms |
| Palette foreground p99 | about 3.34-3.51 ms | about 3.53-3.59 ms |
| Private memory after 40 reloads | 81.59-88.09 MiB | 85.02-85.27 MiB |

Idle CPU and post-reload memory overlap; neither supports a universal reduction.
The small palette sample is foreground readiness, not paint completion, and its
slightly higher tail is retained. The candidate's five native files total about
31 MB and its x64 archive is 13.67 MB; distribution documentation now uses the
measured size rather than the older 25 MB estimate. `final-binaries.json` verifies
that the five executables installed in `dist` match the measured candidate.

## Final native input matrix

The principal final matrix contains **600,000 measured application edges** (including
80,000 GC-pressure edges) and **144,000 command notifications** across 82 cases.
All accepted runs preserve the tested counts/order/edge ownership, with no leaked
bound input. Notifications with identical signal payloads establish counts, not
independent per-notification identity. The receiver is frozen across daemon versions.

Application-delivery p99 ranges, milliseconds:

| Workload | Original | Candidate |
|---|---:|---:|
| Idle | 1.339-1.363 | 1.265-1.312 |
| 28 CPU workers | 1.614-1.730 | 1.652-1.737 |
| Animated windows | 3.605-3.688 | 3.375-3.459 |
| Four saturated IPC clients, initial | 1.612-1.956 | 1.891-2.155 |
| Hardware graphics + CPU load | 1.450-1.486 | 1.459-1.461 |

Because the initial IPC tail is higher, an additional four reversed-order pairs
were run both with paced requests and saturation (160,000 more edges). Paced p99
is **1.296-1.387 vs 1.296-1.402 ms**; saturated p99 is **1.551-1.754 vs
1.576-1.645 ms**. The initial tail increase does not persist in that comparison.
Saturation can process different request counts; the equal-work CPU/allocation
comparison is separate. A 24.18 ms idle candidate maximum is retained rather than
hidden by its lower p99.

Unmodified bound-command p99 is 0.745-0.963 vs 0.774-0.791 ms idle,
1.111-1.170 vs 0.706-0.876 ms with CPU pressure, 2.017-2.534 vs 1.978-2.407 ms
while animating, and 0.354-0.365 vs 0.347-0.356 ms with saturated IPC. Modified
Alt, Ctrl+Shift, Ctrl+Right-Alt and Win delivery was checked too. Larger CPU-loaded
tails on both versions prompted four additional pairs per modifier (96,000 more
notifications). Those runs preserve correctness but have mixed tails, including
slower candidate Alt rows; they do not justify claiming every shortcut percentile
improves. These are command/IPC notification times, not callback execution times.

The actual native hook host records **zero warmed hook-thread allocation** over
20,000 measured lookups, zero faults and zero drops. Its pressured hooked runs
include **159 forced blocking Gen2 collections**. Lookup p99 is about 1 microsecond;
that instrumented figure is not substituted for end-to-end input delivery. The
normal-session tests add 112,000 input edges, including suspension/resume, and 400
successful palette activations.

**Step review:** keyboard semantics remain protected by receiver-based regressions
and native integrity checks. Typical delivery stays in the measured baseline range
or improves; variable tail observations are reported explicitly. This establishes
neither a hard latency bound nor physical USB/raw-input/game-to-photon timing.

## Final original/candidate rerun

Four reversed-order native pairs against the frozen .NET 10 source baseline, with
all selected changes included, reproduce the IPC gains:

| Work | Original B/op | Final B/op | Original CPU ms/op | Final CPU ms/op |
|---|---:|---:|---:|---:|
| Plain 4 KiB IPC | about 36,471 | **35,403-35,412** | 0.0852-0.0936 | **0.0788-0.0855** |
| Escaped/Unicode IPC | 168,128-168,133 | **87,085-87,097** | 0.2338-0.2537 | **0.2103-0.2244** |

Mean reductions are **6.4% CPU / 2.9% allocation / 3.9% elapsed** for plain IPC and
**10.2% CPU / 48.2% allocation / 6.8% elapsed** for escaped IPC. CPU is equal or
lower in every final pair. These are this machine's equal-work results, not a
universal speedup promise. The earlier component-level plain-CPU cost is retained
above; it does not become an overall regression in the final baseline comparison.

Layout and inverse resize keep **0 B/op** and identical geometry checksums. Layout
elapsed is 0.974-0.979 -> 0.948-0.973 s per million operations. Inverse resize has
three faster pairs and one slower candidate row (2.239 vs 2.192 s), retained in
`final-layout` rather than described as uniformly faster.

The final 16-source lifecycle comparison against .NET 10 has **323.6-327.9 ms ->
0.72-8.71 ms** disposal, with one slower candidate cleanup retained. Parent cycle
allocation is **31.31-31.42 -> 0.69-0.72 MiB**, CPU **343.8-437.5 -> 31.3-62.5 ms**,
and post-disposal private memory **25.82-26.47 -> 7.45-7.94 MiB**. All children
started and stopped. Idle source memory overlaps; free worker retention varies.

The final NativeAOT diagnostic builds were profiled after the ordinary timings.
`final-escaped-profile` retains native stack/GC evidence for the combined IPC code.
`final-worker-profile` no longer shows the repeated process-tree termination path;
its remaining process-information samples are in the benchmark's own resource
observer. Matching PDB/ASLR maps and the TraceEvent analyzer source are retained
under the evidence directory's `profiling-tools`.

**Final goal review:** the accepted changes reduce measured IPC and worker lifecycle
costs, retain the allocation-free keyboard/frame paths, preserve input semantics,
and make applied animation cadence substantially closer to its configured period.
Idle CPU, some tail percentiles and post-reload whole-stack memory remain variable;
the report makes no blanket improvement claim for them. The speed-oriented AOT
build was rejected, and the invalid cached async comparison was excluded. The
normal session uses the verified final five binaries, with original copies and
state/config backups retained separately.

### Stable-desktop resource follow-up

Final review found two different window sets across the normal-session samples:
the first baseline had five windows and the remaining sessions six. Although each
individual idle sample was stable, that table is not a perfectly controlled RAM
comparison. A separate baseline/current/current/baseline run therefore enforces
identical window handles and configuration across every case (`final-stable-idle`).

Across those 30-second samples, combined private memory is **43.70-43.93 ->
42.86-43.61 MiB** and working set **99.25-99.41 -> 98.47-98.92 MiB**. The idle
memory improvement is modest. CPU is **0-46.88 -> 62.50-93.75 ms**, in 15.625 ms
counter increments; this short sample supports no idle-CPU reduction claim.
The longer 120-second CPU ranges above overlap. All four final programs were
restored, and no benchmark-owned probe/window process remained.

The final review retains the improvements with their measured scope: appreciably
less IPC/worker allocation, less active-work CPU, faster worker cleanup and more
accurate frame cadence. It does not turn small idle measurements or mixed latency
tails into an all-workloads performance guarantee. Source text and retained evidence
are LF-normalized, and the workspace contains no `nul` file.
