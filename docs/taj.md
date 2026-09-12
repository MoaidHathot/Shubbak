# Taj — the bar

<img src="assets/taj.png" width="72" align="right" alt="" />

**Taj** (تاج, *"crown"*) is the status bar, and it is already in the box. One bar per
monitor, each reserving its own strip, each able to show a different profile. It is
its own program, `taj`, started by the window manager from the config
(`startup-command "taj"`), and it reads the `bar` section of the same file.

```kdl
bar {
    source "clock" kind="time" format="ddd d MMM HH:mm" interval=500
    source "keyboard" kind="keyboard" interval=250

    profile "default" {
        height 34
        background "#1e1e2e"
        foreground "#cdd6f4"

        zone "left" justify="start" gap=4 {
            workspaces hide-empty=#true active-background="#8dbcff"
        }
        zone "centre" justify="center" grow=1 {
            text template="{{ window.title | truncate:90 }}"
        }
        zone "right" justify="end" gap=12 {
            text template="{{ layout | icon }}" colour="#7f849c"
            text template="{{ clock }}" colour="#8dbcff"
        }
    }
}
```

## Widgets without code

**Adding a widget usually needs no code at all.** There are three widget primitives
— `text`, `workspaces`, `spacer` — and the breadth comes from templates, filters and
sources rather than from a catalogue you have to wait for someone to grow:

| What you want | What it costs |
|---|---|
| A new value on the bar | A few lines of KDL |
| Something Taj has never heard of | Any program that writes lines to stdout |
| Genuinely custom drawing | One `IWidget` implementation |

### Sources

A `source` is a value the bar watches. `kind="time"` is a clock, with a `format` and an
optional `timezone` (a Windows id or an IANA one); `kind="keyboard"` is the input
language of the window in front, as a two-letter code; `kind="command"` runs any
program and takes each line it prints as the value. The `interval` is how often a
value is *checked*, not how often the bar redraws: an unchanged value is suppressed,
so a clock showing minutes can be polled twice a second for a prompt tick without
repainting twice a second.

Some values need no source at all, because they come from the window manager's event
stream: `{{ window.title }}`, `{{ window.state }}`, `{{ layout }}`, `{{ workspace }}`,
`{{ paused }}`, `{{ suspended }}`, `{{ binding_mode }}`, `{{ config }}`,
`{{ contexts }}` and `{{ context.<name> }}`. The last six are empty almost all of the
time, and a widget whose template renders empty hides itself — so they cost no room
until something is unusual.

### Templates and filters

Templates get filters — `truncate:N` `upper` `lower` `trim` `default:X` `then:X`
`pad:N` `replace:from,to` `icon` `state-icon` — and a `when { }` block for conditional
styling, so "colour the keyboard indicator red when I'm in the wrong language" is a
line, not a plugin:

```kdl
bar {
    source "keyboard" kind="keyboard" interval=250

    profile "default" {
        zone "right" justify="end" {
            text template="{{ keyboard }}" colour="#7f849c" {
                when value="HE" colour="#f38ba8" bold=#true
            }
        }
    }
}
```

`when value="X"` matches the drawn text, `when not="X"` everything else, and
`when of="source"` tests a source instead of the text — useful after a filter has
already turned the value into a glyph. First match wins.

A widget can have a `font=` of its own, which is how one widget draws a glyph from
Segoe Fluent Icons beside text in the profile's face. `{{ contexts }}` names the
contexts the window manager holds, and is empty when none do; `{{ context.meeting }}`
is `meeting` while that one holds and empty otherwise, so
`{{ context.meeting | then:\u{E720} }}` is a microphone glyph that appears for the
call and leaves with it.

### Clicking

Anything with an `on-click` is a control: the pointer becomes a hand and the widget
lights up under it — a pill lightens, a bare glyph gains the same faint pill the
workspaces use — or takes `hover-background` and `hover-colour` of its own. The value
of `on-click` is a command, the same ones a keybinding runs, so clicking a workspace
sends what a keybinding would, and `on-click="wm-resume"` on the `{{ suspended }}` pill
is the way back that does not need the keyboard.

## Profiles, zones and rules

Zones are flex containers with `justify`, `gap` and `grow`; three is a convention, not
a limit. Profiles can `extend` each other, so a slim "presentation" variant costs five
lines instead of a duplicate:

```kdl
monitor "laptop" { internal }

contexts {
    context "presenting" { when { system-state "presenting" } }
}

bar {
    source "clock" kind="time" format="HH:mm" interval=500

    profile "default" {
        height 34
        zone "right" justify="end" gap=12 {
            text template="{{ clock }}" colour="#8dbcff"
        }
    }

    profile "presentation" extends="default" {
        height 24
        zone "right" justify="end" gap=10 {
            text template="{{ clock }}" colour="#8dbcff"
        }
    }

    rule use="presentation" context="presenting"
    rule use="presentation" monitor="laptop"
}
```

A `rule` picks the profile at runtime by `workspace`, `monitor` or `context`. First
matching rule wins, every attribute on a rule has to hold, and switching is a pointer
swap rather than a restart. `monitor=` takes the names the `monitor` definitions
declare, or a position; `context=` takes the names the `contexts` section declares,
and a rule on a context nobody declares is pointed out rather than silently never
matching.

## Why it does not show stale titles

The bar consumes the window manager's event stream and never inspects windows itself.
`EVENT_OBJECT_NAMECHANGE` fires on things like browser tab switches — about twice as
often as focus changes — so a bar listening only for focus quietly misses two thirds
of title updates. Taj cannot, because it is not listening to Windows at all.

Widgets re-render only when a source they use actually changes, so an idle desktop
does not repaint. The message loop waits rather than polling: the model says when it
changes and the loop wakes for that, with a one-second ceiling so a missed signal can
never leave the bar looking frozen. It used to run sixty-two passes a second whatever
was happening, and measured over an idle desktop it spent more CPU than the window
manager it reports on; now it spends almost none.

## When the window manager goes away

A clean `wm-exit` or `exit-all` announces itself and the bar closes within a frame.
If the window manager is killed instead, the bar waits `window-manager-timeout`
seconds (30 by default; 0 waits for ever) for it to come back, reconnects if it does,
and closes if it does not. A bar that has never connected waits indefinitely, because
it is normally launched by the window manager's own startup command and can win the
race.

`shubbak taj-exit` closes the bar on its own; only one bar runs per account.

## Under the hood

```
L1 transport    Shubbak's IPC
L2 sources      reactive values: WM events, timers, external processes
L3 widget tree  renderer-agnostic model + flex layout
L4 renderer     ITajRenderer — currently GDI
```

L2 and L3 contain no drawing code and are covered by tests that run with no window
on screen. Swapping the renderer means implementing one interface.
