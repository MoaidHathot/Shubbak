<#
.SYNOPSIS
    Hello, world, 4 of 7: a key the window manager has never heard of reaches you.

.DESCRIPTION
    `signal "<name>" [words...]` is a command the window manager carries without
    reading: it goes to whoever is subscribed to the `signal` topic, and that is all.
    Bind a key to a signal of your own, listen for it here, and you have added a key
    to Shubbak without Shubbak learning what it does. This is how the palette opens.

    Put this in the config, reload (alt+shift+r), run this, press alt+h:

        keybindings {
            bind "alt+h" { signal "hello" }
            bind "alt+shift+h" { signal "hello" "shift" "was" "held" }
        }

    `shubbak signal hello from a terminal` raises exactly the same signal, so the key
    is not required to try it. Ctrl+C to stop.

.EXAMPLE
    .\examples\hello\4-signal.ps1
#>
if (-not (Get-Command shubbak -ErrorAction SilentlyContinue)) { throw 'shubbak is not on the PATH. Install Shubbak, or add its folder to PATH.' }

'Waiting for `signal hello`; press alt+h, or run `shubbak signal hello` elsewhere. Ctrl+C to stop.'

shubbak sub signal | ForEach-Object {
    # Every signal arrives here - the palette's, the watcher's, the bar's `announce`
    # - as {"name":"...","arguments":[...]}. Yours is the one with your name on it.
    $signal = ($_ -split "`t", 2)[1] | ConvertFrom-Json

    if ($signal.name -ne 'hello') { return }

    if ($signal.arguments.Count -eq 0) { 'Hello, world.' }
    else { "Hello, world. You also said: $($signal.arguments -join ' ')" }
}
