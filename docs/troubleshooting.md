# Troubleshooting

Shubbak is built to answer "why did it do that?" rather than leave you guessing.
This page is the order to try things in.

## The one command

```
shubbak diagnose -o report.md
```

Produces a single file containing the environment, your config as loaded, the live
window tree drawn as indented text, and the last few thousand log entries.

**Recent log entries are kept in memory even at the default log level**, so this is
usually worth running *after* something has already gone wrong. You do not need to
have enabled logging in advance.

Attach that file to a bug report.

## "This window isn't being tiled"

The most common question, and the one Shubbak can answer directly:

```
shubbak inspect
```

Click the window, wait three seconds. You get every matchable attribute, the
manageability verdict **with its reason**, and which of your rules and app
definitions matched — including, for each app that did not match, the specific
matcher that failed.

Common verdicts and what they mean:

| Reason | What is happening |
|---|---|
| `window has WS_EX_TOOLWINDOW and not WS_EX_APPWINDOW` | The app declares itself a palette or utility window. Usually correct to skip. |
| `window is cloaked by the shell` | A suspended UWP app. It reports as visible but is not composited; tiling it would reserve space for nothing. |
| `window is owned by another window` | A dialog. Its parent gets the tile — otherwise a save prompt would shrink the document behind it. |
| `window has no title` | Splash screens and message-only helpers. |
| `window belongs to an elevated process` | Run Shubbak elevated to manage it. |

If the verdict is `manageable: yes` but a rule matched, the rule is why.

## "It is not loading my config"

```
shubbak config-path
```

Prints the file in effect and how it was found. If nothing was found it lists **every
location it looked in**, which is usually enough on its own — "no config file" is a
useless thing to be told when the file is sitting right there and the search looked
somewhere else.

Search order, first match wins:

1. `--config <path>`
2. `$SHUBBAK_CONFIG` — a file, or a directory containing `shubbak.kdl`
3. `$XDG_CONFIG_HOME/shubbak/shubbak.kdl`
4. each entry of `$XDG_CONFIG_DIRS`
5. `%USERPROFILE%\.config\shubbak\shubbak.kdl`
6. `%APPDATA%\shubbak\shubbak.kdl`

An explicit `--config` is used even when the file does not exist, so you get "that
file is missing" rather than a silent fallback to a different config.

On Windows, `XDG_CONFIG_DIRS` is separated with `;` rather than `:` — a colon would
split `C:\Users\me` at the drive letter.

## "My keybinding does nothing"

```
shubbak check-config
```

Reports unknown commands with a suggestion, unknown keys, and — the one that catches
people out — **duplicate bindings**, naming the line that shadows yours.

If the config is clean, watch the binding fire:

```
shubbak log-level debug
```

Then press the key. Every resolved binding is logged with the commands it ran. If
nothing appears, the keystroke never reached a binding; if it appears but nothing
happens, the command was rejected and the reason is logged.

## "A rule never fires"

`shubbak inspect` lists each app definition with the matcher that failed.

The classic mistake, which Shubbak warns about at load time:

```
title regex="/[Pp]ower[Pp]oint.*/"      # wrong - the slashes are matched literally
title regex="[Pp]ower[Pp]oint.*"        # right
```

## "Windows have disappeared"

They are almost certainly **cloaked**, not closed. Windows on inactive workspaces are
concealed with a DWM cloak, which removes them from the screen, Alt+Tab and the
taskbar while leaving the process running.

The important property is that this is **recoverable**: a cloaked window still reports
as visible to Win32, so simply starting Shubbak again adopts it and un-cloaks it when
its workspace becomes active.

```
shubbak-wm            # restart; concealed windows come back
```

Shubbak also un-cloaks everything on a clean exit. If it was killed outright, the
restart above is the recovery.

If cloaking misbehaves with a particular application, fall back:

```kdl
general { hide-method "hide" }
```

Be aware that `hide` is genuinely unrecoverable - a hidden window is rejected by the
filter as invisible and cannot be re-adopted - so only use it if cloaking is broken in
your environment, which mainly means remote sessions with no compositor.

## "Dragging a window did the wrong thing"

Dropping a tiled window is resolved against the tree:

| Where you drop | What happens |
|---|---|
| middle of another window | the two swap places |
| near its left/right edge | inserted beside it, horizontally |
| near its top/bottom edge | stacked with it, vertically |
| far from any window | nothing; it snaps back |

The edge zone is the outer quarter of each side, leaving the middle half as the swap
zone. A drop landing in the gap between two tiles still resolves to the nearest one.

Dragging a **border** resizes instead, converting the new size back into the tree's
ratios. A move of fewer than 8px, or a size change of fewer than 4px, is ignored -
otherwise clicking a title bar would rearrange the layout.

Run with `--log-level debug` to see each drop resolved.

## "Windows jump around" / "the layout is wrong"

The window tree in the diagnostic report shows the nesting, the layout on each
container and each node's size ratio. When a window is the wrong size, the nesting is
almost always the answer, and an indented drawing shows it at a glance:

```
workspace "1" [active] layout=splith (4,26 3832x2130)
  window 0x8D088C "Firefox" (firefox) Tiling ratio=0.500 (4,26 1916x2130)
  container layout=splitv ratio=0.500 (1920,26 1916x2130)
    window 0x4009FE "Code" (code) Tiling ratio=0.500 (1920,26 1916x1065)
```

## "Everything scattered after a reboot"

Session state lives in `%LOCALAPPDATA%\Shubbak\session.json`. Windows are matched by
process, class and a title hash — deliberately tolerant, because a browser's title
changes constantly.

If restoration puts things in the wrong place, delete that file to start clean. If it
puts *nothing* back, check the log for `session loaded` at startup.

## Capturing an intermittent problem

```
shubbak log-level trace
```

Changes the level on the **running** window manager — no restart, so you do not lose
the state that was about to trigger the problem.

Trace records every window event and every command. It is verbose (a busy desktop
produces well over a hundred entries a second), which is precisely what makes a
misbehaviour reproducible from a log alone.

Reproduce, then:

```
shubbak diagnose -o report.md
```

For a problem that occurs during startup, tracing has to be on from the start:

```
shubbak-wm --log-level trace --log-file
```

## Reading a trace

Filter by category:

```powershell
Select-String -Path report.md -Pattern " Window "     # window lifecycle
Select-String -Path report.md -Pattern " Hook "       # keystrokes and bindings
Select-String -Path report.md -Pattern " Command "    # what ran, what was rejected
Select-String -Path report.md -Pattern " Rule "       # rule matches
Select-String -Path report.md -Pattern " Layout "     # placement passes
```

`EVENT_OBJECT_LOCATIONCHANGE` is deliberately **not** traced. It fires around 120
times a second from a single dragged window; logging it would drown everything else
and slow down the thing being diagnosed.

## If it crashes

A crash writes `%LOCALAPPDATA%\Shubbak\crash-<timestamp>.md` containing the same
report, including the log entries leading up to it. Attach that.

## The bar

Taj logs separately:

```
taj --log-level debug --log-file
```

- **Blank bar** — check `connected to the window manager` appears. Taj retries
  indefinitely, so a missing WM shows as repeated connection attempts.
- **A widget shows nothing** — a widget whose value is empty hides itself, which is
  deliberate: an empty box with padding looks like a rendering fault. Check the
  source name in your template matches a declared source.
- **A widget shows `!`** — the source threw. For a `command` source, run the command
  by hand.

## Known limitations

These are design constraints, not bugs:

- **No whole-desktop workspace transitions.** Windows gives no compositor access.
  Per-window move, resize and fade animations work; sliding the entire desktop does
  not. Komorebi has the same ceiling.
- **Drag-to-swap has no live preview.** The drop is resolved when you release the
  mouse, so there is no highlight showing where the window will land while you drag.
- **Elevated windows need an elevated Shubbak.** They are detected and reported, but
  cannot be moved.
- **A window cannot be on two workspaces at once.** Tags relocate a window to
  whichever tagged workspace you last activated. A Windows window has one position on
  one monitor; anything else would be a promise the platform cannot keep.
