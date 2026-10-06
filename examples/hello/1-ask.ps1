<#
.SYNOPSIS
    Hello, world, 1 of 7: ask the window manager a question.

.DESCRIPTION
    `shubbak query <what>` answers in JSON. `focused` is the window that has the
    keyboard - or `null` when none that Shubbak manages does. The other questions are
    state, windows, workspaces, monitors, layouts, contexts, rules, bindings,
    arrangements, config-path and config; `tree` answers in text.

    This is the whole of asking: one command, one JSON answer, and your program reads
    the fields it cares about. Needs nothing in the config.

.EXAMPLE
    .\examples\hello\1-ask.ps1
#>
if (-not (Get-Command shubbak -ErrorAction SilentlyContinue)) { throw 'shubbak is not on the PATH. Install Shubbak, or add its folder to PATH.' }

$answer = shubbak query focused
if ($LASTEXITCODE -ne 0) { return }    # the command line has already said why - usually "no window manager is running"

$focused = $answer | ConvertFrom-Json

if ($null -eq $focused) {
    'Nothing Shubbak manages has the focus right now.'
    return
}

"Hello, world. The focused window is:"
"  title    $($focused.title)"
"  process  $($focused.process_name)"
"  state    $($focused.state)"
"  size     $($focused.width) x $($focused.height) at ($($focused.x), $($focused.y))"
