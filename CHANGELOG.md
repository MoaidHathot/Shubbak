# Changelog

Notable changes, newest first. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Two version numbers are deliberately not this one, because they change on their own
schedule and breaking either is a different kind of event:

- **The IPC protocol** is versioned inside the pipe name (`shubbak-v2-<SID>`), so a
  new CLI and an old daemon fail to find each other rather than misunderstand each
  other.
- **The session format** is versioned in the file, so an unreadable session is
  discarded rather than misread.

## [Unreleased]

### Changed

- **Taj waits instead of spinning.** The bar's message loop ran every 16 ms - sixty-two
  passes a second, forever - and almost every one found nothing had changed and did
  nothing. That poll existed only to notice a dirty flag being set from a timer or the
  pipe, which is what a wait handle is for, and the palette next door had been doing it
  properly all along. The model now says when it changes and the loop waits for that,
  with a one-second ceiling as a safety net so a signal nobody wires up later shows as
  a second of staleness rather than a bar that has quietly stopped. Loop passes per
  second, counted in the loop itself: **62.5 before, 1 after** while visible, and 4
  while stood down - the latter being the rate at which a full-screen stand-down is
  re-confirmed, so it stays a quarter of a second.

  Measured as an A/B on one machine, same method, 150 seconds each, bar visible and
  untouched: **5.208 ms/s before, 2.708 ms/s after - 1.9x**. The loop was about half of
  what the bar spent; the rest is its pipe connections and its timer callbacks, which
  are unchanged.

- **Suspending now means no wake-ups at all, rather than nearly none.** The loop woke
  once a second while suspended, on the reasoning that a wait has to end eventually
  and that an idle thread waking once a second is free. The second half was true and
  the first was not: every path that can end a suspension signals the pump - an IPC
  request wakes it explicitly, and the resume hotkey arrives as a thread message the
  wait already watches for - and the wake handle is an `AutoResetEvent`, so a signal
  raised while a pass is running is remembered rather than lost. Measured: **32 ticks
  in 30 seconds before, 1 after**, and that one was caused by the diagnostic call
  taking the measurement. `diagnose` no longer accumulates loop statistics while
  suspended, which is the one thing given up.

- **Taj stands down when nothing on screen is showing it.** It kept polling and
  rebuilding behind a full-screen game, and while the window manager was suspended -
  which is what someone does *before* playing one. It now stops its interval and clock
  sources and widens its wait whenever either holds. Messages are still pumped, so the
  indicator saying why it stopped stays clickable, which is the way back that does not
  need the keyboard.

  The full-screen half deliberately does not trust `ABN_FULLSCREENAPP` alone, because
  it reports an opening and a closing rather than what is in front; the shell is asked
  again through `SHQueryUserNotificationState` on every slow pass, so a mistaken
  stand-down lasts a quarter of a second rather than until the application closes.
  Sources restart by taking a reading immediately rather than waiting out their
  interval, so a bar returning from a long game does not come back showing a stale
  clock.

  Together, measured over 150 seconds with the window manager suspended:

  | | before | suspended, after |
  |---|---|---|
  | `taj` | 5.208 ms/s | **0.104 ms/s** |
  | `shubbak-wm` | — | **0.000 ms/s** |

### Fixed

- **The focus border flickered on the window being moved to.** Reported on Windows
  Terminal and nothing else. Shubbak sets the border when focus arrives; the
  application then repaints and resets `DWMWA_BORDER_COLOR`, and the healing timer puts
  it back up to a quarter of a second later - so the border is set, cleared, and healed,
  and all three are visible. That timer cannot close the gap on its own: it asks every
  200 ms but the loop sleeps for 250 ms when nothing is pending, so it runs about once
  per idle wait. The border is now re-asserted every 40 ms for two and a half seconds
  after focus lands *or* after the window is resized, and the loop shortens its wait
  only while that is outstanding - so this is paid just after something moved rather
  than on every tick for ever.

  Sized by measurement, after two wrong guesses. `DWMWA_BORDER_COLOR` is write-only -
  `DwmGetWindowAttribute` refuses it - so the screen is the only witness, and a pixel
  sampled during the gap came back a grey close enough to the configured unfocused
  colour to look like Shubbak painting it. Running with the unfocused colour
  temporarily set to bright green settled that: the flash is never green, so nothing
  here writes it, and what shows during the gap is Windows' own default border, which
  is what a cleared attribute looks like. A plain focus change loses the border for
  250-535 ms. A focus change that also *resizes* the window - moving another window
  onto its workspace takes the terminal from full width to half - clears and re-clears
  it for over two seconds, because re-laying out a character grid repaints far longer
  than one activation does. Twelve of twelve runs of that sequence now reach the
  focused colour in 16-47 ms and hold it.

  Not a regression: it reproduces identically on a build from before any of this work.

- **Dalil's float/tile row could send the verb that was already true, and do nothing.**
  `Ctrl+Shift+F` floated a window, and pressing it again to tile did nothing at all
  while Enter on the same row worked. The row chose between `float` and `tile` from the
  window's state, and that state is a snapshot: the host seeds a reopened palette from
  its cached read before the fresh one lands and refuses to refresh at all while the
  palette is closed, a drill-in frame is frozen when it is pushed and deliberately
  ignores refreshes, and the action itself is resolved from a window captured when the
  row was built. Any of those can describe a floating window as tiled - and `float` on
  something already floating returns from `SetWindowStateCore` without an event or an
  error, so the key looked dead. Both wordings now send `toggle-floating`, exactly as
  Minimise and Make sticky beside them have always sent one command each and varied
  only the label. A toggle cannot be stale, and the help screen has always described
  the chord as one.

- **Clicking another window broke a full-screen video, and leaving full-screen left
  the window in the wrong place.** An application that goes full-screen by itself - a
  browser playing a video, a game in borderless mode - resizes its own window and
  tells nobody: the only event that reports it is `EVENT_OBJECT_LOCATIONCHANGE`, which
  Shubbak deliberately does not subscribe to. So the tree still said *tiling*, and
  since a focus change re-runs the layout, the first click elsewhere dragged the
  browser back into its tile while the browser still believed it was full-screen -
  Taj reappearing over a window that was now neither. Leaving full-screen was worse:
  the application restored its own geometry, the committer skipped the window as
  already where it was put, and it stayed wrong until something changed its target
  rectangle - which is why moving *another* window onto the workspace healed it.
  Shubbak now notices both by looking, at the top of every layout pass and on the
  loop's existing idle tick, and gives such a window the whole monitor without taking
  its tile away, so nothing else on the workspace moves while the video plays.

- **`Unmaximise` put the window a bar's height too low.** `WINDOWPLACEMENT` is
  expressed in workspace coordinates, not screen ones, and `rcNormalPosition` was
  being handed a screen rectangle - the documented mistake whose documented symptom is
  a window that creeps down the display. Measured with Taj on the top edge, a window
  asked to restore to `y=400` arrived at `y=434`. The damage was bounded, because the
  `SetWindowPos` that follows corrected both the position and the stored rectangle, so
  what was left was a single frame of the window drawn too low - which is precisely
  the visible jump this call carries a rectangle in order to avoid. Invisible on a
  desktop with nothing docked at the top or the left, which is why it survived.

- **Taj registered an appbar callback and never listened to it.** The shell had been
  telling the bar that the taskbar had moved, been resized or been hidden
  (`ABN_POSCHANGED`), and that a full-screen application had opened or closed
  (`ABN_FULLSCREENAPP`), into a window procedure with no case for either. The bar now
  re-asserts its reservation on the first, so the work area Shubbak tiles into cannot
  drift away from where the bar actually is, and drops to the bottom of the z-order
  for the second, as the taskbar does.

- **A config that would not parse silently replaced your bar and your palette with
  stock ones.** On reload, `TajConfigLoader` answers an unparseable file with
  `CreateDefault()` and Dalil's loader answered with plain defaults — correct at
  startup, where there is nothing to keep, and wrong on a reload, where there is. A
  stray brace mid-edit therefore swapped a carefully built bar for the generic one and
  reset the palette's colours, size, prefixes and actions, with nothing anywhere to
  connect the change to the keystroke that caused it. Both now keep what they are
  running, which is what the window manager has always done with the rest of the same
  file and for the reason it gives: *a typo must not leave a running desktop with no
  keybindings*.

- **Config diagnostics went to a console that does not exist.** All three processes
  wrote them to `Console.Error`, and none of the three asks for a console on the path
  that matters — `shubbak-wm` only with `--foreground`, Taj and Dalil only for
  `--help` and `--version`. Started at logon, or from `startup-command`, every one of
  them formatted the line, the column, the caret and the hint and dropped the lot on
  the floor. So the headline promise — *"instead of failing silently: you press the
  key, nothing happens, and you go hunting"* — was true only of
  `shubbak check-config`, which you have to decide to run. They now go to each
  process's own log as well, through one shared helper so the three cannot drift apart
  again, which is exactly how this arose: the bar's reporting was written, the
  daemon's was written, the palette's never was, and nobody noticed that neither of
  the first two reached a log.

- **Taj kept no log unless the config asked for one**, so a file that could not be
  parsed — which yields defaults, and a default with no log path — left the bar with
  nowhere at all to say why. That is the one case where being able to say anything
  matters, and it was the one case with no log. It now defaults beside the window
  manager's, as Dalil always has; the asymmetry was not a decision.

### Added

- **`{{ config }}` on the bar.** Empty and invisible while the settings are readable,
  like `paused` and `suspended`; when it appears, the bar is running on what it had
  before rather than on what is in the file. Clickable, so it reloads. Each process
  reports only the part it reads, which keeps the decoupling: the daemon does not need
  to know what a bar profile is to say that one is wrong.

- **`>config` in the palette**, listing what is wrong with the palette's own section —
  severity and code as badges so `error` narrows the list, the hint on the row rather
  than a level down, and `Ctrl+C` yielding a `path:line:col` an editor can jump to. The
  row is absent when there is nothing wrong, because a row that promises problems and
  lists none teaches you to ignore it.

## [0.9.2-validation]

### Added

- **`shubbak check-config` validates the `dalil` section.** It never had. The section
  name was on the window manager's allow-list and its contents were on nobody's, so
  `dalil { with-icons #true }` was accepted in silence and did nothing for ever — in a
  project whose first selling point is a config file that tells you when you have made a
  mistake. Twelve diagnostics, all with a line, a column and a caret: unknown settings
  with a suggestion, numbers outside their range saying which one will be used instead,
  colours that are not colours, unknown placements, prefixes for modes that do not
  exist, prefixes longer than the one character the palette can match on, a prefix that
  takes a character another mode was using and leaves it with none, actions with no
  name, duplicated action names, actions with nothing in them, and any command inside
  an action that will not parse — reported in the parser's own words, at the line it is
  written on.

- **A workspace bound to a monitor by name is now an error rather than a shrug.**
  `monitor=` reads an integer, so `monitor="DISPLAY2"` was dropped on the floor and the
  workspace quietly took the primary. It now says so, and says that monitors are
  numbered from 0 in the order `shubbak query monitors` lists them.

### Fixed

- **A palette action whose name contains a space could not be run.** The command
  composer emits a row for any term containing a space — that is how a verb and its
  arguments become something to press Enter on — and when the text does not parse, that
  row explains why. Both kinds were inserted above the matches, so typing `code lay` put
  an unrunnable "unknown command 'code'" row on top of the "Code layout" action it was
  looking for, and Enter landed on the one that does nothing. Rows that can act still go
  above; rows that only explain now go below, and are still the only row when nothing
  else matched, which is the case they were written for.

## [0.9.1-palette]

Dalil stops being a viewer that can also send a few commands, and becomes a control
surface. Three things drove it: shortcuts that could not be typed on half the world's
keyboards, a safety setting whose default made almost every key in the palette inert,
and a list of actions that could do less to a window than a keybinding could.

### Added

- **`Ctrl+1` … `Ctrl+8` jump straight to a mode**, in the order the hint bar draws
  them. Prefixes are faster and cannot be typed at all on several layouts — on German
  and the international layouts `~` is a dead key, so it produces no character until
  the next keypress and the mode never changes — and Tab was seven presses from one
  end of the ring to the other. A digit is one keystroke and is in the same place on
  every keyboard in the world.

- **Prefixes are configurable**, under `dalil { prefixes { … } }`. The defaults are
  unchanged, so nobody who was happy has to do anything; what changed is that being
  unhappy is now fixable. An empty string gives a prefix up without losing the mode,
  which Tab and the jump key still reach.

- **Marking, and acting on several windows at once.** `Ctrl+Space` marks a window;
  `Ctrl+Enter` then offers the set — move them all to one workspace, float them, tile
  them, minimise them, close them. This is the thing a palette is genuinely for:
  moving six windows by keyboard is six rounds of find-it, focus-it, move-it, with the
  focus landing somewhere different after each one. It needs nothing new from the
  window manager, because the pipe has always accepted a newline-separated sequence.

- **"Write a rule for it"**, on every window row and at the top of every report. It
  composes the KDL that would match that window — class and process live, the
  executable's path commented out beside them, the title commented out under that —
  ready to read and paste. The `do { }` block is deliberately left empty: the same
  window one person wants floated is one somebody else wants ignored, and a generated
  rule that quietly did the wrong thing would be worse than none, because it would
  look right. This was the step the flagship feature always left as an exercise.

- **"Move it to…"**, which did not exist. The palette could bring a window *here* and
  could tag it onto a workspace, and could not send it to one — despite `move
  --workspace` being a verb the window manager has always accepted. Tagging was not a
  substitute: a tag is a membership that makes the window follow you about.

- **Named command sequences**, under `dalil { action "…" { … } }`. Keybindings are a
  scarce resource — there are only so many chords a person can hold, so anything done
  twice a week never gets bound and is then done by hand for ever. A palette row costs
  nothing to have and nothing to remember. They are validated against the real command
  parser at load time, so a mistake is reported on the row rather than swallowed.

- **`diagnose` from the palette.** The method has existed on the pipe since the daemon
  did and nothing but a shell had ever called it, which is exactly backwards: the
  report is wanted at the moment something has gone wrong on somebody's desktop, which
  is the moment they are looking at their desktop.

- **Application icons on window rows**, and a caret in the search box with
  `Left`/`Right`/`Home`/`End`/`Delete`. Commands mode is a text field somebody is
  composing in rather than a filter, and a typo in the middle of `resize --width +5%`
  used to cost the rest of the line.

- **A first row that answers the question before it is asked.** If the window you were
  just in is not being managed, the window list says so and offers the reason for one
  Enter. Only while nothing has been typed.

- **The window manager's state, said out loud.** The search box already reported
  paused tiling and a swallowing binding mode; it now also reports a suspended manager
  and one that cannot be reached at all. All four look exactly like a crash from the
  outside, and a dead daemon and a slow one used to produce an identical empty list
  with identical, confidently wrong, advice.

### Changed

- **`action-guard` became `confirm-destructive`.** The old setting turned every direct
  chord off at once, and its default left every chord in the palette inert except the
  one that took no action at all — while the action list went on printing those chords
  as badges beside the rows they belonged to. So the keys were advertised in the one
  place they were redundant and refused in the only place they would have saved
  anything. Now the two actions that cannot be undone ask first, by whichever route
  they were reached, and the eight that can just happen. Closing a window is stricter
  than it was; floating one is eight keystrokes cheaper. The old name is still read and
  still means "ask first", so no configuration breaks.

- **Tab walks a ring ordered by how often a mode is wanted**, and help is not in it.
  It used to walk the declaration order of a C# enum, which put monitors between
  scratchpad and inspect for no reason anybody chose and made help a stop on the way
  to somewhere else. Help has a prefix, a jump key and an Escape; it does not also need
  to be in everybody's way.

- **The window list is ranked by proximity below the eight most recent.** The list is
  used for two things that want opposite orderings — switching between the few windows
  you have been using wants recency absolutely, and finding one you have lost is a
  search where recency means nothing, because it has not been focused. So the top is
  left exactly as it was and only the tail is regrouped: same workspace, then same
  display, then anywhere.

- **A short list is drawn in a short window.** Two matches used to be two rows of text
  above ten rows of empty background.

- **Layout rows say what a layout does.** All eleven had "layout" in the dim column,
  next to a list whose heading already said it — a wasted column in the one mode where
  the row's own name is jargon.

- **Building the window list is 10× faster and allocates 18× less.** Every window row
  carried a dozen action records, two workspace-sized pickers and a composed rule,
  built on every refresh to answer a question about exactly one of them: measured on a
  desktop of 250 windows and 19 workspaces, 1.1 ms and 3.4 MiB per refresh, now 0.1 ms
  and 183 KiB. The list is only ever read for the selected row, so nothing was traded
  for it — it was work with no reader. Keystroke latency is unchanged at 0.07 ms.

### Fixed

- **`Ctrl+U` and `Ctrl+Backspace` no longer eject you from the mode.** Clearing the
  query drops the prefix, which silently moves the palette back to the window list —
  so a key documented as "clear what you typed" also changed what Enter was going to
  do, and the user had asked for neither. `Ctrl+Backspace` did the same on any
  single-word term.

- **Choosing a monitor with no workspace on it no longer types its name into the
  command box.** Completing was the fall-through for every command-less row in every
  mode but help, so `\\.\DISPLAY2` was offered as a verb somebody had started typing.

- **Badges no longer drop the ones that matter.** They were drawn from the end of the
  list backwards and the loop gave up when it ran out of width, which meant a window
  that was unmanaged, minimised, floating, sticky, tagged onto three workspaces and
  elevated showed the last three and silently omitted the first three — the only ones
  that explained why it was not where it had been left. An "also on" badge is now
  counted rather than listed past a couple of names, so a window tagged onto nineteen
  workspaces does not lose its own title.

- **The selection survives a refresh.** It was preserved by reference identity alone,
  and a refresh rebuilds every entry from the wire — so the selection went back to the
  top on every window event, which on a busy desktop is several times a second.

- **`Alt+Enter` works.** It has been printed as a badge on "Bring it here" since that
  action was written and was in no lookup table, so pressing it did nothing.

- **`?` lists the keys that exist.** `Ctrl+Shift+C` was implemented and documented in
  the README and the changelog and missing from the help; so were all five action
  chords, every one of which is printed as a badge in the list it belongs to. The
  comment above that list has always claimed a test held it to the implementation.
  There now is one.

- **Rows matched on their application are highlighted.** Finding a window by its
  process when the title says nothing about it — "Untitled document" — is most of what
  the dim half of a row is for, and such a row appeared with nothing underlined
  anywhere, which reads as the palette having matched it by accident.

- **A palette that is on screen and cannot be reached is put away.** A window that
  never became active is never told it has been deactivated, so close-on-blur cannot
  dismiss it and Escape never arrives. `PaletteWindow.IsStranded` was written for
  exactly this, with a careful explanation of why it mattered, and was referenced from
  nowhere at all.

- **Dalil survives the window manager shutting down.** It stopped with it, which is the
  wrong half of the relationship — it reconnects when the daemon comes back, so a
  restarted window manager had no palette until somebody noticed.

- **A query longer than 64 characters no longer risks a crash.** The matcher reports
  how many characters matched and writes a position only while the caller's span has
  room, so slicing by the count read past the end.

- **Copying a window row takes the class and process too**, which are the attributes
  somebody is copying it for. The title alone is the one guaranteed to be the wrong
  thing to match on.

- **`AltGr` still types.** Suppressing characters chorded with Ctrl would have broken
  `@`, `#`, `[` and `{` on exactly the European layouts this release is partly for,
  since AltGr *is* Ctrl+Alt.

- **`toggle-managed` is no longer marked destructive.** It is a toggle; pressing it
  twice leaves the desktop exactly as it was found.

## [0.9.0-inspection]

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
