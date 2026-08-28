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

Inspection, which was the best thing the command line could do and the hardest thing
in the palette to find.

`shubbak inspect` has always been able to say why a window is not being tiled. Dalil
has been able to ask for the same report since it existed — from the bottom of an
action list, reached by an undocumented key, under a name that did not contain the
word "inspect". This release makes it findable, makes it readable, and stops the
palette recovering the report by taking the printed text apart.

### Added

- **`Ctrl+Shift+I` inspects the selected window**, from anywhere in the palette. It is
  the one action the `action-guard` setting does not hold back, and the exemption is
  principled rather than convenient: the guard exists so an action cannot be taken by
  accident, and inspecting takes no action. It is the only entry in the list that runs
  no command at all, which a test holds true.

- **An inspect mode, on the `!` prefix.** Every window Shubbak is *not* managing, each
  saying why on the row itself, ranked so the ones you can do something about — excluded
  by a rule, not adopted yet — come before the ones that are facts about Win32. This is
  the palette's answer to `shubbak inspect --all`, which until now was reachable only
  from a shell.

  It deliberately ignores `show-unmanaged`. That setting keeps unmanaged windows out of
  the ordinary list, which is reasonable and would leave this mode permanently empty.

- **The reason a window is unmanaged now appears in the window list**, in the dim text
  where the workspace would be for a managed window. The window manager has always sent
  this and the palette has always discarded it, so the list could say a window was
  `unmanaged` and never say why.

  It is a new short form of the verdict rather than the existing sentence. The long
  ones run past 150 characters and end with the part that says what to do about it, so
  a clipped row showed the half that was no use.

- **Any report line can be opened in full**, with Enter, and left with Escape or
  Backspace. A row is one clipped line, and the values worth opening a report for — a
  path, a regular expression, the sentence about elevation — are the long ones.

  Wrapping is done by breaking the value across ordinary rows rather than by teaching
  the palette to wrap. Variable row heights would have meant a measuring layout pass and
  a window that resizes underneath the selection; this reuses the frame stack that
  already exists for action lists.

- **`Ctrl+C` copies the selected line, `Ctrl+Shift+C` copies everything on screen.**
  The most useful thing to do with an explanation of why a window will not tile is to
  paste it into an issue, and until now the only way to get one out of the palette was
  to read it off the screen and retype it. Rows are copied whole rather than as drawn:
  a path with an ellipsis in the middle of it is not a path.

- **Backspace goes back** when there is nothing left to delete and a list is open.
  Escape already did. Backspace did nothing at all, which is the least useful of the
  three available behaviours.

- `Ctrl+Enter`, `Ctrl+Shift+I` and `Ctrl+C` are now listed in the palette's own help.
  The first two already worked and were written down nowhere, so the one page somebody
  opens to find a key was the one page that did not mention them.

### Changed

- **The `inspect` IPC method returns a structured `WindowReport` rather than the text
  of one**, and the **IPC protocol version is now 2**.

  The report was built as printed columns in the daemon, and the palette — the other
  client — split that text back apart at the padding to find the labels. The daemon's
  choice of whitespace had quietly become an interface for a different process, with
  nothing anywhere testing it: widening a column would have silently stopped the
  palette's labels being labels.

  The fields are the contract now, and the printed layout is decided in exactly one
  place, next to the code that prints it. **The command line's output is unchanged**,
  deliberately — people have it pasted in issues and sitting in scrollback.

  The version rises because this is a payload whose meaning no longer matches its
  name, which is the documented trigger for raising it. Since the version is part of
  the pipe name, a `shubbak` and a `shubbak-wm` from either side of this change do not
  find each other at all, rather than one showing the other's JSON verbatim.

- **`shubbak inspect` no longer prints the same seven fields twice.** With a window
  manager running it printed a local report and then the daemon's, which repeated the
  handle, title, class, process, path, rect and verdict. There is one report builder
  and one formatter now, and the local path fills in what it can.

- The palette's hint bar says what Enter will actually do to the selected row —
  `inspect`, `open`, `read it`, `do it` — instead of always saying "do it". Every
  overlay advertised "do it", including a report whose rows all did nothing, so the one
  list where Enter was inert was also the one insisting it was not.

- The action is called **"Inspect this window"** rather than "Explain this window". The
  old name described it better and was findable only by somebody who had already found
  it; the description keeps the old wording, and descriptions are searched too.

### Fixed

- **Windows that reopen maximised were tiled while Windows still had them flagged
  maximised.** Store applications — Calculator and Settings among them — remember that
  they were maximised and come back that way. Shubbak adopted one as an ordinary tiling
  window, handed it half the screen, and never cleared `WS_MAXIMIZE`.

  The compositor draws a maximised window on the assumption that it fills the monitor:
  the shadow is suppressed and part of the frame is deliberately put off the top of the
  screen. At half the screen that frame is back on screen as a black strip along the
  top, and the focus border is drawn around a shape that is not the window — which is
  how it was reported, as UWP applications having "a strange border and a thin black
  row on the top" with the border invisible on them.

  `WindowFilter.InitialStateFor` claimed in its own comment that a window "already
  minimised or maximised must keep that state". Only the minimised half was ever
  written. `Win32Window.IsMaximised` existed and was called by nothing, and
  `WindowState.Maximised` existed and was assigned by nothing.

  The flag is now cleared before the window is placed, in the committer rather than at
  adoption. Adoption is only one way in: Win+Up, a double-clicked title bar and an
  application maximising itself all set it later, by which time the drift watch has
  expired and `EVENT_OBJECT_LOCATIONCHANGE` is deliberately not subscribed. The
  committer is the one place every rectangle passes through, and the check runs only
  for windows actually being moved.

  Clearing it goes through `SetWindowPlacement`, which carries the destination with it.
  `SW_RESTORE` alone returns the window to whatever it occupied before it was
  maximised, a visible jump to a stale position immediately before the layout corrects
  it. It is also synchronous, and that matters: the committer places windows with a
  *sending* `SetWindowPos`, and a send overtakes anything merely posted — so the
  asynchronous form would have arrived after the placement and undone it. A window that
  is not answering gets the asynchronous form anyway, rather than blocking a layout
  pass on one stuck application.

- **The action chords did nothing, anywhere, with the shipped defaults.** Every row in
  the action list carries the key that acts on it as a badge — `Ctrl+Shift+S` beside
  "Make sticky", `Ctrl+Shift+W` beside "Close it" — and pressing any of them did
  nothing at all.

  Two guards met. Chords were refused outright whenever a list was open, which is the
  only place they are written down; and from the main list they were refused by
  `action-guard`, which is on by default. So the one place a user could learn a chord
  was the one place it could not work, and the place it could work was the one place
  nothing said it existed.

  A chord inside the action list now always acts. The guard is not weakened by that: it
  exists to stop an action being taken by accident from a list where the keyboard is
  busy searching, and by the time somebody has pressed Ctrl+Enter and is reading a list
  of verbs, pressing the key printed on one of them is no more accidental than pressing
  Enter on it. From the main list the guard applies exactly as before.

  The chord also survives becoming a row now. It used to reach the list only as the
  badge — a caption naming a key, on a row that could not be found by that key.

- **A mode prefix only ever worked from the window list.** In any other mode the query
  already began with one, so typing `!` in the command list produced `>!` — still the
  command list, now searching for an exclamation mark. Only the first character decides
  the mode, and it was never replaced. Every mode but the default was a one-way door,
  escapable only with Tab or Backspace.

  A prefix typed while there is nothing to search now replaces the mode. Once there is
  a search term it stays literal, because typing `#` after `>foo` is somebody spelling
  a query rather than changing their mind.

  This was not new. It applied to all six prefixes for as long as they have existed;
  adding a seventh is what made somebody try to switch between two of them.

- **The hint bar dropped whichever mode came last.** A hint that does not fit is not
  drawn, and the modes are drawn in the order they are declared — so adding `!` pushed
  it past the right-hand edge of a 720-pixel palette and it appeared nowhere at all.

  The bar is now tried at four levels of detail and drawn at the fullest that fits,
  rather than budgeting for a particular width. What is given up, in order: the word
  "modes" beside Tab, which explains a key that explains itself; the advertisement for
  Ctrl+Enter, which is also listed under `?`; and only then the mode names, all
  together — a bar naming three modes and showing four bare caps would read as the
  names belonging to the wrong caps.

  Nothing in it knows how wide Segoe UI is at a given scale, so a wider window, a
  larger font or another mode all settle at the right level on their own.

- **The bar and the palette could each run twice, silently.** Only the window manager
  had a single-instance guard; `taj` and `dalil` had none, and reached the doubled
  state by an entirely ordinary route. Both are designed to survive the window manager
  restarting — they reconnect inside `window-manager-timeout` — and the restarted
  window manager then runs its startup commands, one of which starts each of them.

  Two bars is not merely untidy: each reserves its strip through the shell's appbar
  API, so the work area is taken twice and every tiled window is laid out into a
  desktop shorter than it should be, which reads as a gaps setting gone wrong. Two
  palettes both answer the same signal, so one keypress raises two windows, stacked and
  both topmost, and Escape dismisses one to reveal the other.

  Both now refuse to start a second copy and say so. An uncertain answer starts anyway,
  which is the opposite of what the window manager does with the same uncertainty: two
  bars are visibly wrong and easily undone, whereas no bar at all because a mutex could
  not be opened is worse than the thing being guarded against.

- **Raising the protocol version opened the hole the instance mutex exists to close.**
  The window manager's mutex was the pipe name with `Local\` in front, and the pipe name
  carries the protocol version — so a daemon on the old version held a different name
  from one on the new, and both could run. That is precisely the pairing somebody
  working on Shubbak produces all day: an installed copy running while a build from
  source is started.

  Instance names are no longer versioned. The two questions look alike and are not: a
  pipe asks whether two builds can understand each other, and a mutex asks whether one
  is already running — and two bars reserve the same strip of screen whether or not
  they speak the same protocol.

- **`shubbak dalil-exit`**, alongside the existing `taj-exit` and sharing its
  implementation. Both matter more now that each program refuses to start twice:
  without a way to stop the one that is running, a wedged palette could only be cleared
  through Task Manager, which is a worse position than the double-start the guard
  prevents.

- **The icons are visible somewhere other than the taskbar.** Four were drawn for the
  executables and then only ever seen by Windows: the readme showed none of them, and
  an ICO is not something GitHub renders.

  `tools/make-icons.ps1` now writes the 256-pixel frame of each as a PNG into
  `docs/assets` alongside the ICO it already produced — from the same frames, so the
  picture in the readme cannot drift from the one in the taskbar. The readme leads with
  Shubbak's, and Taj and Dalil carry theirs beside their sections.

  The release zip gains `docs/assets` for the same reason it already carries the readme:
  the links are relative so that GitHub resolves them, and without the images the copy
  in the zip would greet a first-time reader with three broken pictures. The
  executables stay at the root, which is the part winget and Scoop name.

  The same script draws `docs/assets/social-card.png`, the 1280×640 picture GitHub
  shows wherever the repository is linked and a URL unfurls. It is composed from the
  same two functions as the icon, so the tile on the card cannot drift from the tile in
  the taskbar, and it is checked against GitHub's recommended 40pt border rather than
  merely laid out inside it — a caption that fits at full size and is cropped away in a
  Slack unfurl is not a failure anybody would see by opening the file. It has to be
  uploaded by hand under Settings → Social preview.

### Internal

- **A test project for the palette host.** `Dalil.Core` has been tested since it
  existed; the executable had never been, because the decisions lived inside
  `PaletteWindow` and that cannot be constructed without a real window, a message loop
  and a device context.

  What a keystroke means is decided in a new `PaletteInput` now — which chord a key
  spells, whether the guard holds it back, what Enter does to a row, and what a copy
  puts on the clipboard. All four are pure functions of the row and the state it is
  chosen in, and all four are covered.

  It also simplified the chord path: inspecting is found by the same lookup as every
  other chord rather than by a branch of its own, because the action now carries the
  chord like the rest of them.

- **The single-instance mechanics are shared.** The mutex handling — including treating
  an abandoned mutex as free, so a process that was killed does not lock the user out
  until they reboot — now lives in `Shubbak.Native.SingleInstanceLock` and is used by
  all three programs. What stays in the window manager is the part specific to it:
  standing an incumbent down over IPC for `--replace`, and refusing rather than
  carrying on when the answer cannot be had.

## [0.9.0] - 2026-08-27

The first public release: the point at which Shubbak can be installed rather than
built.

Most of what it does predates this entry — tiling, workspaces, the bar, the palette.
What is new is everything needed to hand it to somebody else: a release zip and the two
package managers that serve it, autostart, a starter config, icons. And alongside that,
the things a program which owns your keyboard has to offer before it can be trusted
with it — a way to make it let go, a way to see at a glance that it has, and a refusal
to be running twice.

### Added

- **`wm-suspend`, `wm-resume`, `wm-toggle-suspend`.** Releases the low-level keyboard
  hook and the window event hooks, and leaves every window exactly where it is.

  This is for playing a game, and it is not the same as `wm-toggle-pause`. Pausing
  stops Shubbak rearranging the desktop but **keeps the keyboard hook**, so every bound
  chord is still swallowed and never reaches the focused application — deliberately,
  because the command that resumes is a keybinding and a pause that cannot be undone
  from the keyboard is a trap. That is the wrong property for a game: a chord Shubbak
  swallows is an input the game never sees, which matters far more than the microsecond
  the hook costs (ADR 0001 measured p99.9 at 0.8–1.0 µs).

  The only way to get this before was to exit the window manager entirely, which
  un-conceals every window on every workspace on the way out and costs a full restart
  to undo — measured at over two seconds, plus re-adopting every window, plus the bar
  and the palette restarting with it. Suspending costs none of that: the tree stays in
  memory and nothing on screen moves in either direction.

  While suspended the periodic work stops too — no focus-border re-assertion five times
  a second, no monitor polling — and the loop idles instead of running at frame pace.

  Resuming uses **the same key that suspended**, which works because
  `RegisterHotKey` is not a hook: the system matches that one chord itself and posts a
  single message. Nothing of Shubbak's runs for any other keystroke. `shubbak wm-resume`
  works too, and is the way back if another program already owns the chord.

  `shubbak status` now distinguishes `running`, `running, paused` and
  `running, suspended`, and `shubbak diagnose` reports whether each hook is actually
  installed — because "is it really out of the way" deserves an answer rather than
  trust.

- **`wm-toggle-pause` is unchanged.** Both exist because they are genuinely different.

- **A system tray icon.** Right-click or left-click it for suspend/resume, stop
  arranging windows, reload configuration, open the configuration folder, and exit.
  The labels describe the current state rather than being fixed, because the
  difference between "Suspend" and "Resume" is why anyone opens it.

  It matters most in the state where the keyboard has been given away: suspending is
  undoable from here even if the resume chord is already owned by another program.

  Two details worth recording, because both would be bugs if got wrong. The window is
  **message-only** — parented to `HWND_MESSAGE`, so `EnumWindows` never returns it. The
  program enumerating windows and deciding which to tile is this one, and a findable
  tray window would be a window manager arranging its own plumbing; there is a test
  asserting it stays invisible to Shubbak's own enumerator. And it lives on the daemon
  thread, never the keyboard hook's: `TrackPopupMenu` runs a modal loop while the menu
  is open, which on the hook thread would put every keystroke on the machine behind an
  open menu against a 300 ms deadline.

  The icon is taken from the executable itself, so the tray matches Alt-Tab and the
  taskbar rather than being a second image to keep in step.

- **Taj says when Shubbak has stopped.** New template values `status`, `suspended` and
  `paused`, rendered as pills that hide themselves when there is nothing to report.

  Both states change what Shubbak does without changing anything on screen, so neither
  was discoverable by looking. Suspended is the one that matters: it is
  indistinguishable from a crash — windows stay where they are and no key does
  anything — so somebody who suspended it and forgot has no way to tell the difference
  without trying a command and reasoning about the answer.

  `status` is the combined value for a bar with room for one pill, and suspended wins
  when both hold. `suspended` and `paused` are separate so a config can show two and
  give each the click that undoes it — a pill saying "suspended" that resumes when
  clicked is a way back that does not need the keyboard, which is the one thing
  suspending took away.

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

- **Two window managers could run at once, silently.** There was no single-instance
  guard of any kind, and the named pipe could not serve as one: it is created with
  `MaxAllowedServerInstances`, which is precisely the flag that lets any number of
  processes host the same name.

  So a second `shubbak-wm` started perfectly happily, and then the two fought — two
  keyboard hooks, so every binding ran twice; two layout passes issuing contradictory
  `DeferWindowPos` batches; a CLI reaching whichever accept loop won the race, so
  consecutive commands could land in different processes; and on exit, one daemon
  un-concealing windows the other still had recorded as concealed. Nothing reported any
  of it.

  A second launch is now refused with a message naming the running process. `--replace`
  asks the running one to stand down over IPC — so it saves its session and restores
  its windows rather than being terminated — and waits for it to let go before starting.
  An abandoned mutex, left by a daemon that was killed rather than asked to exit, counts
  as free rather than as someone else running.

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

- **`wm.suspended` was published but could not be subscribed to by name.** The topic
  was missing from `IpcProtocol.Topics`, which is what a subscription is checked
  against — so `shubbak sub wm.suspended` was refused for a topic the daemon was
  actively publishing. It also carried an empty `{}` payload rather than saying what
  had changed.

  There was already a test for exactly this, `EveryPublishedTopicIsDeclared`, and it
  passed — because it compared the topic list against a **second hand-maintained
  list**, which had drifted in the same way and for the same reason. It now derives
  the published topics from the event types themselves, so it cannot drift again.
  Verified by removing the entry and watching it fail.

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

[0.9.0]: https://github.com/MoaidHathot/Shubbak/releases/tag/v0.9.0
