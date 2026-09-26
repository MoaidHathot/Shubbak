# Contributing

Shubbak is one person's daily window manager, and it has not been through many hands.
Contributions are welcome, and this page is what will save you a round trip.

## Before writing code

Open an issue first for anything larger than a fix, and say what you were trying to do
rather than what you plan to build. Half the ideas that arrive turn out to be "here is
how Shubbak already spells that", and the other half go better for a conversation about
where the seam is. The [architecture page](docs/architecture.md) is the map: five
programs, one pipe, and the rule that `Shubbak.Core` contains no Win32 - which is the
highest-leverage decision in the project and the one most worth not breaking.

The `ideas/` directory holds analyses of what is known to be imperfect and why. If your
change is in there, read the entry; the reasoning that survives is often more useful
than the finding.

## Building and testing

```
dotnet build
dotnet test
```

That is the whole of it for most changes. Two things to know:

- **`Shubbak.Native.Tests` refuses to run while `shubbak-wm` is running.** It creates
  real windows, which a running window manager would manage, move and conceal, so any
  result would measure the window manager rather than the code under test. Stop it
  first (`shubbak stop`, or the tray) and start it again after. Every other test
  project runs anywhere.
- **The published binary is not the built one.** `dotnet build` gives you a managed
  apphost; what ships is NativeAOT. To try a change as a user would:

  ```
  dotnet publish src/Taj -c Release -r win-x64 -p:PublishAot=true -o dist
  ```

  Then `shubbak taj-exit` and start `dist\taj.exe`. A change that adds a type the
  binary did not have before - a JSON document reader, a process API - can add tens of
  kilobytes; it is worth measuring, and the changelog says when it happened.

## What CI checks that you can check first

The build workflow fails on things a local build does not, and each is one command:

| Check | Command |
|---|---|
| Every `[Fact]`/`[Theory]` is counted in `docs/architecture.md` | `pwsh -File tools/check-test-count.ps1 -Fix` |
| `docs/diagnostics.md` matches the codes in the source | `pwsh -File tools/list-diagnostics.ps1` and commit the result |
| Every KDL snippet in the docs loads without a diagnostic | `pwsh -File tools/check-doc-snippets.ps1` (needs a Release build of the CLI) |
| The example config loads clean | `shubbak check-config docs/shubbak.example.kdl` |
| Version numbers agree | `pwsh -File tools/check-release-consistency.ps1` |

Line endings are LF everywhere, enforced by `.gitattributes`; a file with CRLF in it
will show as changed in every line.

## What a change looks like here

- **A new setting is validated where it is read.** The config file talks back: a
  mistake is a diagnostic with a code, a line, a caret and a hint, and a setting the
  loader does not know is a warning that names the nearest one it does. A setting that
  parses and does nothing is the failure this project most dislikes; two of them lived
  in the example config for a year before anyone noticed.
- **A new diagnostic gets the next code in its series** (`SHB`, `TAJ`, `DAL`, `AYN`)
  and a regenerated catalogue.
- **A new command is in the catalogue** (`CommandCatalogue.cs`), so the palette
  completes it and `shubbak --help` lists it.
- **Tests are pure where they can be.** `Shubbak.Core`, `Taj.Core`, `Dalil.Core` and
  `Ayn.Core` are headless by design and their tests run in milliseconds; a decision
  that lives in a host (`Taj`, `Dalil`, `Ayn`, `Shubbak.Wm`) is worth pulling into the
  core so it can be tested there. Tests against a real pipe use an in-process
  `IpcServer` on an isolated pipe name; the pattern is in `tests/Ayn.Tests`.
- **Nothing waits without a timeout.** A test that waits on a pipe, a process or a
  window bounds the wait and says what it was waiting for when it gives up.
- **Comments say why, not what.** The code says what. A comment that survives is one
  that explains a decision somebody would otherwise reverse - the measured cost of the
  alternative, the bug it fixed, the reason the obvious approach was tried and
  abandoned.
- **The changelog entry is written for the person who hits the change**, not for the
  person who made it: what they can do now, what they will see, and what it cost or
  saved where that was measured.

## Performance

The keyboard hook and the animation frame are the two hot paths, and
[`ideas/analysis3.md`](ideas/analysis3.md) is the current account of both. Nothing
goes into `KeyboardSource.Callback`. Anything on the tick path is measured with
`shubbak diagnose`, whose performance section reports tick and frame percentiles and
allocated bytes per tick; the number to keep at zero is the frame's. A change that
adds a subscriber to an IPC topic costs the daemon a lock per event on its own thread,
which is why the bar subscribes to exactly the topics it reads and no more.

## Releases

[RELEASING.md](RELEASING.md). You will not need it for a contribution; it is here so
that the process is written down.
