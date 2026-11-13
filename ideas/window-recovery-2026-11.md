# Concealed-window recovery and resume — November 2026

## Report and evidence

A second Teams tile appeared in the Chat workspace with only a grey background.
The retained logs identify the closed window as `ms-teams`, class `TeamsWebView`,
title **Microsoft Teams**, handle `0x10870`. The ordinary chat window had handle
`0x30532`.

The earlier native performance artifacts preserved the transition:

| Time on 13 November | Observation |
|---|---|
| 21:12:15 | The .NET 10 baseline left `0x10870` concealed because no session entry claimed it. |
| 21:16:01 | After configuration reloads and suspend/resume, that same baseline claimed the helper. Managed windows increased from five to six. |
| Subsequent restarts | The helper was now in the saved session and was adopted again. |
| 22:42:03 | The normal-session log recorded the user closing that exact extra window. |

The defect predates the .NET 11 changes. The performance exercise exposed it and
the window-count increase was missed as a correctness failure in that review.
`tools/measure-normal-stack.ps1` now compares the actual managed handle sets across
reloads and suspend/resume, retaining both sets and failing if they differ.

Raw evidence for this fix is retained locally under
`artifacts/window-recovery-20261113/`; the earlier transition remains in
`artifacts/net11-20261113/final-normal-stack/baseline-r0/after.md`.

## Mechanism

Resume reused startup adoption, including loading a saved session. `TryManage`
returned immediately for windows already in the live tree, before consuming their
session entries. Reload had cleared the helper's temporary startup verdict. The
helper could therefore take the real window's unclaimed entry: the matcher treated
process and class as sufficient, with title and handle only serving as tie-breakers.

That scoring remains useful for placing a visible application after it restarts.
It is insufficient evidence for revealing a hidden window. The standalone
`shubbak restore` path shared the mistake: it enumerated only concealed candidates,
so even a still-visible real window could lend its entry to a hidden helper.
Startup enumeration had another ordering problem: a new visible window could take
a surviving concealed window's entry before that surviving handle was examined.

## Win32 constraints checked

- [`IsWindowVisible`](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-iswindowvisible)
  reports `WS_VISIBLE`, not whether a window is actually composited. A shell-cloaked
  window can still report visible.
- [`DWMWA_CLOAKED`](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute)
  distinguishes application, shell and inherited cloaks. A shell cloak alone does
  not identify Shubbak as the requester; Windows virtual desktops use it too.
- [`IsWindow`](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-iswindow)
  explicitly documents handle recycling and the possibility of a window disappearing
  after a check. Process/class checks and the recorded owning PID strengthen a handle
  match; a destroyed or differently owned handle must not receive the old window's
  restore or border cleanup.
- [`EnumWindows`](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enumwindows)
  supplies top-level handles, not a session-restoration priority. Exact surviving
  matches are handled before placement guesses, independently of enumeration order.

The implementation keeps Win32 observation and concealment in `Shubbak.Native`,
session matching in the Win32-free core, and lifecycle reconciliation in the daemon.

## Changes

1. **Recovery uses a recorded nonzero handle and agreeing application identity.**
   A recorded PID must also agree. Process/class/title fallback is retained for
   visible placement. The optional PID field keeps version-1 session files readable;
   older handle-containing entries still work. Handleless entries can place visible
   windows but cannot justify automatically revealing a hidden candidate.
2. **Startup gives surviving handles their own entries first.** A new window of the
   same application cannot consume an entry needed by a concealed survivor.
3. **Resume reconciles the live tree.** It retains owned concealment, removes windows
   that closed or hid themselves while hooks were off, reconsiders tray windows that
   showed themselves, and retains explicit exclusions for windows still present.
   It refreshes titles, native state and floating geometry, and follows foreground
   through the normal foreground-event policy. The old session's monitor view is not
   replayed.
4. **Recovered concealment becomes live ownership.** Adoption records an inherited
   hide or shell cloak without revealing it. This matters when an inactive workspace
   stays hidden: `Hide` previously returned early for an already-invisible window,
   leaving no ownership for another resume or for shutdown. Application-level cloaks
   remain the application's to lift, consistently with the existing reveal path.
5. **Session persistence records the current identities and empty state.** The routine
   fingerprint now includes handle and PID. Replacing a browser window between saves
   no longer leaves the obsolete handle on disk. Dismissing the last managed window
   no longer preserves a stale nonempty session. Stopping while suspended reconciles
   departures before saving as well.
6. **Current native minimise/maximise state is respected.** A saved tiled placement
   no longer turns an already-minimised window into an empty tile. Resume preserves
   the base tiled/floating state for restoration. Native maximised/fullscreen bounds
   do not overwrite a floating restore rectangle.
7. **Desktop-test cleanup is scoped to the test.** It requests a clean isolated stop
   first, ends its own daemons before its helpers, and limits recovery to its isolated
   session instead of revealing every cloaked window on the desktop.

## Regression evidence

Before production changes, the new native tests demonstrated all three unsafe
matches: a visible real window lending its entry to a helper, the wrong concealed
handle winning enumeration, and a handleless legacy entry claiming a hidden window.
The session-save regression independently showed the obsolete handle remaining on
disk after a replacement.

All six initial desktop cases failed against the unchanged baseline: the helper
was adopted, a shown tray window inherited the wrong placement, closed/hidden
windows stayed managed, or a new visible window prevented genuine recovery.

The expanded checks exercise:

- A foreign process with a normal window and an identical hidden helper: same PID,
  class and title; three reload/suspend/resume cycles and a clean restart.
- A stale saved monitor view, while the live current workspace remains authoritative.
- An actual killed daemon and recovery through both `SW_HIDE` and a verified shell
  cloak, followed by another suspend/resume and clean shutdown.
- Standalone CLI restoration, first with the real window visible and then hidden;
  the helper stays hidden in both cases.
- A tray window shown while suspended, a closed window, and a last window hidden to
  the tray, including stopping directly from suspension.
- Minimising/maximising and restoring both tiled and floating windows while hooks
  are off, floating-window movement, and changed title/foreground.
- PID mismatch, changed titles, claimed-entry consumption, and older session files.

Testing the first reconciliation implementation caught the missing inherited
concealment ownership; the implementation and regression now cover that sequence.
The floating-move test also observed Windows rounding a DPI-unaware helper by one
pixel at 150% scaling. Its setup uses the existing geometry tolerance, then verifies
that resume records the actual landed rectangle.

## Verification

- Release solution build: zero warnings and errors.
- Final combined solution run with coverage: **3,900 passed, zero failed or skipped**,
  including the standalone CLI integration case.
- Published x64 NativeAOT daemon and CLI: zero trim/AOT warnings; **23 end-to-end
  scenarios passed** against those executables, including standalone restoration.
- Native recovery and cross-process shell-cloak checks: **25 passed**.
- Documentation snippets, examples, example configuration, release consistency,
  diagnostic catalogue and documented test count passed.
- The verified native binaries were installed with the existing five managed
  handles and workspace assignments preserved. Teams remained one managed window.

Native executable growth against the installed baseline is 6,656 bytes for the
daemon and 1,536 bytes for the CLI.

## Combined native and normal-desktop checks

The installed pre-fix binaries were retained and hashed before replacement. The
native keyboard comparison uses the same frozen probe for both versions, two rounds
in reversed order, isolated daemon state and test-owned windows. The normal session
is restored on every exit. Results, diagnostic reports, process counters and applied
geometry traces are retained with the test artifacts.

| p99 delivery, milliseconds | Before | Fixed |
|---|---:|---:|
| Plain application input, idle | 1.652–1.787 | 1.706–1.723 |
| Plain input, animation plus four saturated IPC clients | 6.665–7.364 | 6.737–7.295 |
| Alt-modified command notification, idle | 1.116–1.164 | 1.132–1.200 |
| Alt-modified command notification, saturated IPC, qualified repeat | 1.042–1.103 | 1.157–1.168 |

These short samples are regression observations, not a keyboard speedup claim.
Other applications remain active, tail latency varies, and the fixed build has
slightly slower modified-shortcut rows; those rows are retained. The change runs
during adoption/resume and session persistence, and does not add work to the
keyboard callback or animation-frame implementation.

Two observations changed how the evidence was qualified:

- An initial modified-shortcut run aborted when foreground moved to Firefox after
  1,914 tagged edges. Its partial result is excluded, and the incomplete IPC pair
  was repeated.
- A baseline-only row in that repeat recorded two menu requests. The harness had
  reported these without rejecting the row. `measure-keyboard.ps1` now rejects menu
  requests for bound-key measurements, checks duplicates/order explicitly, and saves
  probe stderr on failure. The entire IPC comparison was repeated under that gate.
  The earlier rows remain available as evidence rather than being overwritten.

The qualified set, including normal-session checks, contains **63,000 application
input edges and 16,800 command notifications**, warmup included. Missing, duplicate,
out-of-order, wrong-edge, leaked-bound-event and menu-request counters are all zero.

Under simultaneous animation and IPC load, both versions delivered geometry to the
two foreign UI threads at a mean **16.67 ms**. Fixed-build geometry p99 was
17.95–18.39 ms versus 18.11–18.30 ms before; neither version recorded intervals below
half or above twice the requested frame period in these samples. These are geometry
delivery timings, not display presentation or photon timings. All frame-allocation
samples reported **0 B**, including maxima.

The installed normal configuration then completed **ten reload/suspend/resume
cycles** with the same five managed identities, workspace placements and monitor
views. Application input was checked before, during the final suspension and after
resume. The resume command, including CLI startup and IPC, took 33.53–38.92 ms.
The final normal-session report retained five managed windows and one Teams window,
and reported frame allocation p99/max of 0 B. Raw resource snapshots cover the whole
exercise, including diagnostics, transient input windows and configuration reloads;
they are not an isolated idle-memory or CPU improvement measurement.
