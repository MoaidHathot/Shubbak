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
```

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
described below and is the one method whose connection is expected to stay open.

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

## Signals

`signal "name" [args...]` publishes a name Shubbak does not interpret at all, to
whichever clients are subscribed to the `signal` topic. That is how Dalil exists
without the window manager knowing about it — a keybinding raises `signal "palette"`
and the palette is what is listening — and how Ayn is told to flip the mute. It is how
you would wire in your own tools: bind a key to a signal, subscribe to it from your
program, and Shubbak never has to learn what it means.

## Supplying facts

A [context](configuration.md#contexts) with no `when` is external: nothing on the
desktop decides it, so a program does. `shubbak context --set meeting --ttl 10s` from
any script pins it for ten seconds, so a poller that crashes leaves nothing behind. A
program that holds a pipe connection open can say `context --set meeting --lease`
instead, and the pin dies with the connection. That is exactly what Ayn does for
everything it watches — the camera and the microphone, so far; Teams presence, OBS
recording or a calendar are written the same way.

## Security

`shell-exec` is refused over the pipe by default. A window manager is not an
execution service, and the pipe is scoped to your *account*, not to your *integrity
level* — so leaving it open would mean any process running as you could ask an
elevated Shubbak to launch something elevated. Flip `allow-shell-exec-over-ipc` in the
`general` section if you want it; keybindings and startup commands can always use it
either way.
