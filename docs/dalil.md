# Dalil — the command palette

<img src="assets/dalil.png" width="72" align="right" alt="" />

**Dalil** (دليل, *"guide"*) is a fuzzy-search palette for your whole desktop. It is
its own program, `dalil`, started by the window manager from the config
(`startup-command "dalil"`); bind a key to `signal "palette"` and it appears.

```kdl
keybindings {
    bind "alt+space" { signal "palette" }
    bind "alt+shift+space" { signal "palette" "commands" }
}

dalil {
    open-on-signal "palette"
    show-unmanaged #true
    confirm-destructive #true
}
```

Dalil is opened by a **signal**, not by a hard-wired command — which means Shubbak
does not know Dalil exists. That is the same extension point anything else can use;
see [Scripting](scripting.md).

## Modes

Eight of them. Every one has a prefix, and every one has a **Ctrl+digit** that jumps
straight to it in the order the hint bar draws them — because a prefix is faster and
`~` is a dead key on several European layouts, where it produces no character at all
until you press something else:

| Prefix | Jump | Mode | |
|---|---|---|---|
| *(none)* | `Ctrl+1` | Windows | Every window on the desktop, managed or not |
| `>` | `Ctrl+2` | Commands | Every verb, plus your own named actions |
| `#` | `Ctrl+3` | Workspaces | With window count, layout and monitor |
| `!` | `Ctrl+4` | Inspect | Every window Shubbak is **not** managing, and why not — with a row that widens it to the tool windows and popups it never lists, which are the ones a `manage` rule is for |
| `$` | `Ctrl+5` | Scratchpad | Everything you have stashed, by slot |
| `~` | `Ctrl+6` | Layouts | What each one actually does, and the one you are in |
| `%` | `Ctrl+7` | Monitors | Size, DPI, and what each is showing |
| `?` | `Ctrl+8` | Help | The palette's keys — **and your own keybindings** |

Prefixes are yours to move: `dalil { prefixes { layouts "l" } }`.

Inside the palette: type to filter, up/down or Ctrl+N/Ctrl+P to move, Tab to change
mode, Enter to act, Escape to dismiss. The mouse works too.

## What it does well

**Type a command and it is parsed for real.** Whatever you type becomes a top-ranked
row, run through the *same* parser your config file uses. So a bad argument gives
you the same message it would at load time, right there, before you press Enter.

**Mark windows and act on all of them.** `Ctrl+Space` marks; `Ctrl+Enter` then acts
on the set — move them all to one workspace, float them, close them. Doing that with
keybindings is six rounds of find-it, focus-it, move-it, with the focus landing
somewhere different after each one. This is the thing a palette is genuinely *for*.

**`shubbak inspect`, without leaving the palette.** Press **Ctrl+Shift+I** on any
window and you get the full report — attributes, verdict, which rules matched, which
app definitions missed and on which matcher. Any line too long to fit opens in full
with Enter, and Escape or Backspace steps back out. **Ctrl+C** copies the selected
line; **Ctrl+Shift+C** copies the whole report, which is the version that belongs in
a bug report.

**And then it writes the rule for you — and adds it, if you say so.** "Write a rule
for it…" opens the rules that could be written for the window, each complete and
named for what it does, best first. A managed window is offered **Ignore it** first;
a window the filter turned down is offered **Manage it** (only where a rule could
actually overrule the filter — a cloaked window is not offered one that would look
right and do nothing); a window a rule already ignores or forces is offered **Stop
ignoring it** / **Stop forcing it**, which removes that rule. Float it, tile it, send
it to a workspace, and "Match it, decide later" — the matchers written and the `do`
block left to you — are always there.

Enter on a choice reads the rule. **Ctrl+Enter** is where it leaves the palette:
**Add it to the config and reload** appends it to `shubbak.kdl` and reloads, and the
answer says what happened — *Added "ignore ms-teams" at line 412 and reloaded. "Teams"
was released.* — with **Remove it again** right under it, because it did not ask
first. **Copy the rule** and **Open the config** remain for the user who would rather
place it by hand. Inside a rule, Ctrl+C copies one line and Ctrl+Shift+C copies all of
it, and the hint bar says so.

The window manager does the writing: it validates the whole file as it would load it
and refuses rather than leave it broken, and a rule that runs `shell-exec` is refused
unless the pipe may run it directly. Every rule row in a report can be removed the same
way, and removal prints the rule so it can be put back. Nothing is guessed: the `do`
block holds what you chose, and the same window one person wants floated is one
another wants ignored. `general { allow-config-edits-over-ipc #false }` turns the
whole thing off; `shubbak rule add` and `shubbak rule remove` do the same from a
terminal. See [Configuration](configuration.md#window-rules).

**Every row has actions** (Ctrl+Enter): go to it, bring it here, send it to another
workspace, float/tile, minimise/restore, make it sticky, edit its tags, write a rule
for it, close it, start or stop managing it, and inspect it. Closing asks first —
whichever route you reached it by, chord included — and nothing else does, because
nothing else is irreversible.

## Your own actions

Keybindings are a scarce resource; palette rows are not.

```kdl
dalil {
    action "Dev layout" description="Editor left, terminal right, on 2" {
        focus --workspace "2"
        layout --set "master-left"
        equalise
    }
}
```

They are validated against the real parser at load time, so a typo is reported on the
row rather than swallowed.

**A row can ask.** A `param` turns one row into a question, so a single entry stands
in for one per workspace — nineteen of them, in the author's config, each of which
would otherwise need its own name to invent and its own line to keep:

```kdl
dalil {
    action "Send it to..." description="Move it there and stay where you are" {
        param "ws" from="workspaces"
        move --workspace "{ws}"
    }

    action "Arrange..." description="Go somewhere and lay it out, in one gesture" {
        param "ws" from="workspaces"
        param "l"  from="layouts"
        focus --workspace "{ws}"
        layout --set "{l}"
        equalise
    }
}
```

Enter opens the picker; Escape goes back one question rather than dismissing. Choices
come `from=` a list the palette already holds — `workspaces`, `layouts`,
`binding-modes`, `scratchpads`, `directions`, `contexts` — or from `values="a b c"`
when you want a set the window manager does not know. Workspaces are shown as
`3 — Code`, because a picker reading `\` is not one anybody can choose from.

The checking is real: a placeholder nothing declares is an error with a line and a
caret, a question no command asks is a warning, and `move --direction "{d}"` is probed
with an actual direction before the parser sees it rather than waved through.

**Put an action on a key without writing it twice.** `signal "palette" "run" "<name>"`
runs a named action outright and shows nothing:

```kdl
bind "alt+ctrl+d" { signal "palette" "run" "Deep work" }
```

Shubbak still has no idea what an action is — it carries the name without reading it,
and the palette is what knows. An action that *asks* cannot be answered by a key, so
those open the palette with the name already typed and the picker one Enter away.

## What the rows tell you

Rows carry the application's icon and badges so you can see at a glance what you are
looking at: `unmanaged`, `minimised`, `cloaked`, `floating`, `fullscreen`, `sticky`,
`elevated`, `stashed`, `also on <workspace>`. Unmanaged windows also carry the reason
in the dim text, so you do not have to open anything to find out why.

The search box tells you when tiling is paused, when a binding mode is eating your
keys, when the window manager has suspended itself, and when it cannot be reached at
all — because all four look exactly like a crash from the outside — and, quieter than
any of those, which contexts it holds, because the keys in force are then not the
ones in the file. Typing `context --toggle ` completes the declared names, and
`>config` lists anything wrong with the palette's own section. `>config path` copies
the path of the file in effect and `>open config` opens it, with whatever Windows
opens `.kdl` files with — the palette does not pick an editor.

## Settings

The `dalil` section, all optional: `open-on-signal` (the signal that opens it),
`width`, `row-height`, `visible-rows` (a cap on drawing, not on searching),
`close-on-blur`, `show-unmanaged`, `confirm-destructive`, `show-icons`,
`shrink-to-fit`, `placement` (`focused-monitor`, `cursor-monitor` or `primary`),
`prefixes`, and the `action` blocks above. Sizes and the font are written at 96 DPI
and scaled to whichever monitor the palette opens on. `shubbak check-config` reports a
misspelt setting here with a line, a column and a caret, like everywhere else.

## When the window manager goes away

After `wm-exit` the palette stays and reconnects when the window manager returns; its
search box says it cannot be reached meanwhile. That is for restarting the window
manager. After `exit-all` - which is what the tray's Exit and the starter config's
`alt+shift+e` run - it leaves with everything else. `shubbak dalil-exit` closes it on
its own, and `shubbak stop` closes it along with everything else from outside, window
manager or no window manager. Only one palette runs per account.
