<# .SYNOPSIS
Compare all five native binaries with the normal configuration, including idle,
application input, palette activation and repeated configuration reloads.
Only benchmark-owned deployment files/processes are changed. Final binaries are
installed and the normal session restored on every exit.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Probe,
    [Parameter(Mandatory)] [string] $CurrentRoot,
    [Parameter(Mandatory)] [string] $Baseline,
    [Parameter(Mandatory)] [string] $Output,
    [int] $IdleSeconds = 120,
    [int] $Rounds = 2
)
$ErrorActionPreference = 'Stop'
foreach ($name in @('Probe','CurrentRoot','Baseline','Output')) { Set-Variable $name ([IO.Path]::GetFullPath((Get-Variable $name -ValueOnly))) }
if (Test-Path $Output) { throw "Refusing to overwrite $Output" }
New-Item -ItemType Directory -Path $Output | Out-Null
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dist=Join-Path $root 'dist'
$cli=Join-Path $dist 'shubbak.exe'
$projects=@{ 'shubbak-wm'='Shubbak.Wm'; 'shubbak'='Shubbak.Cli'; 'taj'='Taj'; 'dalil'='Dalil'; 'ayn'='Ayn' }
function Counters {
    $values=@{}
    foreach ($p in [Diagnostics.Process]::GetProcesses()) {
        try { $values[$p.Id]=[pscustomobject]@{Id=$p.Id;Name=$p.ProcessName;CpuMs=$p.TotalProcessorTime.TotalMilliseconds;PrivateBytes=$p.PrivateMemorySize64;WorkingSet=$p.WorkingSet64;Handles=$p.HandleCount;Threads=$p.Threads.Count} }
        catch [ComponentModel.Win32Exception] { }
        catch [InvalidOperationException] { }
        finally { $p.Dispose() }
    }
    return $values
}
function Save($path,$value) { [IO.File]::WriteAllText($path,($value | ConvertTo-Json -Depth 7).Replace("`r",'')) }
function Stop-Stack {
    & $cli stop
    if ($LASTEXITCODE -ne 0) { throw 'Could not stop the normal stack.' }
}
function Start-Stack([string] $version) {
    Stop-Stack
    foreach ($name in $projects.Keys) {
        $from=if ($version -eq 'baseline') { Join-Path $Baseline "$name.exe" } else { Join-Path $CurrentRoot "$($projects[$name])/$name.exe" }
        Copy-Item -LiteralPath $from -Destination (Join-Path $dist "$name.exe") -Force
    }
    Start-Process -FilePath (Join-Path $dist 'shubbak-wm.exe') -WorkingDirectory $dist
    $ready=[Diagnostics.Stopwatch]::StartNew()
    do {
        $running=@(Get-Process -Name shubbak-wm,taj,dalil,ayn -ErrorAction SilentlyContinue)
        $ok=$running.Count -eq 4
        foreach ($p in $running) { $p.Dispose() }
        if ($ok) { break }
        if ($ready.Elapsed.TotalSeconds -gt 20) { throw 'The four resident programs did not start.' }
        Start-Sleep -Milliseconds 100
    } while ($true)
    Start-Sleep -Seconds 8
}
try {
    for ($round=0; $round -lt $Rounds; $round++) {
        $versions=@('baseline','current')
        if ($round % 2) { [Array]::Reverse($versions) }
        foreach ($version in $versions) {
            Start-Stack $version
            $case=Join-Path $Output "$version-r$round"
            New-Item -ItemType Directory -Path $case | Out-Null
            Save (Join-Path $case 'binaries.json') @(Get-FileHash ($projects.Keys | ForEach-Object {Join-Path $dist "$_.exe"}) | Select-Object Path,Hash)
            & $cli diagnose -o (Join-Path $case 'before.md')
            if ($LASTEXITCODE -ne 0) { throw 'Initial diagnosis failed.' }
            $before=Counters
            $watch=[Diagnostics.Stopwatch]::StartNew()
            Start-Sleep -Seconds $IdleSeconds
            $seconds=$watch.Elapsed.TotalSeconds
            $after=Counters
            $rows=foreach ($id in $after.Keys) {
                if ($before.ContainsKey($id)) {
                    $p=$after[$id]
                    $cpu=$p.CpuMs-$before[$id].CpuMs
                    if ($cpu -gt 0 -or $p.Name -in @('shubbak-wm','taj','dalil','ayn')) {
                        [pscustomobject]@{Name=$p.Name;Id=$id;Seconds=$seconds;CpuMs=$cpu;PercentOneCore=$cpu/($seconds*10);PrivateBytes=$p.PrivateBytes;WorkingSet=$p.WorkingSet;Handles=$p.Handles;Threads=$p.Threads}
                    }
                }
            }
            Save (Join-Path $case 'idle.json') @($rows | Sort-Object CpuMs -Descending)
            $inputResult=& $Probe receive --samples 20000 --warmup 1000 --interval-us 2000 --label "normal-$version-r$round" --raw-output (Join-Path $case 'events.csv')
            if ($LASTEXITCODE -ne 0) { throw "Normal-session input failed: $inputResult" }
            [IO.File]::WriteAllText((Join-Path $case 'input.json'),($inputResult -join "`n"))
            $palette=Get-Process -Name dalil
            try { $paletteResult=& $Probe palette-ui --target-pid $palette.Id --operations 100 }
            finally { $palette.Dispose() }
            if ($LASTEXITCODE -ne 0) { throw "Palette activation failed: $paletteResult" }
            [IO.File]::WriteAllText((Join-Path $case 'palette.json'),($paletteResult -join "`n"))
            Save (Join-Path $case 'before-reloads.json') @((Counters).Values | Where-Object Name -in @('shubbak-wm','taj','dalil','ayn'))
            foreach ($batch in 1..4) {
                foreach ($i in 1..10) {
                    & $cli wm-reload-config *> $null
                    if ($LASTEXITCODE -ne 0) { throw 'Reload failed.' }
                    Start-Sleep -Milliseconds 300
                }
                Start-Sleep -Seconds 2
                Save (Join-Path $case "reloads-$($batch*10).json") @((Counters).Values | Where-Object Name -in @('shubbak-wm','taj','dalil','ayn'))
            }
            & $cli wm-suspend *> $null
            if ($LASTEXITCODE -ne 0) { throw 'Suspend failed.' }
            $suspendedBefore=Counters
            $suspendedWatch=[Diagnostics.Stopwatch]::StartNew()
            Start-Sleep -Seconds 30
            $suspendedAfter=Counters
            $suspendedRows=foreach ($id in $suspendedAfter.Keys) {
                if ($suspendedBefore.ContainsKey($id) -and $suspendedAfter[$id].Name -in @('shubbak-wm','taj','dalil','ayn')) {
                    [pscustomobject]@{Name=$suspendedAfter[$id].Name;CpuMs=$suspendedAfter[$id].CpuMs-$suspendedBefore[$id].CpuMs;Seconds=$suspendedWatch.Elapsed.TotalSeconds;PrivateBytes=$suspendedAfter[$id].PrivateBytes;Handles=$suspendedAfter[$id].Handles}
                }
            }
            Save (Join-Path $case 'suspended.json') @($suspendedRows)
            $suspendedInput=& $Probe receive --samples 4000 --warmup 0 --label "suspended-$version-r$round"
            if ($LASTEXITCODE -ne 0) { throw "Suspended input failed: $suspendedInput" }
            [IO.File]::WriteAllText((Join-Path $case 'suspended-input.json'),($suspendedInput -join "`n"))
            & $cli wm-resume *> $null
            if ($LASTEXITCODE -ne 0) { throw 'Resume failed.' }
            $resumedInput=& $Probe receive --samples 4000 --warmup 0 --label "resumed-$version-r$round"
            if ($LASTEXITCODE -ne 0) { throw "Resumed input failed: $resumedInput" }
            [IO.File]::WriteAllText((Join-Path $case 'resumed-input.json'),($resumedInput -join "`n"))
            & $cli diagnose -o (Join-Path $case 'after.md')
            if ($LASTEXITCODE -ne 0) { throw 'Final diagnosis failed.' }
            Write-Output "$version r$round completed idle, input, 100 palette activations and 40 reloads."
        }
    }
}
finally { Start-Stack 'current' }
