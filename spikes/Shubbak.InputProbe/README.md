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

The keyboard controller accepts `-IpcIntervalMs` to pace query clients, in addition
to the default saturation workload, and records process CPU/private bytes around each
case. Only cases whose loaders and diagnostics also completed enter its aggregate.

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
