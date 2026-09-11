# Ayn — the watcher

<img src="assets/ayn.png" width="72" align="right" alt="" />

**Ayn** (عين, *"eye"*) is the smallest of the five programs, and optional. It supplies
three facts, each a [context](configuration.md#contexts) the window manager holds
while the fact is true: `camera-in-use` and `microphone-in-use`, from the record
Windows keeps of which programs have a device open — the one the privacy indicator in
the tray reads — and `microphone-muted`, from the default microphone's mute switch —
the one the Sound settings toggle.

It is its own program, `ayn`, started by the window manager from the config
(`startup-command "ayn"`), and it reads the `ayn` section of the same file.

```kdl
contexts {
    context "camera-in-use" { }         // facts: nothing in the file sets them, ayn does
    context "microphone-in-use" { }
    context "microphone-muted" { }

    context "meeting" {                 // policy: yours to write
        when { context "microphone-in-use" }
        bindings { bind "alt+shift+q" { } }
    }
    context "meeting-muted" { when { context "meeting"; context "microphone-muted" } }
}

ayn {
    camera     { in-use "camera-in-use" }
    microphone { in-use "microphone-in-use"; muted "microphone-muted" }
    settle 500                          // ms a change of use must last; mute is instant
}
```

The names must be declared in `contexts`, and `shubbak check-config` says so when one
is not. `camera #false` says nothing about the camera at all; `muted #false` turns one
fact off. `settle` is how long a change of use must last before it is believed: a call
opens and closes the devices several times while it is setting up. The mute is never
settled, because a person who pressed the key wants the icon now.

## What it acts on

One thing: `signal "ayn" "microphone" "mute" | "unmute" | "toggle-mute"` from a
keybinding, the bar or the palette flips the system mute, and the endpoint's own
change notification turns that into the context — so a bar widget that reads
`{{ context.meeting-muted | then:\u{EC54} }}` with `on-click="signal ayn microphone
unmute"` is a mute button, and the window manager never learns the word. This is the
system's mute: a call's own mute button is the call's and invisible from here, but
Teams and its kind notice this one and say "muted by your system".

## Why it is a separate program

The pins are made with `--lease`, so they die with Ayn's connection: a watcher that
crashes leaves nothing behind, and a window manager that restarts is told again within
a second. The file says what a meeting *does*; Ayn never needs to know. That division
is the point of it — Shubbak observes the desktop, not the applications, and whether
the camera is on is a fact about an application. Anything with the same shape — Teams
presence, OBS recording, a calendar — is written the same way: hold a pipe connection
open and say `context --set <name> --lease`. See [Scripting](scripting.md).

## Running it

`ayn --report` prints what Windows says about each device right now, which is the
same reading the watcher acts on. `shubbak ayn-exit` stops it, and `shubbak stop`
stops it along with everything else; only one watcher runs per account. It sleeps on
a registry notification and two Core Audio callbacks and holds no timer between
changes, so an idle watcher costs nothing.
