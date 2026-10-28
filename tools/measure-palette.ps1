<# .SYNOPSIS
Longer paired resident-palette foreground checks with all normal companions running.
Run separately from local builds/tests. Restores the current native stack on exit.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Probe,
    [Parameter(Mandatory)] [string] $CurrentRoot,
    [Parameter(Mandatory)] [string] $Baseline,
    [Parameter(Mandatory)] [string] $Output,
    [ValidateRange(1, 10)] [int] $Rounds = 4,
    [ValidateRange(1, 1000)] [int] $Operations = 500
)
$ErrorActionPreference = 'Stop'
foreach ($name in @('Probe','CurrentRoot','Baseline','Output')) { Set-Variable $name ([IO.Path]::GetFullPath((Get-Variable $name -ValueOnly))) }
if (Test-Path $Output) { throw "Refusing to overwrite $Output" }
New-Item -ItemType Directory -Path $Output | Out-Null
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dist = Join-Path $root 'dist'
$cli = Join-Path $dist 'shubbak.exe'
$projects = @{ 'shubbak-wm'='Shubbak.Wm'; 'shubbak'='Shubbak.Cli'; 'taj'='Taj'; 'dalil'='Dalil'; 'ayn'='Ayn' }
function Save($path, $value) { [IO.File]::WriteAllText($path, ($value | ConvertTo-Json -Depth 7).Replace("`r", '')) }
function Counters {
    $values = @{}
    foreach ($p in [Diagnostics.Process]::GetProcesses()) {
        try { $values[$p.Id] = @{ Name=$p.ProcessName; CpuMs=$p.TotalProcessorTime.TotalMilliseconds; PrivateBytes=$p.PrivateMemorySize64 } }
        catch [ComponentModel.Win32Exception] { }
        catch [InvalidOperationException] { }
        finally { $p.Dispose() }
    }
    return $values
}
function Start-Stack([string] $version) {
    & $cli stop
    if ($LASTEXITCODE -ne 0) { throw 'Could not stop the normal stack.' }
    foreach ($name in $projects.Keys) {
        $from = if ($version -eq 'baseline') { Join-Path $Baseline "$name.exe" } else { Join-Path $CurrentRoot "$($projects[$name])/$name.exe" }
        Copy-Item -LiteralPath $from -Destination (Join-Path $dist "$name.exe") -Force
    }
    Start-Process -FilePath (Join-Path $dist 'shubbak-wm.exe') -WorkingDirectory $dist
    $ready = [Diagnostics.Stopwatch]::StartNew()
    do {
        $running = @(Get-Process -Name shubbak-wm,taj,dalil,ayn -ErrorAction SilentlyContinue)
        $ok = $running.Count -eq 4
        foreach ($p in $running) { $p.Dispose() }
        if ($ok) { break }
        if ($ready.Elapsed.TotalSeconds -gt 20) { throw 'The four resident programs did not start.' }
        Start-Sleep -Milliseconds 100
    } while ($true)
    Start-Sleep -Seconds 8
}
Save (Join-Path $Output 'metadata.json') @{StartedUtc=[DateTime]::UtcNow.ToString('O');Probe=(Get-FileHash $Probe).Hash;Operations=$Operations;Rounds=$Rounds}
try {
    for ($round = 0; $round -lt $Rounds; $round++) {
        $versions = @('baseline','current')
        if ($round % 2) { [Array]::Reverse($versions) }
        foreach ($version in $versions) {
            Start-Stack $version
            $case = Join-Path $Output "$version-r$round"
            New-Item -ItemType Directory -Path $case | Out-Null
            Save (Join-Path $case 'binaries.json') @(Get-FileHash ($projects.Keys | ForEach-Object { Join-Path $dist "$_.exe" }) | Select-Object Path,Hash)
            $config = (& $cli query config-path | ConvertFrom-Json).path
            if ($LASTEXITCODE -ne 0) { throw 'Could not query the configuration path.' }
            $hash = (Get-FileHash $config).Hash
            # Match the normal-stack fixture's input prerequisite. A background-only
            # script may lack foreground permission until its controlled receiver has
            # taken input; that refusal is not palette activation latency.
            $setup = & $Probe receive --samples 2 --warmup 0 --interval-us 2000 --label palette-foreground-prerequisite
            if ($LASTEXITCODE -ne 0) { throw "Foreground prerequisite failed: $setup" }
            [IO.File]::WriteAllText((Join-Path $case 'prerequisite.json'), ($setup -join "`n"))
            $before = Counters
            $watch = [Diagnostics.Stopwatch]::StartNew()
            $palette = Get-Process -Name dalil
            try { $text = & $Probe palette-ui --target-pid $palette.Id --operations $Operations }
            finally { $palette.Dispose() }
            $code = $LASTEXITCODE
            [IO.File]::WriteAllText((Join-Path $case 'result.json'), ($text -join "`n"))
            if ($code -ne 0) { throw "Palette measurement failed: $text" }
            $seconds = $watch.Elapsed.TotalSeconds
            $after = Counters
            if ((Get-FileHash $config).Hash -ne $hash) { throw 'Configuration changed during the paired comparison.' }
            foreach ($id in $before.Keys) {
                if ($before[$id].Name -in @('shubbak-wm','taj','dalil','ayn') -and -not $after.ContainsKey($id)) { throw 'A resident process exited during the measurement.' }
            }
            $resources = foreach ($id in $after.Keys) {
                if ($before.ContainsKey($id)) {
                    $cpu = $after[$id].CpuMs - $before[$id].CpuMs
                    if ($cpu -gt 0) { [pscustomobject]@{Id=$id;Name=$after[$id].Name;CpuMs=$cpu;PrivateBytes=$after[$id].PrivateBytes} }
                }
            }
            Save (Join-Path $case 'resources.json') @($resources | Sort-Object CpuMs -Descending)
            $result = $text | ConvertFrom-Json
            $record = @{Version=$version;Round=$round;Seconds=$seconds;Result=$result}
            [IO.File]::AppendAllText((Join-Path $Output 'results.jsonl'), ($record | ConvertTo-Json -Depth 6 -Compress) + "`n")
            Write-Output ("{0}/r{1}: n={2}, p50={3:F3} ms, p99={4:F3}, max={5:F3}" -f $version,$round,$result.Delivery.Count,$result.Delivery.P50Ms,$result.Delivery.P99Ms,$result.Delivery.MaxMs)
        }
    }
}
finally { Start-Stack 'current' }
