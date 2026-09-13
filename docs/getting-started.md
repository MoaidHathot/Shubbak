# Getting started

From nothing to a tiled desktop, in the order you will actually do it. When a step
raises a question this page does not answer, [Configuration](configuration.md) is
the reference, and [Taj](taj.md), [Dalil](dalil.md) and [Ayn](ayn.md) each have a
page of their own.

## 1. Install

**winget** (recommended)

```
winget install shubbak
```

`shubbak` is the package's moniker; `winget install MoaidHathot.Shubbak` is the same
thing spelled out. This installs the MSI: five executables under
`C:\Program Files\Shubbak`, that directory on the system `PATH`, an entry in Apps &
Features, and one Start Menu entry. It asks for administrator approval once, to
install, and Shubbak itself never runs elevated. Installing there is what lets Shubbak
move windows that belong to elevated programs - Task Manager, anything started as
administrator - without being elevated itself; Windows only grants that ability
(`uiAccess`) to a signed program under `Program Files`.

If you would rather not have anything under `Program Files`, or cannot approve an
elevation:

```
winget install shubbak --scope user
```

That is the portable build, under your own profile, with each executable on your
`PATH` through winget's links directory. Everything works the same except that
windows of elevated programs are detected and reported but cannot be moved. Run
`shubbak-wm` elevated if you need those tiled from a portable install.

**Scoop**

```
scoop bucket add shubbak https://github.com/MoaidHathot/Shubbak
scoop install shubbak
```

Portable, like `--scope user`.

**The zip or the MSI by hand**, from
[Releases](https://github.com/MoaidHathot/Shubbak/releases). Each comes for x64
(`-win-x64`) and for ARM64 (`-win-arm64`); winget and Scoop pick the right one for the
machine, and by hand you do. The zip unpacks anywhere and needs nothing else
installed; add its folder to your `PATH` if you want to type `shubbak` from any
terminal, and read [about the signature](#about-the-signature) below.

If you double-clicked the MSI, its last page has a checkbox - *Start Shubbak now, and
at every logon* - ticked by default. Leave it, click Finish, and you can skip to
[the keys](#4-the-keys): steps 2 and 3 have been done for you.

Otherwise, **open a new terminal**: the one you installed from still has the `PATH`
it started with.

## 2. Set it up

```
shubbak setup
```

One command, three things, each skipped if already done:

- Writes the starter config to `%USERPROFILE%\.config\shubbak\shubbak.kdl` (or under
  `XDG_CONFIG_HOME` if you have one). Never overwrites a file that is already there.
- Registers the window manager to start at logon: a per-user Run key, no
  administrator rights. `shubbak autostart status` shows it; `shubbak autostart
  disable` removes it.
- Starts the window manager. Within a second your windows arrange themselves, a bar
  appears along the top of each monitor, and a Shubbak icon appears in the tray.

`--no-autostart` and `--no-start` skip the second and third; `--config <path>` uses a
file of your own and records it in the Run key so it is used at every logon too.

The pieces are all available on their own - `shubbak config init`, `shubbak autostart
enable`, `shubbak-wm` - for doing one at a time.

**If you start `shubbak-wm` with no config at all** - the Start Menu shortcut, say -
it writes the starter for you, to the same place, and the tray icon tells you so. What
it does not do is register itself to start at logon; that is `shubbak autostart
enable` or `shubbak-wm --autostart`.

## 3. Watch it work, if you like

```
shubbak-wm --foreground
```

`--foreground` keeps it attached to your terminal so you can watch it work and stop
it with Ctrl+C. If something is wrong with the config, it says so here with a line, a
column and a caret under the problem - and keeps running on the last good
configuration, or on the defaults if there is none. (`shubbak setup` will have started
one already; `--foreground` on top of that is refused. `shubbak stop` first, or add
`--replace`.)

```
shubbak doctor
```

goes through the install as a checklist - the binaries, PATH, which config is in
effect and whether it parses, the Run key, which of the four programs are running, a
few things that commonly fight - and says how to fix each thing it finds.

## 4. The keys

Everything in the starter config is on Alt. This is the list at the top of the file,
and `?` in the palette shows the same list.

| Keys | What |
|---|---|
| `alt+shift+space` | The command palette (Dalil): every window, every command, `?` for every key |
| `alt+ctrl+space` | The palette, opened on its commands list |
| `alt` + `h` `j` `k` `l` | Focus left / down / up / right |
| `alt+shift` + `h` `j` `k` `l` | Move the focused window |
| `alt` + `u` `p` `i` `o` | Resize: narrower, wider, shorter, taller |
| `alt+shift+u` | Give every window in the container an equal share |
| `alt+r` | Resize mode: `h` `j` `k` `l` or the arrows until Escape |
| `alt` + `1`..`9` `0` | Go to workspace 1..9, 0 |
| `alt+shift` + `1`..`9` `0` | Send the window there, and follow it |
| `alt+shift+tab` | Back to the window you were just in |
| `alt+y` | Back to the workspace you were just on |
| `alt+w` | Cycle the layout |
| `alt+shift` + `s` `f` `g` `z` `v` | Layout: split, fibonacci, grid, monocle, master-left |
| `alt+v` | Flip the split direction for the next window |
| `alt+x` | Fullscreen within the work area; `alt+shift+x` the whole monitor |
| `alt+m` | Minimise |
| `alt+shift+m` | Float the window, or tile it again |
| `alt+shift+n` | Take on a window Shubbak passed over, or let one go |
| `alt+n` | Stash the window in the scratchpad; again to bring it back |
| `alt` + `a` `f` `d` `s` | Move the whole workspace to the monitor left / right / up / down |
| `alt+shift+q` | Close the window |
| `alt+shift+p` | Pause mode: Shubbak's keys off, everything else through. Same key to leave |
| `alt+shift+t` | Stop arranging windows; the keys still work |
| `alt+shift+o` | Suspend: release the keyboard entirely, for a game. Same key to resume |
| `alt+shift+r` | Reload the config; `alt+shift+w` redraw |
| `alt+shift+e` | Exit Shubbak: the window manager, the bar, the palette and the watcher |

Change any of them by editing the file. The palette is on `alt+shift+space` rather than
`alt+space` because Windows uses `alt+space` for the window menu and PowerToys Run
takes it by default; `shubbak doctor` warns if you bind it while PowerToys Run is up.

## 5. Make it yours

The config is one file for the window manager, the bar, the palette and the watcher.
Edit it, then:

```
shubbak check-config         validate it: every section, with carets under mistakes
shubbak wm-reload-config     apply it without restarting (alt+shift+r does the same)
```

Nothing watches the file behind your back, so a half-saved edit cannot take your
desktop with it. The fully annotated reference - every setting and why it exists - is
`shubbak.example.kdl`, installed beside the executables and
[in the repository](shubbak.example.kdl). It is the author's own config translated
into KDL, so treat it as something to read and borrow from rather than copy whole.

Three commands answer the questions that come up first:

```
shubbak inspect              click a window: every attribute, whether it will be tiled, and why not
shubbak monitors             a `monitor` definition for each display, ready to paste
shubbak status               running? paused? suspended? which config?
```

### The bar, the palette, the watcher

They are three separate programs - `taj`, `dalil`, `ayn` - started by the window
manager because the starter config says so:

```kdl
general {
    startup-command "taj"
    startup-command "dalil"
    startup-command "ayn"
}
```

Each reads its own section of the same file (`bar { }`, `dalil { }`, `ayn { }`). To
live without one, delete its `startup-command` line and its section. The palette is
opened by the `signal "palette"` binding, so if you remove Dalil, free that key too.

The bar shows the workspaces (dim when empty, blue when on another monitor, green when
it has the keyboard), the focused window's icon and title, the keyboard language, the
layout as a glyph, and the clock in your Windows accent colour. Only while they apply,
it also shows a red *suspended* pill, an amber *paused* pill, an orange *config* pill
(this file has a problem; click to reload), a red pill naming the active binding mode,
and a camera or microphone glyph while one is in use in a meeting - click the
microphone to mute or unmute. Each pill is clickable, because each describes a state
the keyboard may not be able to get you out of.

The palette lists every window, including ones Shubbak is not managing. `>` switches
to commands, where the starter's dozen actions live - *Go to...*, *Send it to...*,
*Layout...*, *Gaming*, *Reset everything* - and `?` lists every key. The very first
time it opens on a machine it opens on that list.

## 6. Upgrading

**winget**: `winget upgrade shubbak`. The installer asks the running
window manager, bar, palette and watcher to close, replaces the files, and starts the
window manager again, which starts the other three from your config. Your windows
come back where they were.

**Scoop**: `scoop update shubbak`. Scoop stops everything first (so it can replace the
files) but does not start it again: run `shubbak-wm` afterwards.

**By hand**:

```
shubbak stop                 stops all four and waits until they have gone
<replace the files>
shubbak-wm
```

`stop` exits non-zero if anything is still running after ten seconds, so a script can
tell.

## 7. Uninstalling

```
shubbak autostart disable
winget uninstall shubbak      # or: scoop uninstall shubbak
```

The package removes what it installed. Two things are yours and stay, so nothing you
wrote is lost by an uninstall that turns out to be a reinstall:

- `%USERPROFILE%\.config\shubbak\` - your config.
- `%LOCALAPPDATA%\Shubbak\` - the saved session and arrangements, the logs, and any
  crash reports.

Delete both if you want no trace. If you uninstall without `autostart disable`, the
Run key points at a program that is gone; Windows ignores it silently, and
`shubbak autostart disable` from any later install clears it.

## Where things are

Shubbak looks for the config in this order, first match wins:

1. `--config <path>`
2. `$SHUBBAK_CONFIG` - a file, or a directory containing `shubbak.kdl`
3. `$XDG_CONFIG_HOME/shubbak/shubbak.kdl`
4. each entry of `$XDG_CONFIG_DIRS`
5. `%USERPROFILE%\.config\shubbak\shubbak.kdl`
6. `%APPDATA%\shubbak\shubbak.kdl`

`shubbak config-path` says which one is in effect, or lists everywhere it looked.

Logs go to `%LOCALAPPDATA%\Shubbak\` - `shubbak.log`, `taj.log`, `dalil.log`,
`ayn.log` - once you ask for them: the window manager writes a file only with
`shubbak-wm --log-file` or a `logging { file }` section in the config. A recent ring
of log lines is kept in memory regardless, which is what makes
`shubbak diagnose -o report.md` useful after the fact: one Markdown file with your
environment, your config, the live window tree and that log, ready to attach to an
issue. Crashes write `%LOCALAPPDATA%\Shubbak\crash-<timestamp>.md` on their own.

## About the signature

Every executable and the MSI are signed. The certificate is recent, so SmartScreen
may still show its "unrecognised app" warning when you double-click something you
downloaded by hand, until enough machines have run it; "More info" then "Run
anyway" gets past it. Installs through winget are not affected, because winget checks
the download's hash against the manifest and Windows does not mark what it extracts.

## If it goes wrong

[Troubleshooting](troubleshooting.md) is organised by symptom. Two commands are worth
knowing before you need them:

```
shubbak restore              bring back windows a killed window manager left concealed
shubbak diagnose -o report.md
```

Both work with no window manager running.
