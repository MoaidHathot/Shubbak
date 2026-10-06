<#
.SYNOPSIS
    Hello, world, 3 of 7: listen to what the window manager sees.

.DESCRIPTION
    `shubbak sub <topics>` subscribes and prints one line per event for as long as it
    runs: the topic, a tab, and the event's JSON. Nothing is polled - the window
    manager says when something changes, and your program sleeps in between. Thirty
    topics; `shubbak sub` with none listed prints them all, and subscribing to a topic
    that does not exist is refused with the list of ones that do.

    This follows the focus: move between windows and watch. Ctrl+C to stop. Needs
    nothing in the config.

.EXAMPLE
    .\examples\hello\3-listen.ps1
#>
if (-not (Get-Command shubbak -ErrorAction SilentlyContinue)) { throw 'shubbak is not on the PATH. Install Shubbak, or add its folder to PATH.' }

'Following the focus; Ctrl+C to stop.'

shubbak sub window.focused | ForEach-Object {
    # topic <tab> json. The payload of window.focused is the window, or `null` when
    # the focus went to something Shubbak does not manage.
    $json = ($_ -split "`t", 2)[1]
    $window = $json | ConvertFrom-Json

    if ($null -eq $window) { '  (focus left the managed windows)' }
    else { "  $($window.process_name.PadRight(16)) $($window.title)" }
}
