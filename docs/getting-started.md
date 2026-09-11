# Getting started

From nothing to a tiled desktop, in the order you will actually do it. When a step
raises a question this page does not answer, [Configuration](configuration.md) is
the reference, and [Taj](taj.md), [Dalil](dalil.md) and [Ayn](ayn.md) each have a
page of their own.

## 1. Install

**winget** (recommended)

```
winget install MoaidHathot.Shubbak
```

This installs the MSI: five executables under `C:\Program Files\Shubbak`, that
directory on the system `PATH`, an entry in Apps & Features, and one Start Menu entry.
It asks for administrator approval once, to install, and Shubbak itself never runs
elevated. Installing there is what lets Shubbak move windows that belong to elevated
programs - Task Manager, anything started as administrator - without being elevated
itself; Windows only grants that ability (`uiAccess`) to a signed program under
`Program Files`.

If you would rather not have anything under `Program Files`, or cannot approve an
elevation:

```
winget install MoaidHathot.Shubbak --scope user
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
[Releases](https://github.com/MoaidHathot/Shubbak/releases). The zip unpacks anywhere
and needs nothing else installed; add its folder to your `PATH` if you want to type
`shubbak` from any terminal, and read [about the signature](#about-the-signature)
below.

Then **open a new terminal**: the one you installed from still has the `PATH` it
started with.

## 2. Write a config

```
shubbak config init
```

This writes `%USERPROFILE%\.config\shubbak\shubbak.kdl` (or under `XDG_CONFIG_HOME` if
you have one) and refuses to overwrite a file that is already there. It is short - a
hundred and fifty lines, most of them comments - and it turns everything on: the
keybindings below, five workspaces, the bar, the command palette and the camera and
microphone watcher.

Without a config the window manager runs on defaults, which tile every window and
bind **no keys at all**. It will tell you so; this step is not optional.

## 3. Start it

```
shubbak-wm --foreground
```

`--foreground` keeps it attached to your terminal so you can watch it work and stop
it with Ctrl+C. Within a second you should see your windows arrange themselves, a
bar along the top of each monitor, and a Shubbak icon in the system tray. Then try
the keys.

If something is wrong with the config, it says so here with a line, a column and a
caret under the problem - and keeps running on the last good configuration, or on
the defaults if there is none.

When you are happy with it:

```
shubbak autostart enable
```

registers the window manager to start at logon (a per-user Run key; nothing needs
administrator rights). `shubbak autostart status` tells you whether that is set and
whether it still points at a copy that exists. Now start it once more, without
`--foreground`, and close the terminal:

```
shubbak-wm
```

## 4. The keys

Everything in the starter config is on Alt. This is the list at the top of the file.

| Keys | What |
|---|---|
| `alt` + `h` `j` `k` `l` | Focus left / down / up / right |
| `alt+shift` + `h` `j` `k` `l` | Move the focused window |
| `alt` + `u` `p` `i` `o` | Resize: narrower, wider, shorter, taller |
| `alt` + `1`..`5` | Go to workspace 1..5 |
| `alt+shift` + `1`..`5` | Send the window there, and follow it |
| `alt+space` | The command palette (Dalil) |
| `alt+shift+space` | Cycle the layout |
| `alt+m` | Monocle (one window fills the workspace) |
| `alt+shift+m` | Float the window, or tile it again |
| `alt+shift+q` | Close the window |
| `alt+shift+r` | Reload the config |
| `alt+shift+e` | Exit Shubbak |

Change any of them by editing the file. `alt+space` is also PowerToys Run's default;
if you use both, move one.

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

The bar shows the workspaces, the focused window's title, a clock, and - only while
they apply - a red *suspended* pill, an amber *paused* pill, an orange *config* pill
(this file has a problem; click to reload), and a camera or microphone glyph while
one is in use. Each pill is clickable, because each describes a state the keyboard
may not be able to get you out of.

## 6. Upgrading

**winget**: `winget upgrade MoaidHathot.Shubbak`. The installer asks the running
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
winget uninstall MoaidHathot.Shubbak      # or: scoop uninstall shubbak
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
