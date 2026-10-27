<# .SYNOPSIS
Equal-work native comparisons with reversed ordering and ambient process CPU evidence.
Do not run builds/tests concurrently. Private memory is a snapshot, not an allocation rate.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Probe,
    [string] $Previous,
    [Parameter(Mandatory)] [string] $Output,
    [ValidateRange(1, 20)] [int] $Rounds = 4,
    [ValidateRange(1, 1000000)] [int] $Operations = 20000,
    [string[]] $Kinds = @('ipc', 'layout', 'resize')
)
$ErrorActionPreference = 'Stop'
if (Test-Path $Output) { throw "Refusing to overwrite $Output" }
New-Item -ItemType Directory -Path $Output | Out-Null
$Probe = [IO.Path]::GetFullPath($Probe)
if ($Previous) { $Previous = [IO.Path]::GetFullPath($Previous) }
$meta = @{ Probe=(Get-FileHash $Probe).Hash; Previous=$(if ($Previous) { (Get-FileHash $Previous).Hash }); StartedUtc=[DateTime]::UtcNow.ToString('O'); Operations=$Operations }
[IO.File]::WriteAllText((Join-Path $Output 'metadata.json'), ($meta | ConvertTo-Json).Replace("`r", ''))
function Processes {
    $snapshot = @{}
    foreach ($p in [Diagnostics.Process]::GetProcesses()) {
        try { $snapshot[$p.Id] = @{ Name=$p.ProcessName; Cpu=$p.TotalProcessorTime.TotalMilliseconds } }
        catch [ComponentModel.Win32Exception] { }
        catch [InvalidOperationException] { }
        finally { $p.Dispose() }
    }
    return $snapshot
}
foreach ($kind in $Kinds) {
    for ($round = 0; $round -lt $Rounds; $round++) {
        $versions = if ($Previous) { @('previous','current') } else { @('current') }
        if ($round % 2) { [Array]::Reverse($versions) }
        foreach ($version in $versions) {
            $binary = if ($version -eq 'previous') { $Previous } else { $Probe }
            $before = Processes
            $start = [Diagnostics.Stopwatch]::StartNew()
            $text = & $binary perf --kind $kind --operations $Operations --windows 32
            if ($LASTEXITCODE -ne 0) { throw "Failed $kind $version`: $text" }
            $elapsed = $start.Elapsed.TotalSeconds
            $after = Processes
            $ambient = foreach ($id in $after.Keys) {
                if ($before.ContainsKey($id)) {
                    $delta = $after[$id].Cpu - $before[$id].Cpu
                    if ($delta -gt 0) { [pscustomobject]@{ Id=$id; Name=$after[$id].Name; CpuMs=$delta } }
                }
            }
            $result = $text | ConvertFrom-Json
            $record = @{ Kind=$kind; Version=$version; Round=$round; Result=$result; WallSeconds=$elapsed; Ambient=@($ambient | Sort-Object CpuMs -Descending | Select-Object -First 12) }
            [IO.File]::AppendAllText((Join-Path $Output 'results.jsonl'), ($record | ConvertTo-Json -Depth 6 -Compress) + "`n")
            Write-Output ("{0}/{1}/r{2}: {3:F0} B/op, {4:F4} CPU ms/op, {5:F3}s, checksum {6}" -f $kind,$version,$round,$result.BytesPerOperation,$result.CpuMsPerOperation,$result.Seconds,$result.Checksum)
        }
    }
}
