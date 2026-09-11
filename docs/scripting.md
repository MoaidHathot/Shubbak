# Scripting

Everything the CLI and the palette do goes over one named pipe, `shubbak-v2-<SID>`,
scoped per user, with the protocol version in the name. Newline-delimited JSON. The
bar, the palette and the watcher are all just clients of it, and so can anything you
write.

## Asking

```
shubbak query state          # the whole window manager, as JSON
shubbak query windows        # or: all-windows, workspaces, monitors, focused,
                             #     layouts, commands, bindings, contexts, arrangements
```

## Telling

Anything the CLI does not recognise as its own subcommand is forwarded straight to
the daemon as a command, with exactly the syntax a keybinding uses:

```
shubbak focus --workspace 3
shubbak layout --set fibonacci
shubbak context --set meeting --ttl 10s
```

The [commands](configuration.md#commands) page lists the verbs.

## Listening

```
shubbak sub                  # tail every event
shubbak sub window.focused,workspace.activated
```

**28 event topics** you can subscribe to:

```
window.managed       window.unmanaged      window.focused      window.title_changed
window.state_changed window.tags_changed   window.moved        window.native_fullscreen
workspace.activated  workspace.created     workspace.destroyed workspace.moved
layout.changed       container.resized
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
the daemon is leaving on purpose.

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
instead, and the pin dies with the connection. That is exactly what Ayn does for the
camera and the microphone; Teams presence, OBS recording or a calendar are written the
same way.

## Security

`shell-exec` is refused over the pipe by default. A window manager is not an
execution service, and the pipe is scoped to your *account*, not to your *integrity
level* — so leaving it open would mean any process running as you could ask an
elevated Shubbak to launch something elevated. Flip `allow-shell-exec-over-ipc` in the
`general` section if you want it; keybindings and startup commands can always use it
either way.
