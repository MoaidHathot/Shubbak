<# .SYNOPSIS
Compare Taj's real ProcessSource with identical quiet child programs. The child
binary is frozen across variants so its own runtime does not confound parent costs.
Run without builds/tests; record ambient CPU and keep all reversed-order results.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Probe,
    [Parameter(Mandatory)] [string] $Previous,
    [Parameter(Mandatory)] [string] $Child,
    [Parameter(Mandatory)] [string] $Output,
    [ValidateRange(1, 10)] [int] $Rounds = 4,
    [ValidateRange(1, 32)] [int] $Sources = 16,
    [ValidateRange(2, 120)] [int] $Seconds = 15
)
$ErrorActionPreference = 'Stop'
foreach ($name in @('Probe','Previous','Child','Output')) { Set-Variable $name ([IO.Path]::GetFullPath((Get-Variable $name -ValueOnly))) }
if (Test-Path $Output) { throw "Refusing to overwrite $Output" }
New-Item -ItemType Directory -Path $Output | Out-Null
function Save($path, $value) { [IO.File]::WriteAllText($path, ($value | ConvertTo-Json -Depth 8).Replace("`r", '')) }
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
Save (Join-Path $Output 'metadata.json') @{
    StartedUtc=[DateTime]::UtcNow.ToString('O'); Sources=$Sources; Seconds=$Seconds
    Probe=(Get-FileHash $Probe).Hash; Previous=(Get-FileHash $Previous).Hash; Child=(Get-FileHash $Child).Hash
}
for ($round = 0; $round -lt $Rounds; $round++) {
    $versions = @('previous','current')
    if ($round % 2) { [Array]::Reverse($versions) }
    foreach ($version in $versions) {
        $binary = if ($version -eq 'previous') { $Previous } else { $Probe }
        $before = Counters
        $text = & $binary process-sources --sources $Sources --seconds $Seconds --child $Child
        if ($LASTEXITCODE -ne 0) { throw "Invalid process-source run $version/r$round`: $text" }
        $after = Counters
        $result = $text | ConvertFrom-Json
        if (-not $result.NativeAot -or $result.Ready -ne $Sources -or $result.ChildrenRemaining -ne 0) { throw "Unqualified source measurement: $text" }
        $ambient = foreach ($id in $after.Keys) {
            if ($before.ContainsKey($id)) {
                $cpu = $after[$id].CpuMs - $before[$id].CpuMs
                if ($cpu -gt 0) { [pscustomobject]@{Id=$id; Name=$after[$id].Name; CpuMs=$cpu} }
            }
        }
        $record = @{Version=$version; Round=$round; Result=$result; Ambient=@($ambient | Sort-Object CpuMs -Descending | Select-Object -First 12)}
        Save (Join-Path $Output "$version-r$round.json") $record
        [IO.File]::AppendAllText((Join-Path $Output 'results.jsonl'), ($record | ConvertTo-Json -Depth 8 -Compress) + "`n")
        Write-Output ("{0}/r{1}: {2} sources, {3} threads, {4:F2} MiB private, {5:F2} ms idle CPU, disposal {6:F1} ms" -f $version,$round,$Sources,$result.Idle.Threads,($result.Idle.PrivateBytes/1MB),$result.IdleCpuMs,$result.DisposeMs)
    }
}
