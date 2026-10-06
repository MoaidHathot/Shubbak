<#
.SYNOPSIS
    Hello, world, 2 of 7: tell the window manager to do something.

.DESCRIPTION
    Anything the command line does not recognise as its own subcommand goes to the
    window manager as a command, in exactly the words a keybinding uses. So
    `shubbak toggle-floating` is `bind "alt+shift+m" { toggle-floating }` with no key
    in between, and the same is true of every verb: focus, move, layout, gaps,
    workspace, scratchpad, signal, context ... `shubbak query commands` lists them.

    This floats the focused window - most likely the terminal you ran it from - asks
    what happened, and puts it back. Ask, tell, ask: the shape of nearly every script.
    Needs nothing in the config.

.EXAMPLE
    .\examples\hello\2-tell.ps1
#>
if (-not (Get-Command shubbak -ErrorAction SilentlyContinue)) { throw 'shubbak is not on the PATH. Install Shubbak, or add its folder to PATH.' }

function Get-FocusedState {
    $answer = shubbak query focused
    if ($LASTEXITCODE -ne 0) { throw 'the window manager did not answer' }
    $focused = $answer | ConvertFrom-Json
    if ($null -eq $focused) { throw 'nothing Shubbak manages has the focus; click a tiled window and run this again' }
    $focused
}

$before = Get-FocusedState
"""$($before.title)"" is $($before.state)."

# A command that cannot be done is refused with the reason, and the exit code says so:
# a window Shubbak does not manage, a direction with nothing in it. Nothing is thrown;
# the window manager stays exactly as it was.
shubbak toggle-floating
if ($LASTEXITCODE -ne 0) { return }

Start-Sleep -Milliseconds 1500
$after = Get-FocusedState
"Now it is $($after.state)."

shubbak toggle-floating
Start-Sleep -Milliseconds 1500
"And $((Get-FocusedState).state) again. Hello, world."
