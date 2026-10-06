<#
.SYNOPSIS
    Hello, world, 6 of 7: tell the window manager a fact, and let the config decide
    what it means.

.DESCRIPTION
    A context is a named condition that layers changes on the config while it holds:
    different gaps, a key disarmed, a rule that only applies then. One with no `when`
    is external - nothing on the desktop decides it, so a program does. The program
    supplies a boolean; the config says what the boolean does. That split is the whole
    idea: a provider never needs to know what "hello" should change.

    Put this in the config and reload (alt+shift+r):

        contexts {
            context "hello" {
                gaps { inner 24 }                       // visibly wider gaps while it holds
                bindings { bind "alt+shift+q" { } }     // and the close key disarmed
            }
        }

    `shubbak context --set hello --hold` pins the context for as long as that command
    runs: the pin is a lease on its connection, and ending the process is letting go.
    Nothing is ever left behind by a program that crashed. (A poller would use
    `--ttl 10s` and repeat instead; see docs/scripting.md.)

.EXAMPLE
    .\examples\hello\6-context.ps1
#>
if (-not (Get-Command shubbak -ErrorAction SilentlyContinue)) { throw 'shubbak is not on the PATH. Install Shubbak, or add its folder to PATH.' }

function Show-Hello {
    # `shubbak contexts` is the human report: whether each holds, why, and who set it.
    $report = shubbak contexts 2>&1
    $line = $report | Select-String -Pattern '\bhello\b' | Select-Object -First 1
    if ($line) { "  $($line.Line.Trim())" } else { '  (the config declares no context "hello" - add the block above and reload)' }
}

'Before:'
Show-Hello

# Hidden and detached: the hold is a process of its own whose only job is to stay
# connected. Its standard input is nothing, so it does not wait on this console.
$hold = Start-Process shubbak -PassThru -WindowStyle Hidden -ArgumentList 'context', '--set', 'hello', '--hold'
Start-Sleep -Seconds 2

if ($hold.HasExited) {
    # Refused outright - the context is not declared - and it said so and left with 1.
    'The hold ended at once; the config probably declares no context "hello".'
    return
}

'Holding "hello" for 10 seconds - look at the gaps:'
Show-Hello
Start-Sleep -Seconds 10

# Letting go is ending the process. Nothing is sent; the lease dies with its connection.
Stop-Process -Id $hold.Id
Start-Sleep -Seconds 1

'After:'
Show-Hello
'Hello, world.'
