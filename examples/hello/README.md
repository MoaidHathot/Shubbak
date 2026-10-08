# Hello, world

Seven scripts, one idea each, in the order to read them. Every one runs against the
Shubbak you already have in under a minute, and none needs anything built. Together
they are every direction the pipe has: asking, telling, being told, telling the bar,
telling the config, and the wire underneath it all.

| | Script | What it teaches | Needs in the config |
|---|---|---|---|
| 1 | [`1-ask.ps1`](1-ask.ps1) | `shubbak query` answers questions in JSON | nothing |
| 2 | [`2-tell.ps1`](2-tell.ps1) | anything a key can do, a command can; ask-tell-ask | nothing |
| 3 | [`3-listen.ps1`](3-listen.ps1) | `shubbak sub` streams events; you never poll | nothing |
| 4 | [`4-signal.ps1`](4-signal.ps1) | a key bound to a `signal` reaches your program | a `bind` |
| 5 | [`5-bar.ps1`](5-bar.ps1) | `signal` the other way: your program puts a value on the bar | a `source` and a `text` |
| 6 | [`6-context.ps1`](6-context.ps1) | your program supplies a fact; the config decides what it means | a `context` |
| 7 | [`7-raw-pipe.ps1`](7-raw-pipe.ps1) | the wire itself: a named pipe and three shapes of JSON, no CLI, no package | nothing |

Then [`../Shubbak.Example.Hello`](../Shubbak.Example.Hello/) is 1, 3 and 4 again in
forty lines of C# on the `Shubbak.Ipc` package, and [`../focus-timer.ps1`](../focus-timer.ps1)
and [`../Shubbak.Example.FocusTimer`](../Shubbak.Example.FocusTimer/) are all of them
at once in one small program. [`docs/extending.md`](../../docs/extending.md) is the
page that goes with these.

## The config lines they want

Scripts 4, 5 and 6 each want a line or two in your config. All of them together, to
paste in and reload with `alt+shift+r`:

```kdl
keybindings {
    bind "alt+h" { signal "hello" }                     // 4: reaches 4-signal.ps1
}

dalil {
    action "Say hello" { signal "hello" "from" "the" "palette" }   // 4 again, as a palette row rather than a key
}

bar {
    source "hello" kind="signal"                        // 5: {{ hello }} reads what `shubbak signal hello ...` said

    profile "default" {
        zone "left"  justify="start" { workspaces }
        zone "right" justify="end"   { text template="{{ hello }}" }
    }
}

contexts {
    context "hello" {                                   // 6: external - no `when` - so a program sets it
        gaps { inner 24 }
        bindings { bind "alt+shift+q" { } }
    }
}
```

A `bar` block replaces the one you have, so merge the `source` and the `text` into
yours rather than pasting the whole thing if your bar is already set up. The others
add to what is there. The `dalil` row is the same signal as the key, reached by typing
its name in the palette rather than by a chord; `>Say hello` and Enter while
`4-signal.ps1` is running shows the words arriving.

## Trying them against a throwaway window manager

Each script talks to whichever window manager owns the pipe, which is yours. To try
them without touching it, start a second one with its own pipe and state, and run the
scripts in a shell that has the same variable:

```powershell
$env:SHUBBAK_INSTANCE = 'hello'
$env:SHUBBAK_STATE_DIR = "$env:TEMP\shubbak-hello"
shubbak-wm --config .\my-throwaway.kdl        # a config with a rule that ignores everything but one process
.\examples\hello\1-ask.ps1                     # finds the throwaway, not yours
```

See [SHUBBAK_INSTANCE](../../docs/scripting.md#a-second-shubbak-beside-the-first) for
what the two variables do and what they do not.
