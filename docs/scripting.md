# Scripting

Everything the CLI and the palette do goes over one named pipe, `shubbak-v2-<SID>`,
scoped per user, with the protocol version in the name. Newline-delimited JSON. The
bar, the palette and the watcher are all just clients of it, and so can anything you
write.

## Asking

```
shubbak query state          # the whole window manager, as JSON
shubbak query windows        # or: all-windows, workspaces, monitors, focused,
                             #     layouts, commands, bindings, contexts, arrangements, rules
shubbak query tree           # the tree as text: monitors, workspaces, containers, windows
shubbak query config-path    # {"path":"C:\\...\\shubbak.kdl","stale":false}
shubbak query config         # the file's text, as it is on disk
```

The last two are about the file rather than the desktop, and exist because the
window manager is the only one who knows the answer. Every program resolves the same
search order, so they agree about which file is loaded — until the window manager is
started with `--config`, after which the file it reads is one the search order never
finds. `config-path` is the window manager's own answer; `path` is null when it is
running on defaults because no file could be found or written. `stale` says the file
on disk is not what was last loaded: a save with `reload-on-save` off, or a reload the
window manager refused because the file had errors, which a program reading the same
file cannot otherwise tell from one that landed. `config` is the whole file, every
section — the bar's and the palette's included, which the window manager does not
parse but does return — and is refused rather than empty when there is no file, since
a file can be empty.

One more thing can be asked over the pipe that the CLI has no subcommand for, because
its answer is a picture: **`window-icon`**. Send the method `window-icon` with the
window's handle as the payload — optionally followed by a space and the size you mean
to draw at, which picks the large or the small variant — and the answer is the
window's icon as pixels:

```json
{"handle":131842,"width":32,"height":32,"pixels":"<base64>","source":"window"}
```

`pixels` is `width * height * 4` bytes, rows top-down, blue-green-red-alpha with the
colour premultiplied by the alpha, which is what a compositor draws directly. `source`
says who answered: the window itself (`WM_GETICON`, the way the taskbar asks), its
class, or the executable's own icon (`file`) for a window that never set one. The
daemon does the asking so that a client never sends a message to a window that may be
hung — the ask is bounded to a tenth of a second and served off the daemon's loop —
and remembers answers for half a minute, so a bar that asks on every focus change
costs the window one read. This is how Taj draws the focused window's icon; a keycast
overlay or a task switcher of your own gets the same picture the same way.

## Telling

Anything the CLI does not recognise as its own subcommand is forwarded straight to
the daemon as a command, with exactly the syntax a keybinding uses:

```
shubbak focus --workspace 3
shubbak layout --set fibonacci
shubbak context --set meeting --ttl 10s
```

The [commands](configuration.md#commands) page lists the verbs.

Over the pipe itself, a request is one JSON line with a `method` and a `payload`.
The CLI covers `command`, `query`, `inspect` (a window handle; the report `shubbak
inspect` prints), `window-icon`, `add-rule` and `remove-rule` (what the palette's
"Add it to the config" sends; refused unless `allow-config-edits-over-ipc` holds),
`diagnose` (the report `shubbak diagnose` prints), `log-level` (`trace` to `none`,
for the life of the process) and `ping`, which answers `pong` and is the cheapest way
to ask whether the window manager is there. `subscribe` opens the event stream
described below and is the one method whose connection is expected to stay open. A
subscribed connection still answers requests: a reply is written between the events,
each a whole line, and carries the `id` you sent, so one connection can hold a
context with a lease and hear that the file was reloaded. The watcher and
`shubbak context --hold` do exactly that.

## Listening

```
shubbak sub                  # tail every event
shubbak sub window.focused,workspace.activated
```

**30 event topics** you can subscribe to:

```
window.managed       window.unmanaged      window.focused      window.title_changed
window.state_changed window.tags_changed   window.moved        window.native_fullscreen
workspace.activated  workspace.created     workspace.destroyed workspace.moved
layout.changed       container.resized     gaps.changed
monitor.added        monitor.removed       monitor.changed
binding_mode.changed binding.fired         command.rejected    config.reloaded
wm.paused            wm.suspended          wm.environment      wm.shutdown         wm.resync
context.changed      arrangement.restored  signal
```

Three of those exist purely so that things outside the daemon can know what it
knows. `window.native_fullscreen` says an application took its own window full-screen
(a video, a slide show) or gave the monitor back; it is an observation, not a state,
and the window is still tiled underneath. `wm.environment` says the session became
remote or stopped being, or the shell's idea of what you are doing changed —
presenting, a full-screen app, a game. `binding.fired` reports each chord Shubbak
claimed and ran, by key and verb name, and **only** those: nothing typed into an
application can reach it, which is what makes a keycast overlay for a talk a small
external subscriber rather than a keylogger.

Subscribe to a topic that does not exist and you get told, along with the list of
ones that do. `wm.resync` tells you your backlog was dropped; `wm.shutdown` tells you
the daemon is leaving on purpose, and its payload says how much is going with it:
`{}` after `wm-exit`, when the palette and the watcher stay for its return, and
`{"everything":true}` after `exit-all`, when they leave too. A client of your own that
outlives the window manager should read that field the same way.

`config.reloaded` is raised whenever the window manager re-reads its file — by the
key, by a save with `reload-on-save` on, or after `add-rule` — and whatever the
outcome, because the file moved either way. Its payload says which file and whether
it landed: `{"path":"C:\\...\\shubbak.kdl","accepted":true}`, or `"accepted":false`
when the file had errors and the window manager kept what it had. A program that
reads the same file should follow only an accepted reload, as the palette and the
watcher do; following a refused one means re-reading a file that does not parse and
running on whatever that yields, while the window manager runs on the file before it.
An older window manager sends `{}`, which reads as accepted with no path.

## Signals

`signal "name" [args...]` publishes a name Shubbak does not interpret at all, to
whichever clients are subscribed to the `signal` topic. That is how Dalil exists
without the window manager knowing about it — a keybinding raises `signal "palette"`
and the palette is what is listening — and how Ayn is told to flip the mute. It is how
you would wire in your own tools: bind a key to a signal, subscribe to it from your
program, and Shubbak never has to learn what it means.

It also runs the other way: a signal is how a program puts a **value on the bar**. A
`source "battery" kind="signal"` in the [bar's section](taj.md#sources) reads
`{{ battery }}` from whatever `signal "battery" "41"` last said, so a script that knows
something is one command away from showing it:

```
shubbak signal battery 41              # {{ battery }} reads 41
shubbak signal weather Sunny 21C       # several words are joined: "Sunny 21C"
shubbak signal battery                 # nothing after the name clears the readout
```

One signal name is a convention rather than any program's own: **`announce`**. A
signal is fire-and-forget and the window manager keeps none of it, so a bar that
starts after a value was last sent would be blank until the value next changed. The
bar therefore raises `signal "announce"` when it connects with a signal source in its
file, and again after a reload, and a publisher that hears it says its values again.
Ayn does; a script of yours that publishes a value should too — subscribe to `signal`,
and on `announce` repeat what you last said. One that does not is simply blank on the
bar until its next change. The bar subscribes to the topic only when its file has a
signal source, so a bar that shows none costs the window manager nothing per signal
and leaves it able to say when a signal was raised with nobody listening.

## Supplying facts

A [context](configuration.md#contexts) with no `when` is external: nothing on the
desktop decides it, so a program does. Three ways, from the least to the most
involved:

```
shubbak context --set meeting --ttl 10s      # for ten seconds; repeat from a poller
shubbak context --set meeting --hold         # for as long as this process runs
```

`--ttl` from a poller pins the context for that long, so a poller that crashes leaves
nothing behind. `--hold` is the one for a script that knows when something starts and
when it ends: the command pins the context with a lease and *stays*, and the pin dies
with the process — Ctrl+C, `Stop-Process`, or the script that started it ending. A
window manager that restarts or reloads under it is holding the context again within
a second, and `exit-all` ends the hold, since a process waiting to pin a context on a
window manager that is not coming back is what a lease exists to avoid. Refused
outright — a context the file does not declare — it exits 1 with the reason; started
with no window manager running it exits 2 at once rather than waiting for one.

```powershell
$hold = Start-Process shubbak -PassThru -WindowStyle Hidden `
          -ArgumentList 'context --set in-call --hold'
try   { <# the call #> }
finally { Stop-Process $hold }
```

The third way is what `--hold` is made of, for a program that has its own reasons to
hold a pipe connection: send `context --set meeting --lease` and keep the connection
open. Subscribe on the same connection — to `config.reloaded`, so you can pin again
after a reload that dropped it, and to `wm.shutdown`, so you know whether to wait —
and one connection is the whole provider. That is exactly what Ayn does for
everything it watches — the camera and the microphone, so far; Teams presence, OBS
recording or a calendar are written the same way, and a script that can tell when
they start needs nothing more than `--hold`.

## Security

`shell-exec` is refused over the pipe by default. A window manager is not an
execution service, and the pipe is scoped to your *account*, not to your *integrity
level* — so leaving it open would mean any process running as you could ask an
elevated Shubbak to launch something elevated. Flip `allow-shell-exec-over-ipc` in the
`general` section if you want it; keybindings and startup commands can always use it
either way.

## A second Shubbak beside the first

Two environment variables let a separate set of Shubbak processes run under one
account without finding the first, which is what the end-to-end test does and what a
script that wants to try a config against a throwaway daemon might. `SHUBBAK_INSTANCE`
is a short name — letters, digits, dot and dash — appended to the pipe, the
single-instance mutexes and the stop events: a daemon started with it listens on
`shubbak-v2-<SID>-<name>`, and a `shubbak` started with the same value talks to that
daemon and no other. `SHUBBAK_STATE_DIR` is an absolute path used in place of
`%LOCALAPPDATA%\Shubbak` for the logs, the session, the arrangements, crash reports and
the palette's memory. Both are read once, when the process starts. Neither stops two
window managers from both managing the same windows — a second daemon with its own
pipe is still a second daemon on the one desktop — so a config for the second usually
begins with a rule that ignores everything it is not there to look at.
