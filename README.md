# Shubbak

A tiling window manager for Windows, with an animation engine and a
diagnostics-first configuration language.

Named for شبّاك — "window".

## Status

Working and usable. The window manager tiles, animates, and is fully scriptable.
The companion bar (Taj) is not built yet.

| Phase | | |
| --- | --- | --- |
| P0 | De-risking spike | done |
| P1 | Core, platform layer, config, daemon, IPC/CLI | done |
| P2 | Layout strategies | done |
| P3 | Animation engine | done |
| P4 | Taj (the bar) | not started |
| P5 | Tags, scratchpads, session persistence | not started |

## Why .NET

Not the obvious choice for a window manager, so it was measured rather than
assumed. [ADR 0001](docs/adr/0001-language-choice.md) has the numbers; the summary:

- **Keyboard hook latency** — p99.9 of **0.8 µs** against Windows' 300 ms unhook
  threshold, measured under ~1300 forced blocking Gen2 collections. The hazard is
  real but does not materialise, because the callback never allocates.
- **Animation** — zero dropped frames at 144 Hz, with **managed code accounting for
  2.5–5.3% of frame time** and Win32 taking the rest. The unbatched control group
  dropped 33–42% of frames with *identical* managed code, so `DeferWindowPos`
  batching — not language choice — is what determines whether motion looks smooth.
- **Distribution** — 3.2 MB single NativeAOT executable, no runtime prerequisite,
  zero trim/AOT warnings.

## Building

```
dotnet build
dotnet test
dotnet publish src/Shubbak.Wm -c Release -r win-x64 -p:PublishAot=true
```

## Running

```
shubbak-wm --config path/to/shubbak.kdl
shubbak-wm --check-config        # validate without touching any window
```

Config is searched for at `--config`, then `$SHUBBAK_CONFIG`, then
`%USERPROFILE%\.config\shubbak\shubbak.kdl`.

Run elevated to manage windows belonging to elevated processes. Without it those
windows are detected and reported, but cannot be moved.

## Configuration

KDL, with diagnostics that point at the problem:

```
shubbak.kdl:8:20: error SHB0305: Unknown command 'focuss'.
  8 |     bind "alt+h" { focuss --direction left }
    |                    ^^^^^^^^^^^^^^^^^^^^^^^^
  hint: Did you mean 'focus'?
```

[`docs/shubbak.example.kdl`](docs/shubbak.example.kdl) is a complete config,
translated from a real GlazeWM setup.

Two things it does that the original could not:

**`for-each` generates repetitive bindings.** 19 workspaces × 2 bindings each is
40 hand-written lines in GlazeWM; here it is six, and it cannot drift out of sync
with the workspace list.

```kdl
for-each "workspace" {
    bind "alt+{name}"       { focus --workspace "{name}" }
    bind "alt+shift+{name}" { move --workspace "{name}"; focus --workspace "{name}" }
}
```

**Silent failures are reported.** The source config contained a regex wrapped in
slashes, which are matched literally — so the rule had never once fired, and
nothing said so. Shubbak warns and prints the corrected pattern.

## Diagnostics

```
shubbak inspect
```

Prints every matchable attribute of a window, whether Shubbak will tile it **and
why not if it will not**, and which rules and app definitions matched. This is the
answer to "why is this window not being tiled?", which neither GlazeWM nor komorebi
can give.

```
shubbak check-config      # validate, with carets
shubbak query state       # full state as JSON
shubbak sub               # tail the event stream
```

## Layouts

`splith` `splitv` `fibonacci` `fibonacci-v` `fibonacci-mirrored` `master-left`
`master-right` `master-top` `master-bottom` `grid` `monocle`

Layout is a property of a **container**, not of a workspace, so a fibonacci region
can sit inside a columns region with no special case. `layout --cycle` walks a short
list ordered so each entry looks obviously different from the last.

## Architecture

```
src/
  Shubbak.Core/     tree, layouts, animation, state machine   — zero Win32
  Shubbak.Native/   Win32: hooks, window control, monitors
  Shubbak.Config/   KDL parser, schema, diagnostics
  Shubbak.Ipc/      protocol, named-pipe server and client
  Shubbak.Wm/       the daemon
  Shubbak.Cli/      shubbak
tests/              240 tests, ~110 ms
```

`Shubbak.Core` contains no Win32 at all. That is the highest-leverage decision in
the project: the entire behavioural surface — tree, layout, focus, animation, the
state machine — is deterministically testable headlessly, in milliseconds, with no
window manager running. It is also what contains the risk: if a hot path ever did
fail in managed code, it could be replaced behind the `Shubbak.Native` boundary
without touching any of the logic.

## Licence

See [LICENSE](LICENSE).
