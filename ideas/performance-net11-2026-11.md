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
