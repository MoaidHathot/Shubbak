# Production input-path measurement

This complements S1: S1 measures a prototype *after* entering its callback. This
probe measures tagged synthetic input through the real native daemon to a real
foreground window, including the wait before a hook can run. It records only its
own F24/F23 events, not a person's keyboard contents.

Build as the daemon ships:

```powershell
dotnet publish spikes/Shubbak.InputProbe -c Release -r win-x64 -p:PublishAot=true -o artifacts/input-probe
```

Run the controller on an unattended, unlocked desktop, separately from builds and
tests. It stops/restores the normal Shubbak session, creates isolated daemon state
and a config with 98 bindings, and ignores every window except its own noise windows.

```powershell
./tools/measure-keyboard.ps1 -Probe artifacts/input-probe/shubbak-input-probe.exe -Output artifacts/input-results -Previous C:/path/to/previous/shubbak-wm.exe
./tools/measure-keyboard.ps1 -Probe artifacts/input-probe/shubbak-input-probe.exe -Output artifacts/bound-results -Bound -Workloads idle,cpu,animation,ipc
```

Modes in the executable:

- `receive`: F24 press/release pairs. Each event gets a distinct `dwExtraInfo` tag;
  the receiver checks its index and edge and timestamps receipt with QPC. Arrays
  are preallocated, foreground loss aborts the run, and all input batches contain
  complete pairs. High-resolution waitable timers pace injection without raising
  global timer resolution. `--burst 8` sends eight pairs per interval. Reported
  throughput is measured, not inferred from the requested interval.
- `receive --bound --instance NAME`: F23 bound to `signal "input-probe"` in the
  isolated daemon. Measures key-down to subscriber notification; this includes IPC
  delivery and is not the callback's execution time. Bound events leaking to the
  application fail the run. Counts and latency samples here are commands, not edges.
- `hook-host [--gc] [--no-hook]`: the actual `KeyboardSource` and `BindingTable`, in
  a separate process from the injector/receiver. It measures lookup duration and
  hook-thread allocation across warmed callbacks. Optional bounded-heap allocation
  and forced blocking collections expose delays *before* callback entry in the
  external receiver's measurements. The lookup timing itself is instrumented and
  must not be presented as uninstrumented full-callback latency.
- `cpu-load --threads N`: normal-priority CPU workers with a bounded lifetime.
- `churn --instance NAME [--animate]`: sustained IPC queries or geometry changes
  to the isolated daemon's test-owned windows.
- `gpu-load`: local WebGL shader workload in a separate, disposable Edge profile.
  Reports the renderer and browser frame cadence; this is a controlled graphics
  workload, not a benchmark of any particular game.

Child load modes print `ready`, and stop on stdin EOF or their time limit. The
controller drains their pipes and cleans them up even when a measurement fails.
The graphics worker starts rendering immediately but records cadence only after a
`measure` line on stdin, so starting the CPU workers is outside the recorded interval.

## Reading the results

For equal-work CPU/allocation comparisons use `perf --kind ipc|layout|resize
--operations 20000`. The IPC case exchanges 4 KiB requests and responses through the
real client/server; layout cases arrange 32 mixed tiled/floating windows across nested
containers and every registered layout. `resize` also exercises inverse sizing.
They warm 1,000 operations, count process-wide allocations/collections and CPU, and
retain a deterministic geometry checksum. A checksum mismatch invalidates a layout
comparison. Small CPU times need longer runs to exceed process-counter granularity.
`tools/measure-performance.ps1` interleaves native probes and records ambient process
CPU deltas. The benchmark includes its own observer overhead equally in both builds.

`perf --kind ipc --escaped` uses a fixed mixture of nested JSON, Arabic text and
supplementary Unicode, in addition to the plain payload baseline. `--payload-size`
selects the character count. Replies are validated on every measured exchange.

`process-sources --child PATH --sources 16 --seconds 15` runs Taj's real
`ProcessSource` against quiet child processes that report their pids once. Use the
same frozen native probe for `--child` in every runtime variant. The report separates
parent startup, idle CPU/allocation, private memory, working set, threads, handles
and disposal. It fails unless all children become ready and exit during cleanup.
Children have a bounded lifetime even if their parent is interrupted. Compare with
`tools/measure-process-sources.ps1`, which reverses order and retains ambient CPU.
Use `--job-processes` (or the controller's `-JobProcesses`, applied to the candidate)
to exercise the same Windows worker ownership policy as Taj and Dalil. It also
records that policy in the result. The child executable remains identical on both
sides.

For a worktree, `measure-keyboard.ps1` accepts `-SessionDirectory` for the installed
stack and `-TestWindow` for a frozen helper. It takes the same desktop lease as native
tests and restores the session only when it stopped one.

`-Geometry -FrameRate 60` also records applied rectangles on each test window's
own UI thread. Use the same newly built `Shubbak.TestWindow` executable for both
daemon versions. `animation-ipc` and `animation-cpu` combine motion with pressure.
`tools/summarise-frame-cadence.ps1 -Root RESULTS` clips those timestamps to the
measured input window and reports interval percentiles and RMS error from the
requested period. It includes every interval, including long gaps; these are
geometry-delivery measurements, not compositor presentation or photon timings.

The keyboard controller accepts `-IpcIntervalMs` to pace query clients, in addition
to the default saturation workload, and records process CPU/private bytes around each
case. Only cases whose loaders and diagnostics also completed enter its aggregate.
Animation cases additionally wait for both HWNDs/adoption, verify frame activity and
record actual geometry changes from the foreign targets. A gaps-command loop without
moving windows is invalid, even if its keyboard numbers look good.

`palette-ui --target-pid PID --operations 100` checks the resident palette's normal
`palette windows` signal, foreground arrival and Escape dismissal. Foreground is
sampled at approximately 1 ms; this is readiness to receive input, not paint completion.
`tools/measure-normal-stack.ps1` compares matching five-binary deployments with the
normal configuration: idle resources, input delivery, repeated palette activation,
reload retention, suspension and resume. It restores the final native stack on exit.
`tools/measure-palette.ps1` provides longer paired activation samples with ambient CPU
counters and matching full-stack deployments. Its guarded input prerequisite establishes
foreground access before the timed opens; a failed prerequisite produces no latency row.

`results.jsonl` has counts, median/p95/p99/p99.9/max and counts over 1/4/8/16 ms.
Each case also retains the daemon's diagnostics. Missing, duplicate, reordered or
wrong-edge input invalidates a run. `AllocatedBytes` in a pass-through receiver is
the measured receiver-thread allocation; hook allocation is reported separately by
`hook-host`. Zero allocation does not imply zero scheduling latency.

The off baseline includes Windows, other resident hooks and this receiver's input
processing. Differences should be assessed across interleaved rounds, not inferred
from a single maximum. CPU load is applied to the off baseline too. Animation/IPC
churn compare daemon versions: that work cannot exist with the daemon stopped.

Synthetic `SendInput` does not reproduce physical USB polling or establish a raw-
input game's device-to-photon latency. Window-message delivery, command notification,
and graphics frame cadence are distinct measurements and are reported separately.
