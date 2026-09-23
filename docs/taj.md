# Taj — the bar

<img src="assets/taj.png" width="72" align="right" alt="" />

**Taj** (تاج, *"crown"*) is the status bar, and it is already in the box. One bar per
monitor, each reserving its own strip, each able to show a different profile. It is
its own program, `taj`, started by the window manager from the config
(`startup-command "taj"`), and it reads the `bar` section of the same file. The strip
is negotiated with the shell the way the taskbar's is, so a bar that asks for an edge
another docked bar already holds is placed beside it rather than over it, and the
window manager's work area stays honest.

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

**Adding a widget usually needs no code at all.** There are four widget primitives
— `text`, `workspaces`, `icon`, `spacer` — and the breadth comes from templates,
filters and sources rather than from a catalogue you have to wait for someone to grow:

| What you want | What it costs |
|---|---|
| A new value on the bar | A few lines of KDL |
| Something Taj has never heard of | Any program that writes lines to stdout |
| Genuinely custom drawing | One `IWidget` implementation |

### Sources

A `source` is a value the bar watches. `kind="time"` is a clock, with a `format`, an
optional `timezone` (a Windows id or an IANA one) and an optional `culture` — a BCP 47
name such as `de-DE` or `ar-SA` that decides what `dddd` and `MMMM` come out as;
without one they are English. `kind="keyboard"` is the input language of the window in
front, as a two-letter code. `kind="command"` runs a program, and comes in two shapes:
without an `interval` the program is expected to stay running and every line it prints
is the new value — a program that exits is started again, with a wait that doubles to
a minute if it keeps exiting without printing; with an `interval` the program is
expected to print and exit, and is run again that many milliseconds after it does, its
last non-empty line being the value — the shape a shell one-liner or an i3blocks
script already has. In either shape the `interval` is how often a value is *checked*,
not how often the bar redraws: an unchanged value is suppressed, so a clock showing
minutes can be polled twice a second for a prompt tick without repainting twice a
second.

```kdl
bar {
    source "cpu" kind="command" command="pwsh -NoProfile -File cpu.ps1" interval=2000
    source "berlin" kind="time" format="dddd HH:mm" timezone="Europe/Berlin" culture="de-DE"
}
```

The sources are made once and shared by every bar, so a desk with three displays runs
one clock and one copy of each script, not three. A bar that arrives later — a
monitor plugged in — is given every value the sources already have. A source whose
`kind` the bar does not know, or a `command` with nothing to run, is pointed out at
load (`TAJ0027`, `TAJ0028`), as is a `culture` the machine has never heard of
(`TAJ0032`).

Some values need no source at all, because they come from the window manager's event
stream: `{{ window.title }}`, `{{ window.state }}`, `{{ layout }}`, `{{ workspace }}`
(the active workspace's name on this bar's display), `{{ paused }}`,
`{{ suspended }}`, `{{ binding_mode }}`, `{{ config }}`, `{{ contexts }}`,
`{{ context.<name> }}` and `{{ connection }}`. The last seven are empty almost all of
the time, and a widget whose template renders empty hides itself — so they cost no
room until something is unusual. `{{ connection }}` reads `no window manager` while a
window manager the bar had reached has gone, and nothing before the first connection,
so a bar that wins the race at logon does not open by announcing the window manager
missing.

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
            text template="{{ keyboard }}" colour="#7f849c" on-click="keyboard next" {
                when value="HE" colour="#f38ba8" bold=#true
            }
        }
    }
}
```

`when value="X"` matches the drawn text, `when not="X"` everything else, and
`when of="source"` tests a source instead of the text — useful after a filter has
already turned the value into a glyph. First match wins. The `on-click` makes the
indicator a switch as well as a readout; see [Clicking](#clicking).

A widget can have a `font=` of its own, which is how one widget draws a glyph from
Segoe Fluent Icons beside text in the profile's face. `{{ contexts }}` names the
contexts the window manager holds, and is empty when none do; `{{ context.meeting }}`
is `meeting` while that one holds and empty otherwise, so
`{{ context.meeting | then:\u{E720} }}` is a microphone glyph that appears for the
call and leaves with it.

### The focused window's icon

```kdl
bar {
    profile "default" {
        zone "centre" justify="center" grow=1 {
            icon size=20
            text template="{{ window.title }}"
        }
    }
}
```

`icon` draws the focused window's icon — the one its taskbar button shows — beside
whatever you put next to it, and hides when nothing is focused, so the title does not
gain a gap. `size` is the square it is drawn in, and four pixels of room around that
square is the pill it shows on hover; in a bar shorter than the two together the icon
is still centred and the room is what the edges clip, so `size=20` in a `height 23`
bar draws the whole picture in the middle. `background` and `radius` put a pill
behind it; `on-click` makes it a control like any text widget. The picture comes over
the pipe like the title does: the bar asks the window manager, which asks the window
(then its class, then its executable) and answers with pixels — see
[`window-icon`](scripting.md#asking). The bar caches each window's icon for a minute
and the window manager for half of one, so switching between the same windows all day
costs one read per window, not one per focus change. `source=` reads another source
that publishes the same shape, for anything else that wants to draw a picture.

### Colours

Colours are `#RGB`, `#RRGGBB` or `#RRGGBBAA`, or the word **`accent`** — the colour
Windows is set to, in Settings or from the wallpaper — so a bar can follow the machine
instead of hard-coding a blue that stops matching the moment you pick a green. Any
colour may be followed by an opacity: `accent 40%` is a translucent accent pill, and
`#8dbcff 40%` is the same thing spelled for a hex colour. The accent is read when the
config is, and the bar re-reads the config when Windows says it changed, so the
change lands on the bar as it lands on the title bars. The same word works for the
palette's theme and the window manager's focus borders, which are the other two
places colours are written.

### Clicking

Anything with an `on-click` is a control: the pointer becomes a hand and the widget
lights up under it — a pill lightens, a bare glyph gains the same faint pill the
workspaces use — or takes `hover-background` and `hover-colour` of its own. The value
of `on-click` is a command, the same ones a keybinding runs, so clicking a workspace
sends what a keybinding would, `on-click="layout --cycle"` on the layout glyph steps
through the layouts, and `on-click="wm-resume"` on the `{{ suspended }}` pill is the
way back that does not need the keyboard.

The other gestures are settings of the same shape: `on-right-click`,
`on-middle-click` (the wheel pressed), `on-scroll-up` and `on-scroll-down` (the wheel
turned away from you and towards you). A widget with any of the five is a control and
gets the hand and the hover, so a volume pill that only scrolls still looks like
something to touch:

```kdl
bar {
    profile "default" {
        zone "right" justify="end" {
            text template="{{ volume }}" on-click="exec mixer" on-scroll-up="exec mixer --up" on-scroll-down="exec mixer --down"
        }
    }
}
```

The `workspaces` widget scrolls on its own: the wheel over it moves to the previous or
next workspace, wrapping at the ends and stepping through the ones `hide-empty` hides
as well as the ones drawn, so a scroll is a step through the display's workspaces and
not only through its pills. `scroll=#false` on the widget turns that off.

One verb is the bar's own and never reaches the window manager: `keyboard`. Put
`on-click="keyboard next"` on the language indicator and clicking it switches the
window in front to its next installed layout — `keyboard previous` goes the other
way, `keyboard he` picks a language by its two-letter code — and the indicator follows
on its next poll. The bar already reads the layout of the window in front, and
changing it is a message posted to that same window, so there is nothing for the
window manager to add. A `keyboard` command the bar cannot perform is pointed out at
load (`TAJ0023`), naming the gesture it was written on.

## Appearance

The bar's surface is drawn with real alpha, so a `background` with an alpha channel is
a genuinely translucent bar, not a darker opaque one — and every pill, hover and
dimmed colour on it composites properly, with anti-aliased corners. What shows
through is up to `backdrop`:

```kdl
bar {
    profile "default" {
        height 34
        background "#181825b3"      // ~70% - the backdrop shows through
        backdrop "acrylic"          // none | mica | acrylic | tabbed
        border "#ffffff14"          // a one-pixel hairline
        // margin 8                 // float the bar off the screen edges...
        // radius 8                 // ...with rounded corners
    }
}
```

- **`background`** takes `#RRGGBBAA`. Fully opaque is the bar there was before;
  `#00000000` is no surface at all, only the text.
- **`backdrop`** asks the compositor for a material behind the translucent pixels:
  `acrylic` blurs whatever is behind the bar, `mica` is a tinted rendering of the
  wallpaper, `tabbed` is Mica's stronger variant. Windows 11 22H2 or later; elsewhere
  the request is ignored and the desktop itself shows through. The material comes
  out dark or light to match the background's own lightness. A backdrop only shows
  through pixels the bar leaves translucent — with an opaque background nothing of it
  is seen. Acrylic is the one to try first: it keeps its blur however the focus
  moves, whereas Mica is a material for windows that are sometimes active, which a
  bar never is.
- **`margin`** floats the bar: inset by that many pixels from the screen edge and
  from both sides, with the desktop showing around it. The strip reserved from other
  windows grows by the same amount on the inner side, so the bar sits in the middle of
  its gap without the window manager's own gaps having to know.
- **`radius`** rounds the bar's corners, anti-aliased. Meant for a floating bar — on a
  docked one it rounds the corners against the screen edge too. With a backdrop the
  material itself is clipped by the compositor, which offers two sizes; `radius 8`
  matches the larger and `radius 4` the smaller.
- **`border`** is a one-pixel line in the given colour: an outline on a floating bar,
  and on a docked one only along the edge that faces the windows.

All five inherit through `extends`, and a variant can set `margin 0` to dock a bar
whose parent floats. `edge "bottom"` puts the bar along the bottom of the display
instead of the top; that inherits too. Text is smoothed in grayscale rather than
ClearType, because ClearType's colour fringes assume an opaque background of known
colour.

A widget's `min-width` and `max-width` bound its box in the flex layout, so a value
that changes width from second to second — a clock with seconds, a percentage — can
be given a floor and stop nudging its neighbours; a `max-width` cuts with an ellipsis
like a shrinking zone does.

### Sizes and displays

Every size in the `bar` section — `height`, `font-size`, `padding`, `margin`,
`radius`, `size`, `gap`, `min-width` — is written in device-independent pixels and
scaled to each display's own, so one `height 34` is the same fraction of a 4K display
at 150 percent and a 1080p one at 100 percent, and a bar dragged between them by a
change of scaling in Settings follows without a restart. A display's scale is read
when its bar is made and again when Windows says it changed. Colours, text and
everything the layout measures are scaled together, so what is measured is what is
drawn. `dpi-scaling #false` in `bar` turns this off and the numbers are pixels as
written, which is how every version before this one read them: a config tuned in raw
pixels on a high-DPI display will find its bar half again as large the first time it
loads with the scaling on, and is one line from having it back.

```kdl
bar {
    dpi-scaling #false
    profile "default" { height 51; font-size 27 }
}
```

`extends` naming a profile that does not exist, or one declared further down the
file, is pointed out (`TAJ0029`) rather than quietly falling back to the built-in
look; so is an `edge` or `justify` that is not one of the words (`TAJ0030`), a colour
that does not parse wherever it is written (`TAJ0031`), and a `dpi-scaling` that is not
`#true` or `#false` (`TAJ0026`).

## Profiles, zones and rules

Zones are flex containers with `justify`, `gap` and `grow`; three is a convention, not
a limit. A zone that grows is also the zone that gives: when a window title is longer
than the room, the growing centre zone shrinks and the title is cut with an ellipsis
where its neighbours begin — the clock beside it keeps its width. `truncate:N` is
therefore a stylistic cap rather than a necessity. Profiles can `extend` each other,
so a slim "presentation" variant costs five lines instead of a duplicate:

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

Widgets re-render only when a value some widget on the bar actually reads has changed,
so an idle desktop does not repaint, and a source no profile shows — a clock declared
for a variant that is not in force — wakes nothing. The message loop waits rather than
polling: the model says when it changes and the loop wakes for that, with a one-second
ceiling so a missed signal can never leave the bar looking frozen. It used to run
sixty-two passes a second whatever was happening, and measured over an idle desktop it
spent more CPU than the window manager it reports on; now it spends almost none.

## Reloading

The bar re-reads its section when the window manager announces a reload, and it also
watches the file itself, so a bar being tuned with no window manager running still
follows every save. A save the window manager also announces costs one reload, not
two. A file that fails to parse leaves the bar as it was and says so in the log and in
`{{ config }}`; the stock bar is only ever the answer at startup.

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
