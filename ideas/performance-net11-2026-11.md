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
