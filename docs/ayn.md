# Ayn — the watcher

<img src="assets/ayn.png" width="72" align="right" alt="" />

**Ayn** (عين, *"eye"*) is the smallest of the five programs, and optional. It is the
window manager's eyes on the rest of the machine. Whether a device is in use or a
switch is flipped is a fact about the machine, not about a window, so the window
manager does not read it; Ayn does, and hands each fact over as a
[context](configuration.md#contexts) the window manager holds while the fact is true.

Out of the box it watches the camera and the microphone, and supplies three facts:
`camera-in-use` and `microphone-in-use`, from the record Windows keeps of which
programs have a device open — the one the privacy indicator in the tray reads — and
`microphone-muted`, from the mute switch of the default communications microphone,
falling back to the default one — the switch the Sound settings toggle. Seven more
are there for the asking, off until the file names them: `screen-captured` (a program
is sharing or recording the screen, from the same record), `speaker-muted`,
`on-battery`, `battery-low`, `lid-closed`, `user-away` (Windows's own judgement that
nobody is at the keyboard — the one that dims the display) and `dark-theme`. Facts are
named `subject-state`, so the ones about one device sit together and the next device
slots in beside them.

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
settled, because a person who pressed the key wants the icon now; nor are the power
facts or the theme.

The rest of the section, all optional:

```kdl
contexts {
    context "camera-in-use" { }
    context "sharing" { }
    context "in-a-call" { }
    context "speaker-muted" { }
    context "on-headset" { }
    context "unplugged" { }
    context "low-battery" { }
    context "away" { }
    context "lid-closed" { }
    context "dark" { }
}

ayn {
    camera {
        in-use "camera-in-use"
        ignore "obs64.exe" "Lens*"          // these do not count as use
        by "ms-teams.exe" "in-a-call"       // a context of its own for one program
    }
    screen  { captured "sharing" }
    speaker {
        muted "speaker-muted"
        device "*barracuda*" "on-headset"  // while the headset is what sound goes to
    }
    power   { on-battery "unplugged"; battery-low "low-battery"; battery-low-at 15; user-away "away"; lid-closed "lid-closed" }
    theme   { dark "dark" }
    renew 60                                // re-assert every held context each minute
}
```

`ignore` names programs whose use of a device does not count — the recording tool
that keeps the camera open all day is not a meeting — and `by` gives one program's
use a context of its own, so a Teams call and an OBS stream can be told apart without
any context knowing the difference between programs. Both take the program's name as
it appears in `ayn --report`, with `*` and `?` as wildcards and no regard for case.
`microphone` and `screen` take the same two.

`device` is about which device is the default rather than who is using it: `speaker {
device "*barracuda*" "on-headset" }` holds `on-headset` while the default speaker is
one whose name matches, and lets go the moment Windows moves the default — to the
monitor when the headset is unplugged, say. `microphone` takes it too, for the webcam's
microphone against the headset's. The name is the one Windows shows in its sound
settings and `ayn --report` prints, with the same wildcards and no regard for case.
Nothing to settle here: plugging a headset in is one event, and the person who did it
is looking at the bar. A rule missing its name or its context is pointed out and
skipped (`AYN0011`).

`battery-low-at` is the percentage `battery-low` starts at; twenty unless said.
`renew` is off unless said: with it, every held context is asserted again that many
seconds apart with a time to live of twice that, so a watcher that is alive but stuck
— connection open, loop wedged — loses its pins too, a little after it stops renewing
them. It is off by default because a window manager stalled for longer than the time
to live would drop the context and take it back a moment later, running whatever the
file hangs on that.

Two facts naming one context are pointed out (`AYN0006`), because each hands the
context back when it goes false and takes the other's pin with it; the file wants two
contexts and a third composed from them. `camera "meeting"` is read as
`camera { in-use "meeting" }` and said so (`AYN0007`).

## What it acts on

Two things: `signal "ayn" "microphone" "mute" | "unmute" | "toggle-mute"` from a
keybinding, the bar or the palette flips the system mute, and the endpoint's own
change notification turns that into the context — so a bar widget that reads
`{{ context.meeting-muted | then:\u{EC54} }}` with `on-click="signal ayn microphone
unmute"` is a mute button, and the window manager never learns the word. This is the
system's mute: a call's own mute button is the call's and invisible from here, but
Teams and its kind notice this one and say "muted by your system". `signal "ayn"
"speaker" "toggle-mute"` does the same for the default speaker.

## Why it is a separate program

The pins are made with `--lease`, so they die with Ayn's connection: a watcher that
crashes leaves nothing behind, and a window manager that restarts is told again within
a second. The file says what a meeting *does*; Ayn never needs to know. That division
is the point of it — Shubbak observes the desktop, not the applications, and whether
the camera is on is a fact about an application and a piece of hardware. Everything
of that kind lives out here rather than in the daemon, because "it is only a hundred
lines" is true of every device that would follow the camera, and is how a window
manager grows a weather widget. Ayn is where those lines go: the next device is
another subject in its section, not another thing the window manager knows. Anything
with the same shape that Ayn does not watch — Teams presence, OBS recording, a
calendar — is written the same way: hold a pipe connection open and say
`context --set <name> --lease`. See [Scripting](scripting.md).

## Running it

`ayn --report` prints what Windows says about each device, the power and the theme
right now, which is the same reading the watcher acts on. It outlives a `wm-exit` and
reconnects when the window manager returns; `exit-all` takes it down with everything
else, as do `shubbak ayn-exit` on its own and `shubbak stop` from outside. Only one
watcher runs per account. It sleeps on registry notifications, Core Audio callbacks
and power notifications and holds no timer between changes — a microphone unplugged
or a speaker swapped is noticed the same way — so an idle watcher costs nothing.
Sources the file does not name are never opened, and a device the file starts naming
after a reload is opened then, without a restart.
