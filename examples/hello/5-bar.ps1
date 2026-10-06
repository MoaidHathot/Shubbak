<#
.SYNOPSIS
    Hello, world, 5 of 7: put something on the bar.

.DESCRIPTION
    The same `signal`, the other way round: a bar `source` of kind `signal` reads its
    value from whatever signal of that name last said. `shubbak signal hello "Hello,
    world"` makes `{{ hello }}` read "Hello, world"; a signal with no words makes it
    empty, and a widget whose template renders empty hides itself. No timer, no
    script started by the bar - your program says the value when it has one.

    Put this in the config and reload (alt+shift+r):

        bar {
            source "hello" kind="signal"

            profile "default" {
                zone "left"  justify="start" { workspaces }
                zone "right" justify="end"   { text template="{{ hello }}" }
            }
        }

    Then watch the right end of the bar while this runs.

    One convention worth knowing: a bar that connects after a value was last said
    raises `signal "announce"`, and a publisher that hears it says its value again -
    see 4-signal.ps1 for the listening half, and focus-timer.ps1 for both at once.

.EXAMPLE
    .\examples\hello\5-bar.ps1
#>
if (-not (Get-Command shubbak -ErrorAction SilentlyContinue)) { throw 'shubbak is not on the PATH. Install Shubbak, or add its folder to PATH.' }

# Several words are joined with a space by the bar; quoting keeps them as one here.
shubbak signal hello 'Hello, world'
if ($LASTEXITCODE -ne 0) { return }
'The bar reads "Hello, world" ... (5 s)'
Start-Sleep -Seconds 5

shubbak signal hello 'Counting down: 3'
Start-Sleep -Seconds 1
shubbak signal hello 'Counting down: 2'
Start-Sleep -Seconds 1
shubbak signal hello 'Counting down: 1'
Start-Sleep -Seconds 1

# No words: the value is empty, and the widget hides.
shubbak signal hello
'... and now it is gone.'
