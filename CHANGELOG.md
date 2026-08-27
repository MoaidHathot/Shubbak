# Changelog

Notable changes, newest first. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Two version numbers are deliberately not this one, because they change on their own
schedule and breaking either is a different kind of event:

- **The IPC protocol** is versioned inside the pipe name (`shubbak-v1-<SID>`), so a
  new CLI and an old daemon fail to find each other rather than misunderstand each
  other.
- **The session format** is versioned in the file, so an unreadable session is
  discarded rather than misread.

## [Unreleased]

### Fixed

- **A window that moved itself stayed moved.** Reopening Firefox put it on the wrong
  monitor, on top of the window already tiled there, and it stayed until `wm-redraw`
  was pressed.

  Applications reposition their own windows — a browser restoring the geometry it
  remembered from last time does it a moment after its window appears, which is after
  Shubbak has placed it. Windows announces that only through
  `EVENT_OBJECT_LOCATIONCHANGE`, which Shubbak does not subscribe to and still does
  not: the callbacks arrive on the message queue the pump waits on, so a single
  dragged window used to produce 122 wake-ups a second and pace the animation loop
  against its own output.

  What made this *stick* rather than correct itself was the committer's skip check.
  It asks "is this window already where I last told it to be", judged on the target
  alone — so once a window had wandered, every later pass skipped it, because the
  target had not changed and there was seemingly nothing to do. It now also asks
  whether the window is still there, but only for windows it was about to skip, and
  only coarsely: a different monitor, or a long way from its tile. An exact comparison
  was tried once before and reverted, because a terminal snapping to whole character
  cells never lands precisely where it was put and so was re-placed on every layout —
  which, since focus changes run a layout, was a twitch on every focus change.

  Newly adopted windows are additionally looked at twice, about 300 ms and 900 ms
  after being placed, which catches the displacement without waiting for something
  else to trigger a layout. Twice and then never again: an unbounded watch is how this
  becomes a window manager arguing with an application several times a second for as
  long as both are running.

  Worth recording, since it was checked: **neither GlazeWM nor komorebi handles this
  either.** Both subscribe to `EVENT_OBJECT_LOCATIONCHANGE` and both discard it for
  tiling windows — GlazeWM falls through to a bare `_ => {}`, komorebi forwards it
  only to its border overlay. GlazeWM's version of the bug is transient because its
  commit path has no skip check, so the next redraw of that container corrects it;
  komorebi's is masked for this specific case by an allowlist that turns a title
  change into a re-tile, and names Firefox as the reason it exists.

## [0.9.0] - 2026-08-26
- **The scratchpad was one-way.** Stashing worked; pressing the same key again did
  nothing at all, silently.

  Every command that declares `TargetsFocusedWindow` is checked against the foreground
  window before it runs, and refused if that window is not one Shubbak manages.
  Stashing the last window on a workspace leaves nothing focused, so the foreground
  became the desktop — and the next press was refused before `ToggleScratchpad` could
  run. Summoning needs somewhere to *put* a window, not a window to act on, so an
  occupied slot is now exempt from that check.

  The single-window case is the common one, because a scratchpad is what you reach for
  when you want the screen to yourself — which is exactly the case that could never be
  undone.

- **`scratchpad` could not fail to parse.** Its case in the parser ended in an
  unconditional `return true`: an unrecognised option was skipped, no positional
  remained, and the slot silently became `default`. So `scratchpad --hide notes`
  stashed into `default` and summoned from `default`, appearing to work until somebody
  used two slots and found one had swallowed the other.

  `--show`, `--hide`, `--toggle` and friends are now `SHB0312`, and the message says
  the command is already a toggle rather than only listing what is allowed. A trailing
  `--name` with nothing after it is `SHB0313` instead of quietly meaning `default`.

- **Dalil aimed every action on a stashed window at a window about to vanish.** All of
  them were built on a `focus-window` prefix, and focusing a cloaked window reveals it
  without unstashing it, so it concealed itself again at the next layout pass — which
  reads as the palette having done nothing. `PaletteEntries` documented why the row
  itself must summon by slot; `PaletteActions` did not follow suit. It now does, so
  closing, tagging and un-managing a stashed window reach it. "Go to it" becomes
  "Summon it", and "Bring it here" is dropped as a duplicate of it.

## [0.9.0] - 2026-08-26

The first public release. Everything below already worked; what changed is that it
can now be installed rather than built.

### Added

- **`shubbak autostart enable | disable | status`.** Registers the window manager to
  run at logon under `HKCU\...\CurrentVersion\Run`. There was previously no way for
  Shubbak to start itself: `startup-command` launches other programs once the daemon
  is already up, which covers the bar and the palette but not the thing running them.

  `status` reports two failures that were otherwise silent — a registration pointing
  at binaries that have since been deleted, and one pointing at a *different* copy
  than the one you are running, which is what "I updated it but the old version keeps
  starting" actually is.

- **`shubbak config init`.** Writes a short starter config — five workspaces and the
  bindings to drive them — to `$XDG_CONFIG_HOME/shubbak/shubbak.kdl` or
  `%USERPROFILE%\.config\shubbak\shubbak.kdl`. It refuses to overwrite an existing
  file without `--force`.

  This command was already being recommended before it existed: the loader answers a
  missing config with *"hint: Run 'shubbak config init' to write a starter config"*,
  and that instruction fell through the CLI's dispatch to the daemon, which replied
  "no window manager is running". The first thing a new install said to a new user was
  an instruction that failed.

  It writes a short file rather than a copy of `shubbak.example.kdl`, which is 600
  lines and exists to explain every setting — the right thing to read, the wrong thing
  to inherit.

- **`--version` on all four executables.** It previously did not exist. On the CLI it
  fell through to the daemon as a window manager command, so asking a stopped Shubbak
  for its version was answered with "no window manager is running".

- **`--foreground` on `shubbak-wm`**, for running it attached to a console. See the
  subsystem change below.

- **`--help` on `dalil`**, which had none, and **`--quiet` on `taj` and `dalil`**,
  which only the daemon had.

- **Application icons** for all four binaries. They had none, so Alt-Tab, the taskbar
  and Task Manager's Startup tab all showed the generic placeholder. Generated by
  `tools/make-icons.ps1` rather than committed as opaque files, so a change to them is
  a diff somebody can read.

- **A release workflow.** Tagging `v*` publishes a flat zip of the four NativeAOT
  binaries with a SHA256, plus a separate symbols archive.

### Changed

- **`shubbak-wm` is now a GUI-subsystem binary.** This is the one change with a
  visible consequence.

  The subsystem is a field in the PE header that the loader reads before any of our
  code runs. A console-subsystem process started by something without a console of its
  own — Explorer, a shortcut, Task Scheduler, the `Run` key — has one allocated for
  it, and it stays for the life of the process. Autostart would therefore have left a
  black window on the desktop at every logon, with no runtime flag able to suppress
  it.

  What this costs, and how it is paid:

  - Run it in a terminal and it now attaches to that terminal only when asked, with
    `--foreground`. The shell does not wait for a GUI-subsystem process, so output
    arrives underneath your next prompt. Redirect if that matters.
  - **Startup failures still report.** A daemon that cannot load its config and says
    nothing would be indistinguishable from one that was never launched, so the
    failure path takes a console of its own and holds it open long enough to read.
  - Console logging now follows `--foreground` instead of defaulting on.

  The alternative was launching the daemon at logon through a hidden PowerShell, which
  flashes on many machines and produces exactly the process tree — a shell spawning a
  keyboard-hooking binary at logon — that an antivirus heuristic is built to distrust.

- **`taj` and `dalil` no longer format log entries they cannot write.** Both are
  GUI-subsystem binaries that had console logging on by default, so every entry was
  built and then discarded. Console output now follows whether output actually leads
  anywhere.

- **`shubbak diagnose` reports `0.9.0` rather than `0.9.0.0`.** The four-part form is
  what `AssemblyName.Version` gives; comparing it against a `v0.9.0` tag looks like a
  mismatch that is not one.

- **`shubbak-wm --check-config` now points at `shubbak check-config`**, which
  validates the bar's section of the same file as well and is the fuller check.

### Fixed

- **`taj --help` printed nothing at all.** It is a GUI-subsystem binary writing to a
  console it did not have. Same for every diagnostic it wrote before its window
  existed.

- **The manifest declared version `1.0.0.0`** while the product was `0.9.0`. Both
  manifests are now checked against `<Version>` during the build, so the mismatch is
  an error rather than something to notice later.

### Verified

- **The NativeAOT claim.** All four executables now publish AOT with zero trim or AOT
  warnings, which had never been tested: the only evidence in the repository was for a
  spike, and all four defaulted to `PublishAot=false`. CI now publishes them on every
  push, runs each one, and checks its subsystem — so a release is not the build that
  discovers a problem.

- The `net10.0-windows` target pulls in ~25 MB of CsWinRT projections that nothing
  uses. AOT removes them entirely; the four binaries total about 19 MB, and the zip is
  under 9 MB.

### Known limitations

- **Not code signed.** SmartScreen will warn on first run. More importantly,
  `uiAccess` requires an Authenticode signature *and* installation under
  `%ProgramFiles%`, so this build cannot move windows belonging to elevated processes
  unless it is itself run elevated. A signed installer is the next release.

- **x64 only.** There is no ARM64 configuration yet.

[Unreleased]: https://github.com/MoaidHathot/Shubbak/compare/v0.9.0...HEAD
[0.9.0]: https://github.com/MoaidHathot/Shubbak/releases/tag/v0.9.0
