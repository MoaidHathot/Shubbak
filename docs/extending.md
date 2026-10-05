# Extending Shubbak

The bar, the palette and the watcher are not built into the window manager. Each is a
separate program that connects to the same named pipe anything else can, and the
window manager knows none of them by name: it carries a `signal` without reading it,
holds a `context` without asking who set it, and answers a `query` the same whoever
asks. **The pipe is the plugin architecture.** This page is how to write the fourth
program — what it can do, the three idioms the shipped ones are made of, and a worked
example in PowerShell and in C#.

[Scripting](scripting.md) is the reference for the pipe itself: every method, every
topic, the wire format, the security gates. This page assumes it and shows the shape
of a program built on it.

## What a program of yours can do

Everything over one connection, in any mix:

| You want to | Send | It answers |
|---|---|---|
| know what is on the desktop | `query state`, `windows`, `workspaces`, `monitors`, `focused`, `contexts`, `rules`, `bindings`, `arrangements`, `tree` | JSON, or text for `tree` |
| know which config file is in effect, and its text | `query config-path`, `query config` | `{"path","stale"}`; the file |
| do anything a key can do | `command <verb> ...` — forty verbs, the same syntax a binding uses | ok, or why not |
| be told when something changes | `subscribe <topics>` — thirty topics | a stream of `{"topic","data"}` |
| tell the window manager a fact it cannot see | `command context --set <name> --lease` | held while you are connected |
| put a value on the bar, or tell another program something | `command signal <name> <args...>` | delivered to whoever is subscribed to `signal` |
| be told to do something, from a key or the bar | subscribe to `signal`, read `{"name","arguments"}` | — |
| draw a window's icon | `window-icon <handle> [size]` | BGRA pixels |

Two things are gated, because the pipe is scoped to your account and not to your
integrity level: `shell-exec` is refused unless `general { allow-shell-exec-over-ipc
#true }`, and `add-rule` / `remove-rule` — the only methods that write the config
file, and only rules — can be turned off with `allow-config-edits-over-ipc #false`.
See [Security](scripting.md#security). There is no method that sets an arbitrary
setting: policy lives in the file, and a program supplies facts.

## The three idioms

Every shipped companion is some combination of these.

**A provider** knows a fact the window manager cannot see — the camera is on, a call
is up, a build is running — and supplies it as a context. The config says what the
context *does*; the provider never needs to know:

```kdl
contexts {
    context "focusing" {                    // no `when`: external, set over the pipe
        bindings { bind "alt+shift+q" { } } // disarm close while it holds
        window-effects { border #false }
    }
}
```

The provider sends `context --set focusing --lease` and stays connected; the pin dies
with the connection, so a provider that crashes leaves nothing behind. It subscribes
to `config.reloaded` on the same connection, and pins again after a reload that
landed — a reload drops the pins of contexts the new file no longer declares and says
nothing — and to `wm.shutdown`, to know whether the window manager is restarting
(stay, reconnect, pin again) or leaving for good (`{"everything":true}`: go too).
`shubbak context --set focusing --hold` is this whole idiom as one command, for a
script that would rather not open the pipe at all.

**A publisher** has a value the bar should show. It sends `signal <name> <value>`
whenever the value changes, and the bar's `source "<name>" kind="signal"` reads it:

```kdl
bar {
    source "focus" kind="signal"            // shubbak signal focus 24:59

    profile "default" {
        zone "right" justify="end" {
            text template="{{ focus }}"     // empty hides itself, so this costs no room until it runs
        }
    }
}
```

A signal is fire-and-forget and the window manager keeps none of it, so a bar that
starts after the value was last sent would be blank. The bar therefore raises `signal
"announce"` when it connects, and a publisher that hears it says its values again —
subscribe to `signal`, and on `announce` repeat what you last said. A signal with no
arguments clears the value, which hides the widget.

**A listener** is told what to do. Bind a key — or a bar widget's `on-click` — to a
signal the window manager has never heard of, subscribe to `signal` from your program,
and act on it:

```kdl
keybindings {
    bind "alt+f" { signal "focus" "start" "25" }
    bind "alt+shift+f" { signal "focus" "stop" }
}
```

That is the whole of how the palette exists: `signal "palette"` from a key, and a
program that is listening. Arguments after the name are yours to define. `shubbak
signal focus start 25` from a terminal raises exactly the same signal.

## A worked example: a focus timer

Something none of the shipped programs do, that uses all three idioms. Start it from a
key for twenty-five minutes; while it runs, a context holds — so the config can disarm
the close key, drop the borders, or pick a quieter bar profile — and the bar shows the
time left; stopping it, or the time running out, lets go of both. Two versions, the
same design, in [`examples/`](../examples/):

**[`focus-timer.ps1`](../examples/focus-timer.ps1)** is built entirely from the
command line and never opens the pipe itself. `shubbak sub signal` is its ears,
`shubbak signal focus mm:ss` is its voice, and `shubbak context --set focusing --hold`
is its hand on the context. Every primitive is reachable this way, which is what makes
a shell script a complete extension:

```powershell
.\examples\focus-timer.ps1           # waits for `signal focus start [minutes]`
```

**[`Shubbak.Example.FocusTimer`](../examples/Shubbak.Example.FocusTimer/)** is the same
program in C#, on the `Shubbak.Ipc` package, over one connection: it subscribes, holds
the context with a lease on that connection, publishes on it, and reconnects when the
window manager restarts. It is the shape to copy for a program that has reasons of its
own to be a process — one that reads a sensor, a calendar, or another program's API —
and it is what the watcher looks like with everything but the pipe taken out:

```
dotnet run --project examples/Shubbak.Example.FocusTimer
```

Both want this in the config: the context to hold, the source to show it, and the keys
to drive it. Together, as one file:

```kdl
contexts {
    context "focusing" {
        bindings { bind "alt+shift+q" { } }
        window-effects { border #false }
    }
}

bar {
    source "focus" kind="signal"

    profile "default" {
        zone "left" justify="start" { workspaces }
        zone "right" justify="end" {
            text template="{{ focus }}" background="accent 40%" radius=6
        }
    }
}

keybindings {
    bind "alt+f" { signal "focus" "start" "25" }
    bind "alt+shift+f" { signal "focus" "stop" }
}
```

`shubbak contexts` says who holds `focusing` and on which connection; `shubbak sub
signal,context.changed` shows the conversation as it happens.

## Writing against the pipe directly

The package is a convenience, not a requirement. The wire is newline-delimited JSON
over `\\.\pipe\shubbak-v2-<SID>` — your account's SID, which `whoami /user` prints —
and anything that can open a named pipe can speak it:

```
→ {"method":"subscribe","payload":"signal,config.reloaded","id":1}
← {"id":1,"ok":true}
→ {"method":"command","payload":"context --set focusing --lease","id":2}
← {"id":2,"ok":true}
← {"topic":"signal","data":"{\"name\":\"focus\",\"arguments\":[\"stop\"]}"}
```

Requests carry an `id` you choose and the reply echoes it; events have a `topic` and
no `id`; `payload` and `data` are strings, usually strings containing JSON. One
connection can do both at once: a reply is written whole between events. Read
[Scripting](scripting.md) for the rest, and `SHUBBAK_INSTANCE` there for trying a
program against a throwaway window manager without touching the one you are using.

## What to expect of the window manager

- **It is honest about not being there.** `IpcClient.IsServerRunning()` is a
  directory listing; `ping` answers `pong`. A program that polls for the pipe once a
  second costs nothing while it is absent.
- **It never waits for you.** A client that stops reading has its backlog dropped and
  is handed `wm.resync`; re-read your picture when you see it. A request is answered
  on the window manager's own thread, which may be busy adopting windows at startup,
  so allow ten seconds before calling it wedged.
- **Fields are only ever added.** A payload gains optional fields over time and never
  loses or renames one without the protocol version — the `v2` in the pipe name —
  changing, so a program reads the fields it knows and ignores the rest.
- **Suspend and pause do not touch the pipe.** A suspended window manager has let go
  of the keyboard and stopped placing windows, for a game or a full-screen app; it
  still answers, still carries signals, still holds contexts. `wm.suspended` and
  `wm.paused` say when, and `query state` carries both, so a program that should go
  quiet when the window manager has can.
- **A signal with nobody listening is logged**, at info, as a key that does nothing —
  because the usual cause is that the program meant to handle it is not running.
