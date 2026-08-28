<div align="center">

<img src="docs/assets/shubbak-wm.png" width="120" alt="Shubbak" />

# Shubbak

**A tiling window manager for Windows with animations, a status bar, a command
palette, and a config file that tells you when you've made a mistake.**

[![Build](https://img.shields.io/github/actions/workflow/status/MoaidHathot/Shubbak/build.yml?branch=main&logo=github&label=build)](https://github.com/MoaidHathot/Shubbak/actions/workflows/build.yml)
[![Release](https://img.shields.io/github/v/release/MoaidHathot/Shubbak?logo=github&label=release)](https://github.com/MoaidHathot/Shubbak/releases)
[![Downloads](https://img.shields.io/github/downloads/MoaidHathot/Shubbak/total?logo=github)](https://github.com/MoaidHathot/Shubbak/releases)
[![Licence](https://img.shields.io/badge/licence-MIT-blue)](LICENSE)

[Install](#install) •
[Quick start](#quick-start) •
[What makes it different](#what-makes-shubbak-different) •
[Configuration](#configuration) •
[Taj](#taj--the-bar) •
[Dalil](#dalil--the-command-palette) •
[Scripting](#scripting-it) •
[FAQ](#faq)

</div>

## Hello

Shubbak (شبّاك, *"window"*) arranges your windows for you so you can stop dragging
them around. It's keyboard-driven, it animates, and it ships with everything you
need in the box, no second app to install before your desktop looks like your own.


## Demos

Videos are on the way, I'm recording them now and they'll land here shortly:
tiling and layouts, the animation engine at full speed, Taj, and Dalil.

<!--
  Coming soon:
  - Layouts & tiling walkthrough
  - Animations at 144 Hz
  - Taj (the bar) and Dalil (the palette)
  - "Why isn't this window tiling?" — shubbak inspect
-->

Meanwhile, [`docs/shubbak.example.kdl`](docs/shubbak.example.kdl) is a complete,
heavily commented, real-world config you can read end to end.

## What makes Shubbak different

I created Shubbak after being a heavy user of window tiling managers on Windows, aiming to keep the features I liked and improve the aspects that didn’t work well for me in other tiling managers for Windows, in all aspects: architecture, implementation, and features.

### The config file talks back

Instead of failing silently: you press the key, nothing happens, and
you go hunting. Shubbak reads the whole file up front and tells you exactly where
you went wrong, with a line, a column and a caret:

```
shubbak.kdl:8:20: error SHB0305: Unknown command 'focuss'.
  8 |     bind "alt+h" { focuss --direction left }
    |                    ^^^^^^^^^^^^^^^^^^^^^^^^
  hint: Did you mean 'focus'?
```

It also warns about the mistakes that *look* fine. A real one from my own config:
a regex wrapped in `/slashes/`, which Windows matches literally, so the rule had
never once fired, and nothing had ever said so. Shubbak warns and prints the
corrected pattern. Same for duplicate bindings, unknown settings, unknown sections,
and rules that would match every window on your desktop.

You could also manually validate the configuration before reloading
```
shubbak check-config       # validate before you reload
```

### `for-each` — stop copy-pasting keybindings

I run 19 workspaces. That's 40 near-identical lines in other window tiling managers, and every one of
them is a chance to typo a number. Here it's six lines that can't drift out of sync
with the workspace list:

```kdl
keybindings {
    for-each "workspace" {
        bind "alt+{name}"       { focus --workspace "{name}" }
        bind "alt+shift+{name}" { move --workspace "{name}"; focus --workspace "{name}" }
    }
}
```

### "Why isn't this window tiling?"

Every tiling WM on Windows passes over some windows. Almost none of them will tell
you *which* ones, or *why*. Shubbak will:

```
shubbak inspect            # click a window; get the full story in 3 seconds
shubbak inspect --all      # every top-level window, with a verdict for each
```

You get every matchable attribute of the window, whether Shubbak will manage it,
**the specific reason if it won't**, and which of your rules matched. There are 16
distinct reasons a window gets skipped and each one explains itself in plain
English. Copy the attributes straight into a rule and you're done.

`inspect --all` and `restore` both run entirely locally and independently, so they still work when
the window manager isn't running at all.

### Nothing gets stranded

Windows on inactive workspaces are **cloaked**, not hidden. A cloaked window still
reports as visible to Win32, so if Shubbak crashes, is killed, or you pull the
plug, the next run finds those windows and brings them straight back.

Hiding (which is what this used to do, and what a lot of tools do) is a one-way
door: the window filter rejects invisible windows, so they stay stranded with their
process still running and nothing on screen to click.

And if it ever does go wrong, there's a fire escape that doesn't need the daemon:

```
shubbak restore --dry-run  # show me what you'd bring back
shubbak restore            # bring it back
```

### One report to hand to a bug tracker

```
shubbak diagnose -o report.md
```

One Markdown file: your environment, your config, the live window tree, and the
recent log, including a ring buffer that's kept even at the default log level, so
the report is still useful *after* the weird thing happened. You can also raise the
log level on the running daemon without restarting it:

```
shubbak log-level trace
```

### Suspend is different from pause

Two different things you'll actually want:

- **`wm-toggle-pause`** — stop rearranging windows, keep the keyboard. For when you
  want to drag something around manually for a minute.
- **`wm-suspend`** — let go of the keyboard *entirely*, drop the hooks, stop doing
  periodic work. For when a game or a remote session wants every key you press.

Resuming from a full suspend uses a real Windows hotkey rather than a keyboard hook,
so a suspended Shubbak costs you nothing per keystroke. The bar and the tray icon
both tell you which state you're in, and both are clickable, because "suspended"
and "crashed" look identical if the only way back is the keyboard you just gave up.

### Layout belongs to the container, not the workspace

A fibonacci region can sit inside a columns region with no special case, because
layout is a property of a container. Eleven of them:

`splith` `splitv` `fibonacci` `fibonacci-v` `fibonacci-mirrored` `master-left`
`master-right` `master-top` `master-bottom` `grid` `monocle`

`layout --cycle` walks a short list, deliberately ordered so each one looks
obviously different from the last.

### Tags, the AwesomeWM way

A window can belong to several workspaces and show up in whichever one you're
looking at. Windows only lets a window be in one place at a time, so membership
means the window *relocates* to whichever tagged workspace you activated last,
exactly what AwesomeWM does.

### Animations that don't fight you

Per-event durations and cubic-bezier curves. The important bit: re-targeting blends
from the window's *current* position, so hammering a layout key never makes windows
jump backwards or stutter. Frame rate follows your fastest display by default
(`fps "auto"`) and is re-read when monitors come and go.

### Mouse gestures that stick

Drag a tiled window onto the middle of another to **swap** them, or near an edge to
**insert** beside it. Drag a border to resize, and the resize is written back into
the tree's ratios, so the next layout pass respects it instead of undoing it.

### It's four small executables and no runtime

`shubbak-wm`, `shubbak`, `taj`, `dalil`: around 19 MB total, under 9 MB zipped,
compiled ahead-of-time with NativeAOT. Nothing to install first.

## Install

**winget**

```
winget install MoaidHathot.Shubbak
```

**Scoop**

```
scoop bucket add shubbak https://github.com/MoaidHathot/Shubbak
scoop install shubbak
```

**Or just grab the zip** from [Releases](https://github.com/MoaidHathot/Shubbak/releases)
and unpack it anywhere. It's four self-contained executables with no prerequisites.

### A heads-up before you start

**This build isn't code signed yet.** Two consequences worth knowing:

1. SmartScreen will warn you the first time you run it.
2. Windows belonging to **elevated** processes — Task Manager, anything running as
   administrator — are detected and reported, but can't be moved. Run `shubbak-wm`
   elevated if you need them tiled.

Doing it properly without elevation needs `uiAccess`, and Windows only grants that
to a signed binary installed under `Program Files`. That's the next release.

## Quick start

```
shubbak config init          # write a starter config you can actually read
shubbak autostart enable     # start the window manager at logon
shubbak-wm --foreground      # or just run it right now, attached to this terminal
```

`shubbak autostart status` tells you whether it's registered, and warns you if it
points at a copy you've since moved or deleted.

Then poke at it:

```
shubbak status               # running? paused? suspended?
shubbak layouts              # what layouts exist
shubbak config-path          # which config file is actually in effect
shubbak query workspaces     # JSON, for scripts
```

There's a system tray icon too: suspend/resume, stop arranging windows, reload the
config, open the config folder, exit.

## Configuration

One KDL file drives the window manager, the bar and the palette. Shubbak looks for
it in this order, first match wins:

1. `--config <path>`
2. `$SHUBBAK_CONFIG` — a file, or a directory containing `shubbak.kdl`
3. `$XDG_CONFIG_HOME/shubbak/shubbak.kdl`
4. each entry of `$XDG_CONFIG_DIRS`
5. `%USERPROFILE%\.config\shubbak\shubbak.kdl`
6. `%APPDATA%\shubbak\shubbak.kdl`

Yes, XDG on Windows. The spec is nominally Unix, but if you keep your dotfiles in a
repo and symlink them per machine, you already have `XDG_CONFIG_HOME` set — and
every tool that ignores it makes you learn one more bespoke environment variable.

The window manager, the CLI and the bar all share one resolver, so they can't
disagree about which file is loaded.

### The sections

| Section | What goes in it |
|---|---|
| `general` | Behaviour: initial window state, default layout, hide method, startup commands |
| `gaps` | `inner`, and `outer` per side |
| `window-effects` | Focused / unfocused / floating border colours |
| `animation` | `enabled`, `fps`, `minimum-distance`, and per-event duration + curve |
| `logging` | `level`, `file`, `console` |
| `workspaces` | Names, display names, monitor binding, starting layout |
| `keybindings` | `bind`, and `for-each` |
| `binding-modes` | Modal keymaps, i3-style |
| `app` | Reusable named matchers you reference from rules |
| `rules` | Match windows, run commands |
| `bar` | Taj — sources, profiles, zones, widgets |
| `dalil` | The command palette's appearance and behaviour |

Both `colour` and `color` are accepted, everywhere. Settings can be written as a
child node or a property, whichever reads better to you.

### A taste of it

```kdl
general {
    initial-window-state "tiling"
    default-layout "splith"
    toggle-workspace-on-refocus #true
}

gaps {
    inner 6
    outer { top 26; right 4; bottom 4; left 4 }
}

animation {
    enabled #true
    fps "auto"
    window-move { duration 140; curve "ease-out-expo" }
}

keybindings {
    bind "alt+h" { focus --direction left }
    bind "alt+l" { focus --direction right }
    bind "alt+v" { toggle-tiling-direction }
    bind "alt+f" { toggle-floating }
    bind "alt+shift+q" { close }
}
```

### Window rules

Match on `title`, `class`, `process` or `path`, with five operators each — `equals`,
`regex`, `starts-with`, `ends-with`, `contains` (symbolic forms `=` `~=` `^=` `$=`
`*=` work too). Everything is case-insensitive. Prefix a matcher with `!` to negate.

```kdl
app "browser-picture-in-picture" {
    // Raw strings need no backslash escaping, which matters for regexes.
    title regex=r"[Pp]icture.in.[Pp]icture"
    class regex=r"Chrome_WidgetWin_1|MozillaDialogClass"
}

rules {
    rule "float the PiP window" {
        match { app "browser-picture-in-picture" }
        do { float }
    }

    rule "browsers live on 2" {
        match { process regex=r"msedge|chrome|firefox" }
        do { move --workspace "2" }
    }
}
```

Rules can fire `on="manage"` (the default), `on="title-change"` or `on="focus"`.
The `do { }` block takes **any** command — it's the same parser your keybindings
use, so there's no second vocabulary to learn. `ignore` and `manage` are the two
that only make sense here: `ignore` tells Shubbak to leave a window alone, `manage`
tells it to take on a window the built-in filter passed over.

Reloading is explicit — `wm-reload-config`, from a keybinding, the CLI or the tray.
Nothing watches your file behind your back, so a half-saved config can't take your
desktop with it.

### Commands

33 verbs, all usable from a keybinding, a rule, the CLI, the palette, or over IPC.

**Focus & movement** — `focus` `focus-window` `focus-recent-window` `move`
`move-workspace` `resize` `equalise` `split` `toggle-tiling-direction`

**Layout & state** — `layout` `float` `tile` `toggle-floating` `toggle-fullscreen`
`toggle-minimized` `close`

**Workspaces & stashing** — `tag` `sticky` `scratchpad`

**Management** — `ignore` `manage` `toggle-managed`

**The window manager itself** — `wm-enable-binding-mode` `wm-disable-binding-mode`
`wm-toggle-pause` `wm-suspend` `wm-resume` `wm-toggle-suspend` `wm-reload-config`
`wm-redraw` `wm-exit`

**Escape hatches** — `shell-exec` `signal`

Anything the CLI doesn't recognise as its own subcommand is forwarded straight to
the daemon, so `shubbak focus --direction left` just works.

## Taj — the bar

<img src="docs/assets/taj.png" width="72" align="right" alt="" />

**Taj** (تاج, *"crown"*) is the status bar, and it's already in the box. One bar per
monitor, each reserving its own strip, each able to show a different profile.

```kdl
bar {
    source "clock" kind="time" format="ddd d MMM HH:mm" interval=500
    source "keyboard" kind="keyboard" interval=250

    profile "default" {
        height 34
        background "#1e1e2e"
        foreground "#cdd6f4"

        zone "left" justify="start" gap=4 {
            workspaces hide-empty=#true active-background="#8dbcff"
        }
        zone "centre" justify="center" grow=1 {
            text template="{{ window.title | truncate:90 }}"
        }
        zone "right" justify="end" gap=12 {
            text template="{{ layout | icon }}" colour="#7f849c"
            text template="{{ clock }}" colour="#8dbcff"
        }
    }
}
```

**Adding a widget usually needs no code at all.** There are three widget primitives
— `text`, `workspaces`, `spacer` — and the breadth comes from templates, filters and
sources rather than from a catalogue you have to wait for someone to grow:

| What you want | What it costs |
|---|---|
| A new value on the bar | A few lines of KDL |
| Something Taj has never heard of | Any program that writes lines to stdout |
| Genuinely custom drawing | One `IWidget` implementation |

Templates get filters — `truncate:N` `upper` `lower` `trim` `default:X` `pad:N`
`replace:from,to` `icon` `state-icon` — and a `when { }` block for conditional
styling, so "colour the keyboard indicator red when I'm in the wrong language" is a
line, not a plugin.

Zones are flex containers. Profiles can `extend` each other, so a slim
"presentation" variant costs five lines instead of a duplicate. A `rule` picks the
profile at runtime by workspace or monitor, and switching is a pointer swap.

**Why it doesn't show stale titles.** The bar consumes the window manager's event
stream and never inspects windows itself. `EVENT_OBJECT_NAMECHANGE` fires on things
like browser tab switches — about twice as often as focus changes — so a bar
listening only for focus quietly misses two thirds of title updates. Taj can't,
because it isn't listening to Windows at all.

Widgets re-render only when a source they use actually changes, so an idle desktop
doesn't repaint. Clicking a workspace sends the same command a keybinding would.

### Under the hood

```
L1 transport    Shubbak's IPC
L2 sources      reactive values: WM events, timers, external processes
L3 widget tree  renderer-agnostic model + flex layout
L4 renderer     ITajRenderer — currently GDI
```

L2 and L3 contain no drawing code and are covered by tests that run with no window
on screen. Swapping the renderer means implementing one interface.

## Dalil — the command palette

<img src="docs/assets/dalil.png" width="72" align="right" alt="" />

**Dalil** (دليل, *"guide"*) is a fuzzy-search palette for your whole desktop. Bind a
key to `signal "palette"` and it appears.

Eight modes, each with a prefix:

| Prefix | Mode | |
|---|---|---|
| *(none)* | Windows | Every window on the desktop, managed or not, ranked by recency |
| `>` | Commands | Every verb the WM accepts |
| `#` | Workspaces | With window count, layout and monitor |
| `~` | Layouts | Marks the one you're already in |
| `%` | Monitors | Size, DPI, and what each is showing |
| `$` | Scratchpad | Everything you've stashed, by slot |
| `!` | Inspect | Every window Shubbak is **not** managing, and why not |
| `?` | Help | The palette's keys — **and your own keybindings** |

Three things I'm particularly happy with:

**Type a command and it's parsed for real.** Whatever you type becomes a top-ranked
row, run through the *same* parser your config file uses. So a bad argument gives
you the same message it would at load time, right there, before you press Enter.

**`shubbak inspect`, without leaving the palette.** Press **Ctrl+Shift+I** on any
window and you get the full report — attributes, verdict, which rules matched, which
app definitions missed and on which matcher. Any line too long to fit opens in full
with Enter, and Escape or Backspace steps back out. **Ctrl+C** copies the selected
line; **Ctrl+Shift+C** copies the whole report, which is the version that belongs in
a bug report.

**The `!` mode answers "what is being skipped?"** — the palette's version of
`shubbak inspect --all`. Every window Shubbak passed over, each one saying why on the
row itself, with the ones you can do something about (excluded by a rule, not adopted
yet) sorted to the top. Enter inspects; Ctrl+Enter still reaches "Manage it".

**Every row has actions** (Ctrl+Enter): go to it, bring it here, float/tile,
minimise/restore, make it sticky, edit its tags, close it, start or stop managing it,
and inspect it.

Rows carry badges so you can see at a glance what you're looking at: `unmanaged`,
`minimised`, `cloaked`, `floating`, `fullscreen`, `sticky`, `elevated`, `stashed`,
`also on <workspace>`. Unmanaged windows also carry the reason in the dim text, so
you don't have to open anything to find out why. The search box tells you when tiling
is paused or a binding mode is eating your keys.

Dalil is opened by a **signal**, not by a hard-wired command — which means Shubbak
doesn't know Dalil exists. That's the same extension point anything else can use.

## Scripting it

Everything the CLI and the palette do goes over one named pipe, `shubbak-v1-<SID>`,
scoped per user, with the protocol version in the name. Newline-delimited JSON.

```
shubbak query state          # the whole window manager, as JSON
shubbak query windows        # or: all-windows, workspaces, monitors,
                             #     focused, layouts, commands, bindings
shubbak sub                  # tail every event
shubbak sub window.focused,workspace.activated
```

**23 event topics** you can subscribe to:

```
window.managed       window.unmanaged      window.focused      window.title_changed
window.state_changed window.tags_changed   window.moved
workspace.activated  workspace.created     workspace.destroyed workspace.moved
layout.changed       container.resized
monitor.added        monitor.removed       monitor.changed
binding_mode.changed command.rejected      config.reloaded
wm.paused            wm.suspended          wm.shutdown         wm.resync
signal
```

Subscribe to a topic that doesn't exist and you get told, along with the list of
ones that do. `wm.resync` tells you your backlog was dropped; `wm.shutdown` tells
you the daemon is leaving on purpose.

`signal "name" [args...]` publishes a name Shubbak doesn't interpret at all. That's
how Dalil exists without the window manager knowing about it, and it's how you'd
wire in your own tools.

**On security:** `shell-exec` is refused over the pipe by default. A window manager
isn't an execution service, and the pipe is scoped to your *account*, not to your
*integrity level* — so leaving it open would mean any process running as you could
ask an elevated Shubbak to launch something elevated. Flip
`allow-shell-exec-over-ipc` if you want it; keybindings and startup commands can
always use it either way.

## FAQ

**Do I need a separate hotkey daemon?**
No. Keybindings are built in. If you'd rather drive it from AutoHotkey or something
else, the CLI and the pipe are right there.

**Do I need to install a bar separately?**
No. Taj ships with Shubbak and is configured in the same file. If you'd rather use
something else, the event stream is public.

**Can I run it alongside GlazeWM or komorebi?**
Please don't — two window managers fighting over the same windows goes exactly how
you'd expect. Shubbak refuses to start if another copy of *itself* is already
running (`--replace` asks the incumbent to stand down cleanly first), but it can't
detect other people's window managers.

**Something's not tiling. What do I do?**
`shubbak inspect`, click the window, and it'll tell you why. That's the whole
feature.

**How do I get out if it all goes wrong?**
`shubbak restore` un-conceals anything stranded, and works with no daemon running.
Beyond that, `shubbak diagnose -o report.md` gives you one file to attach to an
issue.

**Where are my logs?**
Each process writes its own — `shubbak.log`, `taj.log`, `dalil.log`. Crashes are
written automatically to `%LOCALAPPDATA%\Shubbak\crash-<timestamp>.md`.

**Does it survive a reboot?**
Yes. Windows go back to their workspaces. Titles are hashed rather than stored,
because titles contain URLs and document names and that's your business.

**Multi-monitor? High DPI?**
Both. Per-monitor DPI awareness (V2) in all three GUI processes, effective DPI read
per display, workspaces bindable to a monitor, one bar per monitor, and
`move-workspace` to shove a whole workspace to another screen.

## Status

Released as **0.9.0** — feature complete and working, but not yet battle-tested. If
something misbehaves, [Troubleshooting](docs/troubleshooting.md) is organised by
symptom, and `shubbak diagnose` is the fastest way to tell me about it.

| Phase | | |
| --- | --- | --- |
| P0 | De-risking spike | done |
| P1 | Core, platform layer, config, daemon, IPC/CLI | done |
| P2 | Layout strategies | done |
| P3 | Animation engine | done |
| P4 | Taj — the bar | done |
| P5 | Tags, scratchpad, session persistence | done |

**1358 test methods**, around 700 ms to run. Everything except the platform layer
and the renderer runs headless, so the entire behavioural surface — tree, layout,
focus, animation, tags, sessions, the state machine — is testable in milliseconds
with no window manager running.

**Known limitations:** not code signed (see [above](#a-heads-up-before-you-start)),
and x64 only for now.

## Why .NET

Not the obvious choice for a window manager, so it was measured rather than assumed.
[ADR 0001](docs/adr/0001-language-choice.md) has all the numbers; the summary:

- **Keyboard hook latency** — p99.9 of **0.8 µs** against Windows' 300 ms unhook
  threshold, measured under ~1,300 forced blocking Gen2 collections. The hazard is
  real; it doesn't materialise, because the callback never allocates.
- **Animation** — zero dropped frames at 144 Hz, with **managed code accounting for
  2.5–5.3% of frame time** and Win32 taking the rest. The unbatched control group
  dropped 33–42% of frames with *identical* managed code — so `DeferWindowPos`
  batching, not language choice, is what decides whether motion looks smooth.
- **Distribution** — four single-file NativeAOT executables, ~19 MB total, under
  9 MB zipped, no runtime prerequisite, zero trim/AOT warnings.

## Building

```
dotnet build
dotnet test
```

Publishing is what CI does on every push, so it's worth knowing it works:

```
dotnet publish src/Shubbak.Wm -c Release -r win-x64 -p:PublishAot=true
```

One quirk worth explaining: `shubbak-wm` is a **GUI-subsystem** binary despite
having no window. A console-subsystem process gets given a console window when
it's started by something that has no console of its own — which at logon means a
black rectangle on your desktop forever. `--foreground` is how you get a console
back when you want one, and failures that stop it starting open one regardless,
because a daemon that dies silently is indistinguishable from one that never
launched.

### Layout of the repo

```
src/
  Shubbak.Core/     tree, layouts, animation, state machine, logging  — zero Win32
  Shubbak.Native/   Win32: hooks, window control, monitors, tray, DPI
  Shubbak.Config/   KDL parser, schema, diagnostics
  Shubbak.Ipc/      protocol, named-pipe server and client
  Shubbak.Ui/       visual tree, flex layout, IRenderer            — no drawing code
  Shubbak.Ui.Gdi/   the GDI renderer
  Shubbak.Wm/       the daemon
  Shubbak.Cli/      shubbak, and autostart registration
  Taj.Core/         bar model, widgets, sources
  Taj/              bar host
  Dalil.Core/       fuzzy matching, palette model                     — no Win32
  Dalil/            the palette
tests/              1358 test methods across 9 projects
bucket/             the Scoop manifest, where Scoop looks for it
packaging/winget/   the winget manifests
```

`Shubbak.Core` contains no Win32 at all, and that's the highest-leverage decision in
the project. It's what makes the logic testable headlessly in milliseconds, and it's
also the insurance policy: if a hot path ever did fail in managed code, it could be
replaced behind the `Shubbak.Native` boundary without touching any of the logic.

See [RELEASING.md](RELEASING.md) for how a release is cut, and
[CHANGELOG.md](CHANGELOG.md) for what changed.

## Thanks

To **[GlazeWM](https://github.com/glzr-io/glazewm)**, which is where I learned what
a good Windows tiling WM feels like to live in — and whose config I used as the
translation target for Shubbak's own example file. To
**[komorebi](https://github.com/LGUG2Z/komorebi)**, for showing that a window
manager can be a queryable, scriptable, subscribable service rather than a black
box. And to **AwesomeWM** and **i3**, for the ideas everyone on Windows is still
catching up with.

## Licence

MIT. See [LICENSE](LICENSE).
