<#
.SYNOPSIS
    Hello, world, 7 of 7: the wire itself, with no shubbak command and no .NET
    package - a named pipe, three shapes of JSON, one line each.

.DESCRIPTION
    Everything the six scripts before this did went through `shubbak`, and the C#
    examples go through the Shubbak.Ipc package. Neither is required. The pipe is
    `\\.\pipe\shubbak-v2-<your SID>`, and over it:

        a request   {"method":"query","payload":"focused","id":1}
        a reply     {"id":1,"ok":true,"data":"<json as a string>"}   or  {"id":1,"ok":false,"error":"..."}
        an event    {"topic":"window.focused","data":"<json as a string>"}

    `payload` and `data` are strings, usually strings containing JSON. A reply carries
    the `id` you sent; an event has a `topic` and no `id`. One connection does both:
    after `subscribe`, requests are still answered, written whole between the events.
    That is the entire protocol, and anything that can open a pipe can speak it - this
    uses .NET's pipe class because PowerShell has it to hand, and nothing else.

    `SHUBBAK_INSTANCE` is honoured the way the window manager honours it, so this
    finds a throwaway daemon started with it, and the real one otherwise. Needs
    nothing in the config.

.EXAMPLE
    .\examples\hello\7-raw-pipe.ps1
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---- the pipe's name --------------------------------------------------------------

# Per account, by SID: two people logged into one machine each drive their own window
# manager. The protocol version is in the name, so a client and a daemon that disagree
# about the wire fail to find each other rather than misread each other.
$sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$instance = $env:SHUBBAK_INSTANCE
$suffix = if ($instance -and ($instance.Trim() -match '^[A-Za-z0-9.-]{1,32}$')) { "-$($instance.Trim())" } else { '' }
$pipeName = "shubbak-v2-$sid$suffix"

"pipe: \\.\pipe\$pipeName"

# ---- connect ----------------------------------------------------------------------

$pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', $pipeName, [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::Asynchronous)

try {
    $pipe.Connect(2000)
}
catch [System.TimeoutException] {
    Write-Error 'no window manager is running (the pipe did not answer within 2 s)'
    return
}

$utf8 = [System.Text.UTF8Encoding]::new($false)
$reader = [System.IO.StreamReader]::new($pipe, $utf8, $false, 4096, $true)
$writer = [System.IO.StreamWriter]::new($pipe, $utf8, 4096, $true)
$writer.AutoFlush = $true

$script:nextId = 0

# The two halves of the conversation, each shown as it goes over the wire. Shown with
# Write-Host rather than emitted, because in PowerShell everything a function emits
# is its return value, and these return the id and the parsed reply alone.
function Send-Request([string] $method, [string] $payload) {
    # One JSON object, one line. The id is whatever you like; the reply echoes it.
    $script:nextId++
    $request = @{ method = $method; payload = $payload; id = $script:nextId } | ConvertTo-Json -Compress
    Write-Host "-> $request"
    $writer.WriteLine($request)
    return $script:nextId
}

function Read-Line {
    $line = $reader.ReadLine()
    if ($null -eq $line) { throw 'the window manager closed the connection' }
    Write-Host "<- $line"
    return ($line | ConvertFrom-Json)
}

try {
    # 1. ping: the cheapest question there is, and the proof the pipe is Shubbak's.
    $id = Send-Request 'ping' $null
    $reply = Read-Line
    if (-not $reply.ok -or $reply.id -ne $id) { throw "unexpected reply to ping: $reply" }
    "   the window manager says $($reply.data)"
    ''

    # 2. query focused: `data` is a string that contains JSON, so it is parsed twice -
    #    once as the reply, once as the window.
    $id = Send-Request 'query' 'focused'
    $reply = Read-Line
    $window = if ($reply.data -eq 'null') { $null } else { $reply.data | ConvertFrom-Json }
    if ($window) { "   focused: ""$($window.title)"" ($($window.process_name), $($window.state))" } else { '   focused: nothing Shubbak manages' }
    ''

    # 3. subscribe, then keep asking: the one connection carries both. The reply to
    #    the subscription comes first; then events, with any later reply written whole
    #    between them and told apart by having an `id` rather than a `topic`.
    $id = Send-Request 'subscribe' 'window.focused,signal'
    $reply = Read-Line
    if (-not $reply.ok) { throw "subscription refused: $($reply.error)" }
    ''

    $id = Send-Request 'ping' $null
    'Subscribed. Change focus, press a key bound to a signal, or run `shubbak signal hello` - the first two events are printed, then this leaves. (30 s)'

    $heard = 0
    $deadline = (Get-Date).AddSeconds(30)
    $pending = $null

    while ($heard -lt 2 -and (Get-Date) -lt $deadline) {
        # A read with a timeout, so the wait is bounded; the pipe itself never times out.
        if (-not $pending) { $pending = $reader.ReadLineAsync() }
        if (-not $pending.Wait(500)) { continue }

        $line = $pending.Result
        $pending = $null
        if ($null -eq $line) { throw 'the window manager closed the connection' }

        $message = $line | ConvertFrom-Json

        if ($message.PSObject.Properties['topic']) {
            $heard++
            "<- event  $($message.topic)  $($message.data)"
        }
        else {
            "<- reply  id $($message.id) ok=$($message.ok) data=$($message.data)   (the ping sent after subscribing, answered between the events)"
        }
    }

    if ($heard -eq 0) { '   nothing happened in 30 s - that is fine; the connection and the subscription were the point.' }
    ''
    'Hello, world: that was the whole protocol.'
}
finally {
    # Closing the pipe ends the subscription and releases anything leased on this
    # connection. Nothing needs to be sent.
    $reader.Dispose()
    $writer.Dispose()
    $pipe.Dispose()
}
