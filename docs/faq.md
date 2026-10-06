# FAQ

**Do I need a separate hotkey daemon?**
No. Keybindings are built in. If you would rather drive it from AutoHotkey or
something else, the CLI and the pipe are right there.

**Do I need to install a bar separately?**
No. Taj ships with Shubbak and is configured in the same file. If you would rather
use something else, the event stream is public; see [Scripting](scripting.md).

**Can I write my own widget, watcher or plugin?**
Yes, and not as a plugin: as a program. There is no in-process plugin model - the
window manager is compiled to native code and loads nothing - and there does not need
to be, because the bar, the palette and the watcher are themselves just programs on
the pipe anything can connect to. Bind a key to a `signal` the window manager has
never heard of and subscribe to it; supply a fact as a `context` the config then acts
on; put a value on the bar with `signal <name> <value>`. Any language that can open a
named pipe and write a line of JSON will do, as will a shell script made of `shubbak`
commands, and for .NET the client is the `Shubbak.Ipc` package. [Extending](extending.md)
starts with seven hello-worlds, one per direction the pipe has, and ends with a
worked example, both ways.

**Can I run it alongside GlazeWM or komorebi?**
Please don't — two window managers fighting over the same windows goes exactly how
you would expect. Shubbak refuses to start if another copy of *itself* is already
running (`--replace` asks the incumbent to stand down cleanly first), but it cannot
detect other people's window managers.

**Something is not tiling. What do I do?**
`shubbak inspect`, click the window, and it tells you why. That is the whole feature.
[Troubleshooting](troubleshooting.md) lists the common verdicts.

**How do I get out if it all goes wrong?**
`shubbak restore` un-conceals anything stranded, and works with no window manager
running. `shubbak stop` takes everything down. Beyond that,
`shubbak diagnose -o report.md` gives you one file to attach to an issue.

**Where are my logs?**
Each process writes its own — `shubbak.log`, `taj.log`, `dalil.log`, `ayn.log` —
under `%LOCALAPPDATA%\Shubbak`, once asked to: the window manager writes a file only
with `--log-file` or a `logging { file }` section. A recent ring of log lines is kept
in memory regardless, which is what `shubbak diagnose` reads. Crashes write
`%LOCALAPPDATA%\Shubbak\crash-<timestamp>.md` on their own.

**Does it survive a reboot?**
Mostly. Windows go back to their workspaces, with their tags, their stickiness and
whether they were tiled, floating or minimised; each monitor shows the workspace it
was showing. What is not kept: the layout each workspace was in, the ratios of the
splits, and where a floating window sat - those come from the config's defaults and
the windows' own positions on the next start. Titles are hashed rather than stored,
because titles contain URLs and document names and that is your business. A logoff
or shutdown saves the session cleanly on the way out. For a snapshot you can restore
by name, layouts and all, see `arrangement --save`.

**Multi-monitor? High DPI?**
Both. Per-monitor DPI awareness (V2) in all three GUI processes, effective DPI read
per display, workspaces bindable to a monitor by name or by position, one bar per
monitor that comes and goes with it, and `move-workspace` to shove a whole workspace
to another screen — by direction or by name.

**Why does it need administrator approval to install?**
It does not, unless you take the default MSI. That one installs under `Program Files`,
which is what lets Shubbak move windows belonging to elevated programs (Task Manager,
anything run as administrator) without itself running elevated — Windows only grants
that ability, `uiAccess`, to a signed program installed there. `winget install
MoaidHathot.Shubbak --scope user`, Scoop and the zip need no elevation and work the
same, minus that one ability.

**Does it run on ARM64 Windows?**
Natively. Every release ships an ARM64 MSI and zip beside the x64 ones, and winget and
Scoop pick the one for your machine. The tests run on an ARM64 machine in CI; the
performance numbers in [Architecture](architecture.md#why-net) were taken on x64 and
have not been repeated there yet.

**Why .NET?**
It was measured rather than assumed; the numbers are in
[Architecture](architecture.md#why-net) and the full study in
[ADR 0001](adr/0001-language-choice.md).
