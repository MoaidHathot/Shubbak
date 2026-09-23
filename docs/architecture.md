# Architecture

How Shubbak is put together, why it is written in .NET, and how to build it. For how
a release is cut, see [RELEASING.md](../RELEASING.md).

## Five programs, one pipe

| Program | What it is |
|---|---|
| `shubbak-wm` | The window manager. Hooks, layout, animation, the tray icon, the IPC server. A GUI-subsystem process with no window of its own. |
| `shubbak` | The command line. Everything the keys do, plus `inspect`, `diagnose`, `restore`, `stop`, `autostart`, `config init`. |
| `taj` | The bar. A client of the window manager's event stream; never inspects windows itself. |
| `dalil` | The command palette. Opened by a signal, so the window manager does not know it exists. |
| `ayn` | The watcher. The window manager's eyes on the rest of the machine: facts that are not about windows, supplied as contexts over a held connection. Today, the camera and the microphone. |

The three companions are clients of the window manager and share what that takes.
`Shubbak.Companion` holds the start of each program (help, version, the single-instance
lock, the log file, the DPI opt-in - in that order, because the lock has to come before
the log or a duplicate truncates the running copy's file), the window class and window
procedure every companion window derives from, the message loop that waits rather than
polls, and the subscription that reconnects for as long as the process lives. Each used
to carry its own copy of all four, and no two copies agreed.

Everything talks over one named pipe, `shubbak-v2-<SID>`, newline-delimited JSON,
with the protocol version in the name so a new client and an old daemon fail to find
each other rather than misunderstand each other. See [Scripting](scripting.md).

## Layout of the repo

```
src/
  Shubbak.Core/     tree, layouts, animation, state machine, logging  — zero Win32
  Shubbak.Native/   Win32: hooks, window control, monitors, tray, DPI
  Shubbak.Config/   KDL parser, schema, diagnostics
  Shubbak.Ipc/      protocol, named-pipe server and client
  Shubbak.Companion/ bootstrap, window base, message loop, reconnecting pump — shared by taj, dalil, ayn
  Shubbak.Ui/       visual tree, flex layout, IRenderer            — no drawing code
  Shubbak.Ui.Gdi/   the GDI renderer
  Shubbak.Wm/       the daemon
  Shubbak.Cli/      shubbak, and autostart registration
  Taj.Core/         bar model, widgets, sources
  Taj/              bar host
  Dalil.Core/       fuzzy matching, palette model                     — no Win32
  Dalil/            the palette
  Ayn.Core/         the watcher's decisions: debounce, leases, config  — no Win32
  Ayn/              the watcher: the rest of the machine, as contexts
tests/              2373 test methods across 14 projects
docs/               this, and the annotated example config
bucket/             the Scoop manifest, where Scoop looks for it
packaging/winget/   the winget manifests: one package, the MSI and the portable zip
packaging/msi/      the installer (WiX), which carries the uiAccess build
tools/              release scripts: build, sign-check, version consistency, prepare
```

`Shubbak.Core` contains no Win32 at all, and that is the highest-leverage decision in
the project. It is what makes the logic testable headlessly in milliseconds, and it is
also the insurance policy: if a hot path ever did fail in managed code, it could be
replaced behind the `Shubbak.Native` boundary without touching any of the logic.

## Tests

**2373 test methods**, around a second to run. Everything except the platform layer and
the renderer runs headless, so the entire behavioural surface — tree, layout, focus,
animation, tags, sessions, the state machine, the config diagnostics, the palette's
matching, the bar's model — is testable in milliseconds with no window manager
running.

`Shubbak.Native.Tests` is the exception: it creates real windows, and refuses to run
while a window manager is managing them. `shubbak stop`, run them, `shubbak-wm`. The
count above is checked by CI against the tree, because it was wrong on every occasion
somebody looked.

## Why .NET

Not the obvious choice for a window manager, so it was measured rather than assumed.
[ADR 0001](adr/0001-language-choice.md) has all the numbers; the summary:

- **Keyboard hook latency** — p99.9 of **0.8 µs** against Windows' 300 ms unhook
  threshold, measured under ~1,300 forced blocking Gen2 collections. The hazard is
  real; it does not materialise, because the callback never allocates.
- **Animation** — zero dropped frames at 144 Hz, with **managed code accounting for
  2.5–5.3% of frame time** and Win32 taking the rest. The unbatched control group
  dropped 33–42% of frames with *identical* managed code — so `DeferWindowPos`
  batching, not language choice, is what decides whether motion looks smooth.
- **Distribution** — five single-file NativeAOT executables, ~25 MB total, under
  11 MB zipped, no runtime prerequisite, zero trim/AOT warnings.

The measurements were made on x64. The code has no architecture-specific paths and
ships for ARM64 as well; the numbers there have not been taken yet.

## Building

```
dotnet build
dotnet test
```

Publishing is what CI does on every push, so it is worth knowing it works:

```
dotnet publish src/Shubbak.Wm -c Release -r win-x64 -p:PublishAot=true
```

The whole release — the five executables, the uiAccess build of the window manager,
the zip, the MSI and the filled package manifests — comes out of one script, which is
also what the release workflow runs:

```
.\tools\build-release.ps1
```

### Two quirks worth knowing

**`shubbak-wm` is a GUI-subsystem binary** despite having no window. A
console-subsystem process gets given a console window when it is started by something
that has no console of its own — which at logon means a black rectangle on your
desktop forever. `--foreground` is how you get a console back when you want one, and
failures that stop it starting open one regardless, because a daemon that dies
silently is indistinguishable from one that never launched.

**There are two builds of the window manager.** The portable one asks for nothing in
its manifest. The one in the MSI asks for `uiAccess`, which is what lets it move
windows belonging to elevated processes — and which Windows honours only for a signed
binary under `Program Files`, refusing to start it anywhere else. The two build into
separate directories (`-p:ShubbakUiAccess=true`), because the manifest is baked into
the apphost and a shared directory once let one leak into the other.

### Architectures

Shubbak ships for x64 and ARM64, from one tree. The code has no architecture-specific
paths; what differs is compiled. `Shubbak.Native` is the one project that must be
built for a concrete architecture — CsWin32 generates some Win32 structures
differently per platform — and it, the executables and the test hosts all take their
runtime identifier from `ShubbakRid` in `Directory.Build.props`, x64 unless told
otherwise:

```
dotnet build -p:ShubbakRid=win-arm64
dotnet test  -p:ShubbakRid=win-arm64
```

is the whole solution, natively, on an ARM64 machine, and is what CI runs there.
Publishing needs the C++ build tools for the target architecture (the linker), which
is why the release is built on an x64 runner that has both and the ARM64 binaries are
then run on an ARM64 runner. `tools/build-release.ps1 -Rids win-x64` gives a complete
build on a machine without the ARM64 tools.
