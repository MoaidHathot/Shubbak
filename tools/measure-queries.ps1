<# .SYNOPSIS
Compare companion reads against isolated native daemons, with equal operation counts.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Probe,
    [Parameter(Mandatory)] [string] $Current,
    [Parameter(Mandatory)] [string] $Previous,
    [Parameter(Mandatory)] [string] $Output,
    [string] $SessionDirectory = (Join-Path $PSScriptRoot '../dist'),
    [switch] $LegacyPrevious,
    [ValidateRange(1, 10)] [int] $Rounds = 4,
    [ValidateRange(1, 100000)] [int] $Operations = 200,
    [string[]] $Kinds = @('bar', 'palette')
)
$ErrorActionPreference = 'Stop'
foreach ($name in @('Probe','Current','Previous','Output')) { Set-Variable $name ([IO.Path]::GetFullPath((Get-Variable $name -ValueOnly))) }
if (Test-Path $Output) { throw "Refusing to overwrite $Output" }
New-Item -ItemType Directory -Path $Output | Out-Null
$SessionDirectory = [IO.Path]::GetFullPath($SessionDirectory)
$cli = Join-Path $SessionDirectory 'shubbak.exe'
$meta = @{ Probe=(Get-FileHash $Probe).Hash; Current=(Get-FileHash $Current).Hash; Previous=(Get-FileHash $Previous).Hash; Operations=$Operations; LegacyPrevious=[bool]$LegacyPrevious; StartedUtc=[DateTime]::UtcNow.ToString('O') }
[IO.File]::WriteAllText((Join-Path $Output 'metadata.json'), ($meta | ConvertTo-Json).Replace("`r", ''))
function Counters {
    $values = @{}
    foreach ($p in [Diagnostics.Process]::GetProcesses()) {
        try { $values[$p.Id] = @{Name=$p.ProcessName; CpuMs=$p.TotalProcessorTime.TotalMilliseconds} }
        catch [ComponentModel.Win32Exception] { }
        catch [InvalidOperationException] { }
        finally { $p.Dispose() }
    }
    return $values
}
& $cli stop
if ($LASTEXITCODE -ne 0) { throw 'Could not stop the normal session.' }
try {
    foreach ($kind in $Kinds) {
        for ($round = 0; $round -lt $Rounds; $round++) {
            $versions = @('previous','current')
            if ($round % 2) { [Array]::Reverse($versions) }
            foreach ($version in $versions) {
                $instance = "query-$([Guid]::NewGuid().ToString('N').Substring(0,12))"
                $state = Join-Path $Output "$kind-$version-r$round"
                New-Item -ItemType Directory -Path $state | Out-Null
                $config = Join-Path $state 'config.kdl'
                [IO.File]::WriteAllText($config, "general { reload-on-save #false; }`nworkspaces { workspace `"probe`"; }`nrules { rule `"ignore desktop`" { match { !process `"ShubbakPerformanceNeverMatches`"; } do { ignore; } } }`n")
                $binary = if ($version -eq 'previous') { $Previous } else { $Current }
                $info = [Diagnostics.ProcessStartInfo]::new($binary)
                $info.UseShellExecute=$false
                $info.CreateNoWindow=$true
                $info.RedirectStandardOutput=$true
                $info.RedirectStandardError=$true
                $info.Environment['SHUBBAK_INSTANCE']=$instance
                $info.Environment['SHUBBAK_STATE_DIR']=$state
                $info.ArgumentList.Add('--config')
                $info.ArgumentList.Add($config)
                $daemon=[Diagnostics.Process]::Start($info)
                $stdout=$daemon.StandardOutput.ReadToEndAsync()
                $stderr=$daemon.StandardError.ReadToEndAsync()
                $oldInstance=$env:SHUBBAK_INSTANCE
                try {
                    $env:SHUBBAK_INSTANCE=$instance
                    $ready=[Diagnostics.Stopwatch]::StartNew()
                    do {
                        & $cli status *> $null
                        if ($LASTEXITCODE -eq 0) { break }
                        if ($daemon.HasExited -or $ready.Elapsed.TotalSeconds -gt 15) { throw 'Isolated daemon did not start.' }
                        Start-Sleep -Milliseconds 50
                    } while ($true)
                    # The older consolidation experiment intentionally did different
                    # work. Runtime comparisons must ask both daemons the same query.
                    $legacy = $LegacyPrevious -and $version -eq 'previous'
                    $query = if ($kind -eq 'bar') { if ($legacy) {'state'} else {'bar-state'} } else { if ($legacy) {'legacy-palette'} else {'palette'} }
                    $before = Counters
                    $text = & $Probe perf --kind query --query $query --instance $instance --target-pid $daemon.Id --operations $Operations
                    if ($LASTEXITCODE -ne 0) { throw "Query probe failed: $text" }
                    $after = Counters
                    $ambient = foreach ($id in $after.Keys) {
                        if ($before.ContainsKey($id)) {
                            $cpu = $after[$id].CpuMs - $before[$id].CpuMs
                            if ($cpu -gt 0) { [pscustomobject]@{Id=$id; Name=$after[$id].Name; CpuMs=$cpu} }
                        }
                    }
                    [IO.File]::WriteAllText((Join-Path $state 'result.json'), ($text -join "`n"))
                    $r=$text | ConvertFrom-Json
                    $record=@{Kind=$kind;Version=$version;Round=$round;Result=$r;Ambient=@($ambient | Sort-Object CpuMs -Descending | Select-Object -First 12)}
                    [IO.File]::AppendAllText((Join-Path $Output 'results.jsonl'), ($record | ConvertTo-Json -Depth 5 -Compress) + "`n")
                    Write-Output ("{0}/{1}/r{2}: p50={3:F3} ms p99={4:F3}, target CPU={5:F1} ms, client B/op={6:F0}" -f $kind,$version,$round,$r.Latency.P50Ms,$r.Latency.P99Ms,$r.TargetCpuMs,$r.BytesPerOperation)
                }
                finally {
                    if (-not $daemon.HasExited) { & $cli wm-exit *> $null }
                    if (-not $daemon.WaitForExit(10000)) { $daemon.Kill($true); $daemon.WaitForExit(5000) | Out-Null }
                    [IO.File]::WriteAllText((Join-Path $state 'daemon.log'), ($stdout.Result + $stderr.Result).Replace("`r", ''))
                    $daemon.Dispose()
                    $env:SHUBBAK_INSTANCE=$oldInstance
                }
            }
        }
    }
}
finally { Start-Process -FilePath (Join-Path $SessionDirectory 'shubbak-wm.exe') -WorkingDirectory $SessionDirectory }
