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

Prefixes are yours to move: `dalil { prefixes { layouts "^" } }`. Keep them to
punctuation - a letter or a digit as a prefix would turn the first keystroke of every
search into a change of mode, and the palette says so (`DAL0018`) if you try.

Inside the palette: type to filter, up/down or Ctrl+N/Ctrl+P to move, Tab to change
mode, Enter to act, Escape or Alt+F4 to dismiss. The mouse works too. The search box
is a text field with the manners of one: Left/Right and Home/End move the caret by
whole characters - an emoji or a letter with its mark is one step and one Backspace -
**Ctrl+V** or **Shift+Insert** pastes (a copied line with its newline becomes one
line), and **Ctrl+A** selects what you typed so the next key replaces it. `?` lists
every key.

## Searching

The matcher is a subsequence matcher with opinions: every letter you type must appear
in order, and the score comes from *where* it lands. Letters at the start of a word,
after a separator or at a camel-case boundary are worth far more than letters in the
middle of one - that is what an abbreviation is made of - and an unbroken run is worth
more than the same letters scattered, so a prefix beats a coincidence and `dsc` finds
Discord. Of every way the letters could be placed, the best one is taken: `st` lights
the **St** of *Visual Studio*, not the *s* of Visual and the *t* of Studio.

Letters are compared folded. Case does not matter; nor do accents, so `cafe` finds
*Café*; nor do the Arabic spellings of one sound - the alifs with and without hamza,
`ة` and `ه`, `ى` and `ي`, the Persian kaf and yeh and the Arabic ones - so `احمد` finds
*أحمد*, and vowel marks are stepped over on both sides, so `محمد` finds *مُحَمَّد*. A
title that reads right to left is drawn whole rather than highlighted, because placing
a colour inside shaped, reordered text needs the shaper's own positions and a title
read correctly beats one underlined and unreadable.

A space separates words that must all match, in any order: `code proj` finds *My
Project - Visual Studio Code*. The space you have just typed before your next word
is not yet a word and does not empty the list.

**The command list learns.** What you run from it is remembered - a count per verb or
action that halves every fortnight - and before you type, the list is in that order:
the command you run every day is at the top, the hundred you never touch are
alphabetical below it. Once you type, the match decides and the history only settles
ties. The record is `%LOCALAPPDATA%\Shubbak\dalil-frecency.tsv`, one line per command,
and deleting it forgets everything.

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
in the dim text, so you do not have to open anything to find out why. The icons come
from the window manager over the pipe — the same [`window-icon`](scripting.md#asking)
the bar uses — fetched on the background thread that reads the list, once per window
the palette has not seen, so a window that never set an icon still shows the one its
taskbar button does, and the palette itself never sends a message to a window it
lists.

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
and scaled to whichever monitor the palette opens on - and stay scaled through a
reload of the file. `shubbak check-config` reports a misspelt setting here with a
line, a column and a caret, like everywhere else, and a prefix that is a letter or a
digit with `DAL0018`.

An empty list says why it is empty in the words of the mode it is in: an empty
scratchpad says nothing is stashed and how to stash something, an empty inspect list
says every window is managed, and only a list the window manager fills - layouts,
displays, commands - says it is still waiting for an answer. A window manager that
cannot be reached says that, whatever the mode.

## When the window manager goes away

After `wm-exit` the palette stays and reconnects when the window manager returns; its
search box says it cannot be reached meanwhile. That is for restarting the window
manager. After `exit-all` - which is what the tray's Exit and the starter config's
`alt+shift+e` run - it leaves with everything else. `shubbak dalil-exit` closes it on
its own, and `shubbak stop` closes it along with everything else from outside, window
manager or no window manager. Only one palette runs per account.
