<div align="center">

<img src="docs/assets/shubbak-wm.png" width="120" alt="Shubbak" />

# Shubbak

**A tiling window manager for Windows, batteries included.**

[![Build](https://img.shields.io/github/actions/workflow/status/MoaidHathot/Shubbak/build.yml?branch=main&logo=github&label=build)](https://github.com/MoaidHathot/Shubbak/actions/workflows/build.yml)
[![Release](https://img.shields.io/github/v/release/MoaidHathot/Shubbak?logo=github&label=release)](https://github.com/MoaidHathot/Shubbak/releases)
[![Downloads](https://img.shields.io/github/downloads/MoaidHathot/Shubbak/total?logo=github)](https://github.com/MoaidHathot/Shubbak/releases)
[![Licence](https://img.shields.io/badge/licence-MIT-blue)](LICENSE)

[Getting started](docs/getting-started.md) ·
[Configuration](docs/configuration.md) ·
[Taj](docs/taj.md) ·
[Dalil](docs/dalil.md) ·
[Ayn](docs/ayn.md) ·
[Scripting](docs/scripting.md) ·
[FAQ](docs/faq.md)

</div>

Shubbak (شبّاك, "window") arranges your windows so you can stop dragging them around.
It is driven from the keyboard, it animates, and one config file covers the whole
desktop: the window manager, the status bar, the command palette, and the watcher
that tells them about the rest of the machine, starting with whether your camera is
on. There is no second project to install before it looks like yours.

<!-- A screenshot or a short clip belongs here. -->

## What's in the box

Five small programs, compiled to native code. About 25 MB together, nothing to
install first.

- **shubbak-wm**, the window manager. Eleven layouts that belong to containers rather
  than workspaces, so they nest. Workspaces pinned to monitors, tags, floating,
  fullscreen, a scratchpad, and animations at your display's refresh rate.
- **Taj** (تاج, *crown*), the bar. One per monitor. Three widget primitives and a
  template language do the work of a widget catalogue, and any program that prints to
  stdout can drive a widget.
- **Dalil** (دليل, *guide*), the command palette. Every window, command, workspace and
  layout under one search box. Mark several windows and act on all of them at once.
  Ask it why a window is not tiling, and it writes the rule for you.
- **Ayn** (عين, *eye*), the watcher. The window manager's eyes on the rest of the
  machine: what is not a window is watched here and arrives as a context, so the
  window manager never has to learn it. Today that is the camera and the microphone,
  in use or muted, and your config reacts: a mute button on the bar, the close key
  disarmed during a call.
- **shubbak**, the command line. Everything the keys do, plus `inspect`, `diagnose`,
  `restore` and `stop`.

## Why another one

I used the tiling window managers available for Windows for years. Shubbak keeps what
I liked about them and changes what kept getting in my way:

- **The config file talks back.** A mistake is reported at load time with a line, a
  column, a caret and a hint, for every section of the file. Mistakes that look fine
  are caught too: a regex that can never match, a rule that matches every window, a
  key bound twice. A file that will not parse never replaces the one that is running.
- **It tells you why a window is not tiling.** `shubbak inspect`, click the window,
  and you get every attribute, the verdict, the reason, and which of your rules
  matched.
- **Nothing gets stranded.** Windows on other workspaces are cloaked, not hidden, so a
  crash or a kill leaves them recoverable. `shubbak restore` brings them back with
  nothing else running.
- **Contexts.** A presentation, a docked monitor, a call with the microphone open:
  each is a named condition that layers changes on the config while it holds, and lets
  go the moment it does not.
- **Monitors by what they are**, not by the number Windows gave them today. A
  workspace bound to a display leaves when it is unplugged and comes back with it.
- **Scriptable.** One named pipe, JSON, 30 event topics, and a `signal` verb the window
  manager carries without reading. The bar and the palette are ordinary clients of it,
  and so can anything you write.

## Install

With winget:

```
winget install shubbak
```

That installs the MSI under `Program Files`, which is what lets Shubbak tile windows
that belong to elevated programs (Task Manager, anything run as administrator) without
running elevated itself. For a portable copy under your own profile, with no
administrator prompt:

```
winget install shubbak --scope user
```

With Scoop:

```
scoop bucket add shubbak https://github.com/MoaidHathot/Shubbak
scoop install shubbak
```

Or take the MSI or the zip from [Releases](https://github.com/MoaidHathot/Shubbak/releases),
for x64 or for ARM64. The zip unpacks anywhere. Everything is signed. (`shubbak` is
the winget moniker; `MoaidHathot.Shubbak` is the full id, if you prefer.)

## First steps

Open a new terminal, then:

```
shubbak setup
```

That writes a starter config if you have none, registers the window manager to start
at logon, and starts it. Your windows tile, a bar appears along the top of each
monitor, and a Shubbak icon sits in the tray. If you double-clicked the MSI instead,
the last page of the installer has a checkbox that does the same thing.

The starter config is a working desktop rather than a skeleton: borders, animation, ten
workspaces, a bar with every indicator that matters, a palette with a dozen actions,
and the watcher for the camera and microphone. Everything is on Alt:

| Keys | What |
|---|---|
| `alt+shift+space` | The command palette. `?` inside it lists every key |
| `alt` + `h` `j` `k` `l` | Focus left, down, up, right |
| `alt+shift` + `h` `j` `k` `l` | Move the window |
| `alt` + `1`…`9` `0` | Go to a workspace; with `shift`, send the window there |
| `alt+w` | Cycle the layout |
| `alt+shift+m` | Float the window, or tile it again |
| `alt+shift+q` | Close the window |
| `alt+shift+p` | Pause Shubbak's keys, so a game or a video gets them |
| `alt+shift+r` | Reload the config |

`shubbak doctor` checks the install when something seems off. Editing the config,
upgrading, uninstalling and where the logs are: see
[Getting started](docs/getting-started.md).

## A taste of the config

One KDL file, shared by all five programs. This is the shape of it:

```kdl
general {
    default-layout "splith"
    startup-command "taj"
    startup-command "dalil"
}

gaps {
    inner 6
    outer { top 4; right 4; bottom 4; left 4 }
}

workspaces {
    workspace "1"
    workspace "2"
    workspace "3"
}

keybindings {
    bind "alt+h" { focus --direction left }
    bind "alt+l" { focus --direction right }
    bind "alt+shift+space" { signal "palette" }

    for-each "workspace" {
        bind "alt+{name}"       { focus --workspace "{name}" }
        bind "alt+shift+{name}" { move --workspace "{name}" --focus }
    }
}

rules {
    rule "browsers live on 2" {
        match { process regex=r"msedge|chrome|firefox" }
        do { move --workspace "2" }
    }
}

bar {
    source "clock" kind="time" format="HH:mm" interval=500

    profile "default" {
        zone "left"  justify="start" { workspaces hide-empty=#true }
        zone "right" justify="end"   { text template="{{ clock }}" }
    }
}
```

[Configuration](docs/configuration.md) is the reference.
[`shubbak.example.kdl`](docs/shubbak.example.kdl) is a complete, commented, real
config to read and borrow from.

## Documentation

| Page | What it covers |
|---|---|
| [Getting started](docs/getting-started.md) | Install, first run, the keys, upgrading, uninstalling |
| [Configuration](docs/configuration.md) | The file: sections, rules, layouts, monitors, contexts, commands |
| [Taj](docs/taj.md) | The bar: sources, templates, profiles |
| [Dalil](docs/dalil.md) | The palette: modes, actions, questions |
| [Ayn](docs/ayn.md) | The watcher: the rest of the machine as contexts - camera, microphone, screen, speaker, power, theme |
| [Scripting](docs/scripting.md) | The pipe, the events, signals, security |
| [Troubleshooting](docs/troubleshooting.md) | Organised by symptom |
| [Diagnostics](docs/diagnostics.md) | Every SHB, TAJ, DAL and AYN code `check-config` can print, and what each means |
| [FAQ](docs/faq.md) | The questions that come up first |
| [Architecture](docs/architecture.md) | How it is built, why .NET, the layout of the repo |

## Status

Shubbak is what I run all day, but it is young and has not been through many hands
yet. Expect rough edges. When you hit one, `shubbak diagnose -o report.md` writes a
single file with everything I need; open an issue and attach it.
[CHANGELOG.md](CHANGELOG.md) says what changed between releases.

## Building

```
dotnet build
dotnet test
```

[Architecture](docs/architecture.md) explains the layout and the two quirks worth
knowing before you change anything; [RELEASING.md](RELEASING.md) is how a release is
cut.

## Thanks

To [GlazeWM](https://github.com/glzr-io/glazewm), where I learned what a good Windows
tiling window manager feels like to live in, and whose config I used as the
translation target for Shubbak's example file. To
[komorebi](https://github.com/LGUG2Z/komorebi), for showing that a window manager can
be a queryable, scriptable, subscribable service rather than a black box. And to
AwesomeWM and i3, for the ideas everyone on Windows is still catching up with.

## Licence

MIT. See [LICENSE](LICENSE).
