# Shubbak

A tiling window manager for Windows, with an animation engine, a status bar, and a
configuration language that tells you when you have made a mistake.

Named for شبّاك — "window". The bar is **Taj** (تاج, crown).

## Status

Feature complete and working. Not yet battle-tested — see
[Troubleshooting](docs/troubleshooting.md) if something misbehaves, and
`shubbak diagnose` if you want to report it.

| Phase | | |
| --- | --- | --- |
| P0 | De-risking spike | done |
| P1 | Core, platform layer, config, daemon, IPC/CLI | done |
| P2 | Layout strategies | done |
| P3 | Animation engine | done |
| P4 | Taj — the bar | done |
| P5 | Tags, scratchpad, session persistence | done |

**923 test methods**, ~700 ms. Everything except the platform layer and the renderer runs
headless.

## Why .NET

Not the obvious choice for a window manager, so it was measured rather than assumed.
[ADR 0001](docs/adr/0001-language-choice.md) has the numbers; the summary:

- **Keyboard hook latency** — p99.9 of **0.8 µs** against Windows' 300 ms unhook
  threshold, measured under ~1,300 forced blocking Gen2 collections. The hazard is
  real but does not materialise, because the callback never allocates.
- **Animation** — zero dropped frames at 144 Hz, with **managed code accounting for
  2.5–5.3% of frame time** and Win32 taking the rest. The unbatched control group
  dropped 33–42% of frames with *identical* managed code, so `DeferWindowPos`
  batching — not language choice — is what determines whether motion looks smooth.
- **Distribution** — single NativeAOT executables, no runtime prerequisite, zero
  trim/AOT warnings.

## Building and running

```
dotnet build
dotnet test
dotnet publish src/Shubbak.Wm -c Release -r win-x64 -p:PublishAot=true
```

```
shubbak-wm --config path/to/shubbak.kdl      # the window manager
taj                                          # the bar (or launch it from startup-command)
```

Config is searched for in this order, first match wins:

1. `--config <path>`
2. `$SHUBBAK_CONFIG` — a file, or a directory containing `shubbak.kdl`
3. `$XDG_CONFIG_HOME/shubbak/shubbak.kdl`
4. each entry of `$XDG_CONFIG_DIRS`
5. `%USERPROFILE%\.config\shubbak\shubbak.kdl`
6. `%APPDATA%\shubbak\shubbak.kdl`

XDG is honoured on Windows. The specification is nominally Unix, but people who keep
dotfiles in a repository and symlink them per machine set `XDG_CONFIG_HOME` on
Windows too, and every tool that ignores it needs its own bespoke variable instead.

```
shubbak config-path      # which file is in effect, or everywhere that was searched
```

The window manager, the CLI and the bar share one resolver, so they cannot disagree
about which file is loaded.

Run elevated to manage windows belonging to elevated processes. Without it those
windows are detected and reported, but cannot be moved.

## Configuration

One KDL file for both the window manager and the bar, with diagnostics that point at
the problem:

```
shubbak.kdl:8:20: error SHB0305: Unknown command 'focuss'.
  8 |     bind "alt+h" { focuss --direction left }
    |                    ^^^^^^^^^^^^^^^^^^^^^^^^
  hint: Did you mean 'focus'?
```

[`docs/shubbak.example.kdl`](docs/shubbak.example.kdl) is a complete config,
translated from a real GlazeWM setup.

**`for-each` generates repetitive bindings.** 19 workspaces × 2 bindings is 40
hand-written lines in GlazeWM; here it is six, and it cannot drift out of sync with
the workspace list:

```kdl
for-each "workspace" {
    bind "alt+{name}"       { focus --workspace "{name}" }
    bind "alt+shift+{name}" { move --workspace "{name}"; focus --workspace "{name}" }
}
```

**Silent failures are reported.** The source config contained a regex wrapped in
slashes, which are matched literally — so the rule had never once fired, and nothing
said so. Shubbak warns and prints the corrected pattern. Likewise duplicate
bindings, unknown commands, and rules that would match every window.

## Diagnostics

```
shubbak inspect              # why is this window not being tiled?
shubbak diagnose -o r.md     # one file to attach to a bug report
shubbak log-level trace      # raise logging on the running WM, no restart
shubbak check-config         # validate, with carets
```

`inspect` prints every matchable attribute of a window, whether Shubbak will tile it
**and why not if it will not**, and which rules matched. Neither GlazeWM nor
komorebi can answer that question.

See [Troubleshooting](docs/troubleshooting.md).

## Features

**Layouts** — `splith` `splitv` `fibonacci` `fibonacci-v` `fibonacci-mirrored`
`master-left` `master-right` `master-top` `master-bottom` `grid` `monocle`.

Layout is a property of a **container**, not a workspace, so a fibonacci region can
sit inside a columns region with no special case. `layout --cycle` walks a short list
ordered so each entry looks obviously different from the last.

**Tags** — the AwesomeWM model: a window can belong to several workspaces and appears
in whichever you are viewing. A Windows window has one position on one monitor, so
membership means the window *relocates* to whichever tagged workspace you last
activated — the same thing AwesomeWM does.

**Scratchpad** — named slots, so several windows can be stashed and summoned
independently.

**Session persistence** — a restart or reboot puts windows back on their workspaces.
Titles are hashed rather than stored.

**Animation** — per-event durations and cubic-bezier curves. Re-targeting blends from
the current position, so rapid layout changes never make windows jump backwards.

**Mouse** — drag a tiled window onto the middle of another to swap them, or near an
edge to insert beside it. Drag a border to resize, which writes back to the tree''s
ratios rather than being undone by the next layout pass.

**Recoverable concealment** — windows on inactive workspaces are cloaked rather than
hidden. A cloaked window still reports as visible to Win32, so if Shubbak exits,
crashes or is killed, the next run adopts it and un-cloaks it. Hiding, which is what
this originally did, is unrecoverable: the filter rejects invisible windows, so they
stay stranded with their process still running.

## Taj

Four layers, each independently replaceable:

```
L1 transport    Shubbak's IPC
L2 sources      reactive values: WM events, timers, external processes
L3 widget tree  renderer-agnostic model + flex layout
L4 renderer     ITajRenderer — currently GDI
```

L2 and L3 contain no drawing code and are covered by tests that run with no window on
screen. Swapping the renderer means implementing one interface.

**Adding a widget needs no renderer code**, and usually no code at all:

| Tier | Effort |
|---|---|
| KDL template bound to a source | a few lines of config |
| External program writing lines to stdout | any language |
| `IWidget` implementation | only for custom drawing |

Widgets re-render only when a source they use actually changes, so an idle desktop
does not repaint. Clicking a workspace sends the same command a keybinding would.

The bar consumes the window manager's event stream and never inspects windows itself.
That is the structural fix for Zebar's stale titles: `EVENT_OBJECT_NAMECHANGE` fires
on browser tab switches, twice as often as focus changes, so a bar listening only for
focus misses two thirds of title updates.

## Architecture

```
src/
  Shubbak.Core/     tree, layouts, animation, state machine, logging  — zero Win32
  Shubbak.Native/   Win32: hooks, window control, monitors
  Shubbak.Config/   KDL parser, schema, diagnostics
  Shubbak.Ipc/      protocol, named-pipe server and client
  Shubbak.Wm/       the daemon
  Shubbak.Cli/      shubbak
  Taj.Core/         widget tree, flex layout, sources          — no drawing code
  Taj/              bar host + GDI renderer
tests/              923 test methods
```

`Shubbak.Core` contains no Win32 at all. That is the highest-leverage decision in the
project: the entire behavioural surface — tree, layout, focus, animation, tags,
sessions, the state machine — is deterministically testable headlessly, in
milliseconds, with no window manager running. It is also what contains the risk: if a
hot path ever did fail in managed code, it could be replaced behind the
`Shubbak.Native` boundary without touching any of the logic.

## Licence

See [LICENSE](LICENSE).
