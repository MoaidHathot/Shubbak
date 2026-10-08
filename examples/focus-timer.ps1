<#
.SYNOPSIS
    A focus timer for Shubbak, built from the command line alone: start it from a
    key, and for the next twenty-five minutes a context holds and the bar counts
    down. An example of extending Shubbak without opening the pipe yourself.

.DESCRIPTION
    Three idioms, each one `shubbak` command:

      listener   `shubbak sub signal` is the ears. A key bound to
                 `signal "focus" "start" "25"` arrives here as a line; so does
                 `signal "focus" "stop"`, and the bar's `announce`.
      provider   `shubbak context --set focusing --hold` is the hand on the context.
                 It stays running for as long as the timer does; ending the process
                 lets go, because the pin is a lease on that process's connection.
      publisher  `shubbak signal focus mm:ss` is the voice. The bar's
                 `source "focus" kind="signal"` shows it; a signal with no arguments
                 clears it.

    The config it wants is in docs/extending.md: a `context "focusing"` with whatever
    it should change, a `source "focus" kind="signal"` on the bar, and two keys - or
    two palette rows, which raise the same signals by name:

      dalil {
          action "Start focus timer" unless-context="focusing" { signal "focus" "start" "25" }
          action "Stop focus timer"  when-context="focusing"   { signal "focus" "stop" }
      }

    Tied to the context this script holds, so the palette offers "Start" while nothing
    is running and "Stop" while something is - the timer's state reaching the palette
    without this script knowing the palette exists.

    Nothing here polls. The script blocks on the event stream and wakes once a second
    only while a timer is running - and that second is how often the bar changes, so
    nothing is sent that would not be shown.

.PARAMETER DefaultMinutes
    How long a `start` with no number runs for.

.EXAMPLE
    .\examples\focus-timer.ps1
    shubbak signal focus start 25      # from another terminal, alt+f, or the palette row
    shubbak signal focus stop
#>
[CmdletBinding()]
param(
    [int] $DefaultMinutes = 25
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$shubbak = Get-Command shubbak -ErrorAction SilentlyContinue
if (-not $shubbak) { throw 'shubbak is not on the PATH. Install Shubbak, or add its folder to PATH.' }

# ---- the timer's state -------------------------------------------------------------

$endsAt = $null        # [datetime] while running, else $null
$hold = $null          # the `--hold` process while running, else $null
$lastSaid = $null      # the last value put on the bar, to repeat on `announce`

function Say([string] $value) {
    # `shubbak signal focus <value>` puts the value on the bar; with no value it clears.
    # Remembered so an `announce` - a bar that has just connected - can be answered.
    $script:lastSaid = $value
    if ($value) { & shubbak signal focus $value | Out-Null } else { & shubbak signal focus | Out-Null }
}

function Start-Focus([int] $minutes) {
    Stop-Focus -Quiet
    $script:endsAt = (Get-Date).AddMinutes($minutes)

    # The hold is its own process so the lease outlives nothing but this script's
    # decision to end it. Hidden, and its input from nowhere, so it waits on its pipe
    # and not on a console.
    $script:hold = Start-Process shubbak -PassThru -WindowStyle Hidden `
        -ArgumentList 'context', '--set', 'focusing', '--hold'

    Write-Output "focusing for $minutes min, until $($script:endsAt.ToString('HH:mm'))"
    Tick
}

function Stop-Focus([switch] $Quiet) {
    if ($script:hold) {
        # Ending the process is letting go: the pin dies with its connection.
        Stop-Process -Id $script:hold.Id -ErrorAction SilentlyContinue
        $script:hold = $null
    }

    if ($script:endsAt -and -not $Quiet) { Write-Output 'stopped' }
    $script:endsAt = $null
    Say ''
}

function Tick {
    if (-not $script:endsAt) { return }

    $left = $script:endsAt - (Get-Date)

    if ($left.TotalSeconds -le 0) {
        Write-Output 'time'
        Stop-Focus -Quiet
        return
    }

    Say ('{0:00}:{1:00}' -f [int][math]::Floor($left.TotalMinutes), $left.Seconds)
}

# ---- the ears ----------------------------------------------------------------------

# `shubbak sub signal` prints one line per signal: the topic, a tab, the JSON payload.
# Run as a child whose output is read line by line; a timeout on the read is the
# once-a-second tick while a timer runs, and no wake at all while none does.
$listener = [System.Diagnostics.Process]::new()
$listener.StartInfo.FileName = $shubbak.Source
$listener.StartInfo.ArgumentList.Add('sub')
$listener.StartInfo.ArgumentList.Add('signal')
$listener.StartInfo.UseShellExecute = $false
$listener.StartInfo.RedirectStandardOutput = $true
$listener.StartInfo.CreateNoWindow = $true
$null = $listener.Start()

Write-Output 'focus timer: waiting for `signal focus start [minutes]`; Ctrl+C to leave'

# Said once on the way in, in case a bar is already up and asked before we existed.
Say ''

try {
    $pendingRead = $null

    while (-not $listener.HasExited) {
        if (-not $pendingRead) { $pendingRead = $listener.StandardOutput.ReadLineAsync() }

        # Block on the stream while idle; while a timer runs, wake once a second.
        $wait = if ($endsAt) { 1000 } else { -1 }

        if ($pendingRead.Wait($wait)) {
            $line = $pendingRead.Result
            $pendingRead = $null
            if ($null -eq $line) { break }

            # topic <tab> {"name":"focus","arguments":["start","25"]}
            $tab = $line.IndexOf("`t")
            if ($tab -lt 0) { continue }

            $payload = $line.Substring($tab + 1) | ConvertFrom-Json
            $arguments = @($payload.arguments)

            if ($payload.name -eq 'announce') {
                # A bar connected and asked everybody to say their values again.
                Say $lastSaid
            }
            elseif ($payload.name -eq 'focus' -and $arguments.Count -gt 0) {
                # Our own values come back to us - `signal focus 24:59` is a signal like
                # any other, and we are subscribed - so only the words that are a request
                # are acted on; a value is ours, and a clear has no words at all.
                switch ($arguments[0]) {
                    'start' {
                        $minutes = $DefaultMinutes
                        $parsed = 0
                        if ($arguments.Count -gt 1 -and [int]::TryParse([string]$arguments[1], [ref]$parsed) -and $parsed -gt 0) { $minutes = $parsed }
                        Start-Focus $minutes
                    }
                    'stop' { Stop-Focus }
                    default { if ($arguments[0] -notmatch '^\d\d:\d\d$') { Write-Warning "signal focus: unknown word '$($arguments[0])'; expected start or stop" } }
                }
            }
        }
        else {
            Tick
        }
    }

    if ($listener.HasExited) { Write-Warning 'the window manager went away; leaving' }
}
finally {
    Stop-Focus -Quiet
    if (-not $listener.HasExited) { $listener.Kill() }
    $listener.Dispose()
}
