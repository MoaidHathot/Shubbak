# Configuration

One KDL file drives the window manager, the bar, the palette and the watcher. This
page is the reference for it: where it lives, what goes in it, and how each part
behaves. The fully annotated example, every setting with the reasoning beside it, is
[`shubbak.example.kdl`](shubbak.example.kdl); the short starter that
`shubbak config init` writes is the one to inherit from.

## Where the file lives

Shubbak looks for it in this order, first match wins:

1. `--config <path>`
2. `$SHUBBAK_CONFIG` — a file, or a directory containing `shubbak.kdl`
3. `$XDG_CONFIG_HOME/shubbak/shubbak.kdl`
4. each entry of `$XDG_CONFIG_DIRS`
5. `%USERPROFILE%\.config\shubbak\shubbak.kdl`
6. `%APPDATA%\shubbak\shubbak.kdl`

Yes, XDG on Windows. The spec is nominally Unix, but if you keep your dotfiles in a
repo and symlink them per machine, you already have `XDG_CONFIG_HOME` set — and
every tool that ignores it makes you learn one more bespoke environment variable.

The window manager, the CLI, the bar, the palette and the watcher all share one
resolver, so they cannot disagree about which file is loaded. `shubbak config-path`
prints the file in effect and how it was found, or lists everywhere it looked.

Without a config the window manager runs on defaults: every window is tiled and no
key is bound. `shubbak config init` writes a starter where the resolver will find it.

## The file talks back

Instead of failing silently — you press the key, nothing happens, and you go hunting —
Shubbak reads the whole file up front and tells you exactly where you went wrong,
with a line, a column and a caret:

```
shubbak.kdl:8:20: error SHB0305: Unknown command 'focuss'.
  8 |     bind "alt+h" { focuss --direction left }
    |                    ^^^^^^^^^^^^^^^^^^^^^^^^
  hint: Did you mean 'focus'?
```

It also warns about the mistakes that *look* fine. A real one from the author's own
config: a regex wrapped in `/slashes/`, which Windows matches literally, so the rule
had never once fired, and nothing had ever said so. Shubbak warns and prints the
corrected pattern. Same for duplicate bindings, unknown settings, unknown sections,
and rules that would match every window on your desktop.

You can validate before you reload:

```
shubbak check-config
```

It checks the whole file, not just the window manager's part of it — the `bar`,
`dalil` and `ayn` sections too, so a misspelt setting in any of them is reported here
rather than being accepted and quietly doing nothing.

And it tells you without being asked. Each process writes what is wrong with the part
it reads to its own log, so a mistake made at logon is not something you have to go
looking for; the bar's `{{ config }}` indicator appears when its settings could not be
read, and `>config` in the palette lists the palette's own. **A file that won't parse
never replaces what's already running** — the bar, the palette and your keybindings all
keep working on the last good config rather than reverting to stock.

## Reloading

Reloading is explicit: `wm-reload-config`, from a keybinding, the CLI or the tray icon.
The window manager re-reads its part and tells the bar and the palette to re-read
theirs. Nothing watches your file behind your back, so a half-saved config cannot take
your desktop with it.

## The sections

| Section | What goes in it |
|---|---|
| `general` | Behaviour: initial window state, default layout, hide method, startup commands |
| `gaps` | `inner`, and `outer` per side |
| `window-effects` | Focused / unfocused / floating border colours |
| `animation` | `enabled`, `fps`, `minimum-distance`, and per-event duration + curve |
| `logging` | `level`, `file`, `console` |
| `workspaces` | Names, display names, monitor binding, starting layout |
| `monitor` | A display named by what it is, for workspaces and commands to refer to |
| `contexts` | Named conditions on the desktop that layer overrides on the config while they hold |
| `keybindings` | `bind`, and `for-each` |
| `binding-modes` | Modal keymaps, i3-style |
| `app` | Reusable named matchers you reference from rules |
| `rules` | Match windows, run commands |
| `bar` | [Taj](taj.md) — sources, profiles, zones, widgets |
| `dalil` | [Dalil](dalil.md) — the command palette's appearance, behaviour and actions |
| `ayn` | [Ayn](ayn.md) — which facts the watcher supplies, and as which contexts |

Both `colour` and `color` are accepted, everywhere. Settings can be written as a
child node or a property, whichever reads better to you.

### A taste of it

```kdl
general {
    initial-window-state "tiling"
    default-layout "splith"
    toggle-workspace-on-refocus #true

    startup-command "taj"
    startup-command "dalil"
    startup-command "ayn"
}

gaps {
    inner 6
    outer { top 4; right 4; bottom 4; left 4 }
}

animation {
    enabled #true
    fps "auto"
    window-move { duration 140; curve "ease-out-expo" }
}

keybindings {
    bind "alt+h" { focus --direction left }
    bind "alt+l" { focus --direction right }
    bind "alt+v" { toggle-tiling-direction }
    bind "alt+f" { toggle-floating }
    bind "alt+shift+q" { close }
}
```

`startup-command` runs whatever it is given when the window manager starts. A bare
name is looked for beside `shubbak-wm.exe` first and then on `PATH`, which is how the
bar, the palette and the watcher are started; `shell-exec <anything>` runs anything
else through the shell.

## Keybindings

`bind "<chord>" { <commands> }`. The chord is modifiers and a key, `alt+shift+h`; the
body is one or more [commands](#commands), the same ones a rule, the CLI or the palette
use. A binding with no commands is a warning here — pressing it would do nothing —
and a tool inside a context's `bindings`, where laying an empty binding over a key is
how the key is disarmed while the context holds.

### `for-each` — stop copy-pasting

Nineteen workspaces is forty near-identical lines in most window managers, and every
one of them is a chance to typo a number. Here it is six lines that cannot drift out
of sync with the workspace list:

```kdl
workspaces {
    workspace "1"
    workspace "2"
    workspace "3"
}

keybindings {
    for-each "workspace" {
        bind "alt+{name}"       { focus --workspace "{name}" }
        bind "alt+shift+{name}" { move --workspace "{name}" --focus }
    }
}
```

### Binding modes

A `binding-modes` block declares named keymaps that replace the default set while
active, i3-style: `wm-enable-binding-mode "resize"` switches into one, and a binding
inside it (or `wm-disable-binding-mode`) switches back. A mode can let unbound keys
through to applications or swallow them, which is how a "pause" mode can keep the
keyboard entirely. The bar can show `{{ binding_mode }}` while one is active; the
example config makes that pill clickable to leave the mode, since a mode that swallows
keys is one the keyboard may not be able to get you out of. See the `binding-modes`
section of the example config.

## Layouts

Layout is a property of a **container**, not of a workspace, so a fibonacci region can
sit inside a columns region with no special case. Eleven of them:

`splith` `splitv` `fibonacci` `fibonacci-v` `fibonacci-mirrored` `master-left`
`master-right` `master-top` `master-bottom` `grid` `monocle`

`layout --set <name>` picks one for the focused container; `layout --cycle` walks a
short list, deliberately ordered so each one looks obviously different from the last.
`shubbak layouts` lists them, and the palette's `~` mode says what each actually does.

## Tags

A window can belong to several workspaces and show up in whichever one you are
looking at. Windows only lets a window be in one place at a time, so membership means
the window *relocates* to whichever tagged workspace you activated last — exactly what
AwesomeWM does. `tag` edits a window's tags; `sticky` puts it on every workspace; the
palette badges a tagged window with `also on <workspace>`, because a window that
relocates itself reads as a fault rather than as something that was asked for.

## Window rules

Match on `title`, `class`, `process` or `path`, with five operators each — `equals`,
`regex`, `starts-with`, `ends-with`, `contains` (symbolic forms `=` `~=` `^=` `$=`
`*=` work too). Everything is case-insensitive. Prefix a matcher with `!` to negate.

```kdl
app "browser-picture-in-picture" {
    // Raw strings need no backslash escaping, which matters for regexes.
    title regex=r"[Pp]icture.in.[Pp]icture"
    class regex=r"Chrome_WidgetWin_1|MozillaDialogClass"
}

rules {
    rule "float the PiP window" {
        match { app "browser-picture-in-picture" }
        do { float }
    }

    rule "browsers live on 2" {
        match { process regex=r"msedge|chrome|firefox" }
        do { move --workspace "2" }
    }
}
```

An `app` is a named matcher you can reuse from several rules, from contexts, and from
the bar. Rules can fire `on="manage"` (the default), `on="title-change"` or
`on="focus"`. The `do { }` block takes **any** command — it is the same parser your
keybindings use, so there is no second vocabulary to learn. `ignore` and `manage` are
the two that only make sense here: `ignore` tells Shubbak to leave a window alone,
`manage` tells it to take on a window the built-in filter passed over. Both decide
whether a window is taken on at all, so both act only on the `manage` trigger; written
under `title-change` or `focus` they do nothing, and the loader says so (`SHB0452`).

`manage` overrules the filter's *opinions* — a tool window, a window missing from
Alt+Tab, one with no title yet, one Windows says cannot be activated — and not its
*facts*: a cloaked window, a child control, the shell's own windows and a window with
no area cannot be managed by any rule. `shubbak inspect` and the palette both say which
kind a verdict is.

Every `rules { }` block in the file counts, in file order, so a block appended at the
end is simply more rules. That is what lets a tool add one for you:

```
shubbak rule list                          # every rule in force, with its line
shubbak rule add --ignore                  # click a window; a rule that ignores it
shubbak rule add 0x2041E --manage --float  # by handle, taking it on and floating it
shubbak rule add --workspace 2 --print     # show the rule instead of adding it
shubbak rule remove "ignore ms-teams"      # and take it out again
```

`rule add` composes the rule from the window's class and process, appends it to the
file as a block of its own under a `// Added by Shubbak` comment, validates the whole
file as the window manager would load it, and reloads — and refuses, leaving the file
untouched, if the file already has errors, if the rule would not load, or if the rule
runs `shell-exec` without `allow-shell-exec-over-ipc`. `rule remove` cuts the rule's
lines, takes the block with it when the rule was all it held, and prints the rule so it
can be put back. The palette does the same from a window's row; see
[Dalil](dalil.md). Every tool that does this goes through the window manager, and
`general { allow-config-edits-over-ipc #false }` turns all of them off.

Saving the file reloads it. Reload used to be entirely command-driven, so editing the
file meant remembering to press the reload key; the window manager now watches the
file and reloads a moment after any editor saves it, with the same gate as the key - a
file with errors is reported and the running configuration is kept. Turn it off with
`general { reload-on-save #false }`.

### "Why isn't this window tiling?"

Every tiling window manager on Windows passes over some windows. Almost none of them
will tell you *which* ones, or *why*. Shubbak will:

```
shubbak inspect            # click a window; get the full story in 3 seconds
shubbak inspect --all      # every top-level window, with a verdict for each
```

You get every matchable attribute of the window, whether Shubbak will manage it,
**the specific reason if it won't** and whether a rule could change that, and which of
your rules matched and what each one does — and for each `app` that did not match, the
matcher that failed. There are sixteen distinct reasons a window gets skipped and each
one explains itself in plain English. Then `shubbak rule add`, or the palette's "Write
a rule for it…", writes the rule and adds it. `inspect --all` runs entirely locally, so
it works when the window manager is not running at all. [Troubleshooting](troubleshooting.md)
lists the common verdicts.

## Monitors by name

`monitor=1` on a workspace is a position in the order Windows reports displays, and
Windows reorders that on replug, on DisplayPort wake and on a driver restart — which
is how a workspace bound to "the right-hand screen" ends up on the left one after a
dock. So a display can be named by what it *is* instead, with the same matcher shape
an `app` uses:

```kdl
monitor "laptop"     { internal }
monitor "dell-left"  { path *= "UID4355" }   // two of the same model report the same
monitor "dell-right" { path *= "UID4357" }   // name; the connector path tells them apart

workspaces {
    workspace "3" display-name="Code"   monitor="dell-left"
    workspace "/" display-name="Second" monitor="dell-right"
}

keybindings {
    bind "alt+shift+o" { move-workspace --monitor "laptop" }
}
```

`shubbak monitors` prints a definition for every attached display, ready to paste, so
the hundred characters of hexadecimal that distinguish two identical panels never
have to be typed. A workspace whose monitor is unplugged moves to a survivor, and
**moves back when the monitor returns** — the round of dragging workspaces home after
every dock is gone. Bars follow too: Taj opens one on a display that arrives and
closes the one on a display that goes, and a bar `rule` can say `monitor="laptop"` in
the same words.

## Contexts

A talk from the laptop alone, then on a projector, then docked to two monitors for a
remote session: each wants different gaps, a different bar, a safer keyboard, and the
slides somewhere else. A **context** is a named condition on the desktop that layers
overrides on the config while it holds — and stops the moment it doesn't:

```kdl
app "powerpoint-slideshow" { title regex=r"[Pp]ower[Pp]oint [Ss]lide [Ss]how" }
monitor "projector"  { !internal }
monitor "dell-right" { path *= "UID4357" }
workspaces { workspace ";" display-name="Slides" }

contexts {
    context "presenting" {
        when { window app="powerpoint-slideshow" }   // any block holding is enough
        when { system-state "presenting" }           // the Win+P / Mobility Center toggle
        linger 500                                   // ride out PowerPoint's window churn

        gaps { inner 0; outer { top 0; right 0; bottom 0; left 0 } }
        window-effects { border #false }
        animation { enabled #false }
        bindings { bind "alt+shift+q" { } }          // disarm close while on stage
        workspaces { workspace ";" monitor="projector" }
        on-enter { focus --workspace ";" }
    }

    context "docked" { when { monitor present="dell-right" } }
    context "meeting" { }                            // external: set over the pipe
    context "docked-meeting" { when { context "docked"; context "meeting" } }
}
```

Conditions are deliberately only things Shubbak already knows or Windows says about
the *session* in one call: a window present, focused or full-screen (matched with the
same `app` definitions rules use); a workspace active or focused; how many monitors,
which named ones, the Win+P topology; remote session; the shell's notification state.
Within a block every condition must hold; several blocks mean any one is enough; `!`
negates. Several contexts hold at once and cascade in declaration order. Every
override is a delta — `gaps { inner 0 }` changes the inner gap and nothing else.

What a context can change: `gaps`, `window-effects` and `animation`; `bindings`, laid
over the default table, with an empty binding disarming a key; `rules`, consulted only
while it holds; `workspaces`, re-homing a declared workspace while it holds and sending
it back after; and `on-enter` / `on-exit`, run once each way.

**The line:** Shubbak observes the desktop, not the applications. Whether the camera
is on, whether a call is up, what the calendar says — those come from *outside*, as a
context with no `when` that another program sets: `shubbak context --set meeting
--ttl 10s` from any script, or a held pipe connection with `--lease` so the fact dies
with the process that supplied it. The config says what a meeting *does*; the program
supplying the fact never needs to know. Pins beat detection (`--set`, `--clear`,
`--toggle`); `--auto` hands a context back to its conditions. [Ayn](ayn.md) is the
reference provider, and where facts about the machine rather than its windows live —
`camera-in-use`, `microphone-in-use` and `microphone-muted`, so far. It supplies
facts, and nothing else.

`shubbak contexts` says why each one is the way it is, condition by condition, and who
pinned what — the same answer `inspect` gives for a window that didn't tile.

## Saved arrangements

A demo whose windows have been dragged about wants them back where they were:

```
shubbak arrangement --save demo       # the focused workspace's tree, under a name
shubbak arrangement --restore demo    # put the windows that are here back into it
```

An arrangement records what the session file deliberately doesn't — the containers,
their layouts and their ratios, and which window sits in each leaf, by process and
class, never by title. Restoring rearranges only the windows on the workspace: one
that isn't open is left out and its share goes to its siblings; one the arrangement
never knew stays, at the end, with the share it would have had as one more child.
`shubbak arrangements` lists them, `arrangement.restored` on the event stream says how
many were placed, and the palette completes the names.

## Animation

Per-event durations and cubic-bezier curves, in the `animation` section. The
important bit: re-targeting blends from the window's *current* position, so hammering
a layout key never makes windows jump backwards or stutter. Frame rate follows your
fastest display by default (`fps "auto"`) and is re-read when monitors come and go.

## Mouse

Drag a tiled window onto the middle of another to **swap** them, or near an edge to
**insert** beside it. Drag a border to resize, and the resize is written back into the
tree's ratios, so the next layout pass respects it instead of undoing it.
`focus-follows-cursor` in `general` does what it says; `cursor-jump` moves the pointer
to the window that just took focus, on every focus change or only when it crosses
monitors.

## Suspend is different from pause

Two different things you will actually want:

- **`wm-toggle-pause`** — stop rearranging windows, keep the keyboard. For when you
  want to drag something around manually for a minute.
- **`wm-suspend`** — let go of the keyboard *entirely*, drop the hooks, stop doing
  periodic work. For when a game or a remote session wants every key you press.

Resuming from a full suspend uses a real Windows hotkey rather than a keyboard hook,
so a suspended Shubbak costs you nothing per keystroke. The bar and the tray icon
both tell you which state you are in, and both are clickable, because "suspended"
and "crashed" look identical if the only way back is the keyboard you just gave up.

## Cloaked, not hidden

Windows on inactive workspaces are **cloaked**, not hidden. A cloaked window still
reports as visible to Win32, so if Shubbak crashes, is killed, or you pull the plug,
the next run finds those windows and brings them straight back.

Hiding — what this used to do, and what a lot of tools do — is a one-way door: the
window filter rejects invisible windows, so they stay stranded with their process
still running and nothing on screen to click. `hide-method "hide"` is still there for
the one case that needs it, a remote session without a compositor.

And if it ever does go wrong, there is a fire escape that does not need the daemon:

```
shubbak restore --dry-run  # show me what you'd bring back
shubbak restore            # bring it back
```

## Commands

36 verbs, all usable from a keybinding, a rule, the CLI, the palette, or over IPC.

**Focus & movement** — `focus` `focus-window` `focus-recent-window` `move`
`move-workspace` `resize` `equalise` `split` `toggle-tiling-direction`

**Layout & state** — `layout` `float` `tile` `toggle-floating` `toggle-fullscreen`
`toggle-minimized` `close`

**Workspaces & stashing** — `tag` `sticky` `scratchpad`

**Management** — `ignore` `manage` `toggle-managed`

**Contexts** — `context` (`--set` `--clear` `--toggle` `--auto`, with `--ttl` and `--lease`)

**Arrangements** — `arrangement` (`--save` `--restore` `--delete`)

**The window manager itself** — `wm-enable-binding-mode` `wm-disable-binding-mode`
`wm-toggle-pause` `wm-suspend` `wm-resume` `wm-toggle-suspend` `wm-reload-config`
`wm-redraw` `wm-exit`

**All of Shubbak** — `exit-all`: the window manager, the bar, the palette and the
watcher. `wm-exit` stops the window manager alone and the other three wait for it to
come back, which is for restarting it; this is for being done with it, and it is what
the tray's Exit runs.

**Escape hatches** — `shell-exec` `signal`

Anything the CLI does not recognise as its own subcommand is forwarded straight to
the daemon, so `shubbak focus --direction left` just works. `shubbak query commands`
lists every verb, and the palette's `>` mode parses what you type with the real parser
before you press Enter.

## When something is wrong

```
shubbak diagnose -o report.md
```

One Markdown file: your environment, your config, the live window tree, and the
recent log, including a ring buffer that is kept even at the default log level, so
the report is still useful *after* the weird thing happened. You can also raise the
log level on the running daemon without restarting it:

```
shubbak log-level trace
```

[Troubleshooting](troubleshooting.md) is organised by symptom.
