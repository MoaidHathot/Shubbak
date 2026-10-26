# Intermittent animation latency

Measured on 2026-10-26, starting with the 0.11.0 daemon and a clean `main`.
The reported case was removing one of two tiled windows: the survivor sometimes
expanded sluggishly, then later attempts were fast again.

## Focus hand-off

The running daemon targeted 60 Hz. Its frame interval was median 17.37 ms,
p99 53.50 ms; focus hand-off was median 12.14 ms, p99 96.56 ms, maximum
151.65 ms. A recorded removal spent 121.8 ms in focus, and the motion report
counted seven of fourteen expected frames over 214 ms. These are observations of
that run, not performance guarantees. In particular, the old motion timestamps
included work before the first frame was actually posted.

`ApplyLayout` calls `WindowActions.Focus` on the frame-driving thread. The latter
attached that thread to the foreign foreground's input queue before asking for
activation. A new controlled test window, in a separate process, acknowledges
when its UI thread starts a bounded two-second pause. The existing same-process
focus-sink tests could not exercise the attachment branch.

Temporary phase instrumentation of the old implementation measured:

| Busy foreground, old path | Time |
|---|---:|
| AttachThreadInput(true) | 0.42 ms |
| SetForegroundWindow | 2012.62 ms |
| AttachThreadInput(false) | 0.37 ms |

Removing the attachment, while retaining the existing permission fallback, gave:

| Separate input queues | Request time | Eventual foreground |
|---|---:|---|
| Busy foreground | 0.62 ms | requested window |
| Busy target | 0.70 ms | requested window after it resumed |

The regression asserts that the request does not wait out the foreign thread's
pause and that the requested window really receives focus. It also checks repeated
cross-process transitions and zero managed allocation in warmed focus requests.
The temporary instrumentation is not shipped. No thread, queue, polling or privilege
grant was added to production; the keyboard callback is untouched.

Reproduce with the user's daemon stopped:

```powershell
dotnet test tests/Shubbak.Native.Tests -c Release --filter FullyQualifiedName~FocusLatencyTests --logger "console;verbosity=detailed"
```

The whole Release solution passed before changes (15 test projects). Native and
end-to-end tests serialize desktop access and isolate their daemons' state from
the user's session.

## Animation clock

A regression exercising the old daemon's clamp failed: a 140 ms linear resize,
advanced by 16, 100, then 24 ms, remained active. The clamp handed the engine only
16 + 32 + 24 = 72 ms. This was an explicit policy in the old clamp tests, justified
as protection against a new track inheriting a preceding idle wait.

That protection now belongs to each track's first tick. The daemon supplies one
frame interval for a fresh track and the full measured delta for a running one.
A first-frame flag also handles a zero initial step correctly and survives track
compaction. There is no new timer or callback: the existing post-layout timestamp
is reused, rather than backdating frames to the beginning of the tick. The engine's
two-argument Tick remains available for callers that already supply track-relative
deltas; the daemon supplies the explicit initial step.

Deterministic tests cover idle starts at 60/144/240 Hz, a 100 ms stall, a five-second
stall with a new track joining an old one, retargeting only one of two tracks,
zero-step starts and a later motion reusing the same slot. The frame-allocation
tests use the daemon's new call shape and still require exactly zero bytes.

## Redundant retargeting

Before the fix, eight cases in the new retarget regression suite failed: redundant
layouts extended the deadline, changed easing trajectories, replaced an opening's
profile with a move's, and lost the final-frame path near the rounded target.

Retarget now compares the destination of the existing track before applying the
minimum-distance shortcut. An unchanged destination retains elapsed time, origin,
curve and animation kind. Disabled animation and an explicitly instant request
still cancel immediately. Reconfiguring the original active profile restarts from
the current position with the new profile, including when a later layout merely
supplies the default window-move kind. The lookup is shared with track creation,
so this adds no second scan, dictionary, allocation or per-frame system call.

The tests compare an interrupted curve with an uninterrupted control, exercise
duration and curve changes, preserve the settling frame after rounding, and require
zero allocations for both unchanged and genuinely changed destinations.

## The same stall in Dalil

The follow-up review found Dalil still attaching input queues and synchronously
calling BringWindowToTop before SetForegroundWindow. A test using the same foreign
window and a local companion window reproduced a 2025.00 ms wait. Removing those
two calls reduced it to 9.40 ms. The test verifies both the actual foreground and
WM_SETFOCUS on the local window, including repairing local focus when it was
already the foreground window. SetActiveWindow and SetFocus remain on the palette's
own queue; the existing refused-request input fallback and UIPI boundary remain.

The helper client is shared by the two suites. Dalil's test takes the same desktop
mutex as the native/end-to-end suites and carries their Requires=Foreground trait.

## End-to-end verification

The real daemon now has a regression for closing the focused member of a two-window
tile while repeatedly requesting `wm-redraw`. It reads back actual visible bounds,
checks that the survivor received foreground keyboard focus, and asserts zero
frame-path allocation in the daemon's own diagnostic report. It exercises both the
normal 140 ms duration and a deliberately long 1000 ms linear curve.

The saved pre-change NativeAOT daemon failed the 1000 ms scenario: the survivor
still had not arrived after 2003.2 ms and 124 redundant redraws. The corrected managed
daemon finished a run in 1017.0 ms including detection, despite 61 redraws, reporting
60 animation frames over 996 ms and zero bytes allocated on the frame path.

Foreground tests now record actual arrival inside the controlled window's UI thread,
instead of assuming it must still hold the desktop's foreground after a two-second
pause. The removal test also explicitly focuses the departing tile first rather than
assuming an enumeration order. Interactive runs encountered Windows Search holding
the foreground and refusing the setup request. A controlled run with the desktop
idle passed all 3605 test cases, with no failures or skips. Last-input timestamps and
the foreground holder are included when diagnosing an interrupted focus test.

The keyboard callback, binding table, IPC permissions and elevation/UIAccess policy
have no changes. No production worker thread or polling mechanism was added. The
frame clock reuses an existing timestamp; first-frame state and animation kind fit
in the preallocated track array. Tests assert zero allocations for frame stepping,
both retarget paths, and warmed foreign focus requests.

Both modified executables (`shubbak-wm` and `dalil`) were published as win-x64
NativeAOT without warnings. The removal scenario was then run with the published
daemon substituted for the test fixture's managed apphost:

| Configured duration | Close to observed arrival | Motion's frame span | Redraw requests | Frame allocations |
|---|---:|---:|---:|---:|
| 140 ms | 162.8 ms | 138 ms (9 frames) | 7 | 0 B maximum |
| 1000 ms | 1019.8 ms | 991 ms (59 frames) | 62 | 0 B maximum |

Arrival includes event detection, IPC and the test's 10 ms observation interval;
the motion span is the daemon's frame clock. The corresponding frame-interval
medians were 16.95 and 16.99 ms. Private memory in this two-window test was 6.2 and
7.1 MiB. Process CPU counters reported no increment during these short native runs;
that is below their measurement resolution, not a claim that the work uses no CPU.

The normal session and all companions were restarted from `dist`. In a 10.05-second
idle observation the daemon had no measurable process-CPU increment, 11.01 MiB
private memory and a 31.14 MiB working set. Its live report showed zero frame-path
allocation across 42 samples, zero dropped keystrokes, zero hook faults and no GC
collections since restart. The previous long-lived session is not a comparable
memory baseline, so these numbers are observations rather than claimed RAM savings.

Release tests, documented test count, all 39 config snippets, 30 example checks,
the example config, release-version consistency and the generated diagnostics
catalogue passed. Tracked text files were checked for LF endings and the working
tree for a `nul` file. No temporary timing instrumentation remains in production.

## CI follow-up, 2026-10-27

The first hosted run of these changes
([40414798593](https://github.com/MoaidHathot/Shubbak/actions/runs/40414798593))
passed the build and every native/keyboard test, but failed both new removal cases:
their windows reached the expanded rectangle in 62-70 ms with zero redraws observed,
including the configured 1000 ms motion. The test had assumed that enabling motion
in the config was enough, although Shubbak also respects the system preference.

Reproduced locally by temporarily clearing `SPI_SETCLIENTAREAANIMATION`: both cases
failed identically, reaching their targets in 56-58 ms with zero redraws. The fixture
now explicitly controls that session preference under the desktop lease, restores
the original value on every exit, and does not persist or broadcast the change. A
separate case verifies that system-disabled animations really do place immediately;
a native test verifies restoration when the test body throws. The daemon reports
the setup facts before any motion, so an unavailable prerequisite is visible.

That early report exposed a second issue: startup had not yet queried the system
and could report the default `True` while Windows said `False`. Startup/resume now
read display preferences with their initial monitor snapshot, before layout or IPC,
and stamp the poll time so the first tick does not repeat the read. The fixture also
requests its initial focus through `focus-window` instead of racing the daemon with
a second foreground request from the test process.

For the short motion, recorded frames prove animation even if the runner does not
return from IPC inside 140 ms. The long scenario still requires redundant mid-flight
redraws, and both animated cases retain their deadline and zero-allocation checks.

The full Release suite was run with the same coverage collector as CI, starting
with system animations disabled: **3607 passed, zero failures or skips**. The session
preference was still disabled at the end of the suite, proving the fixtures restored
it, and was then restored to the user's original setting before restarting Shubbak.
