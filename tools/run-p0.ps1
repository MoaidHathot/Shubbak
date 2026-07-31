#requires -Version 7.0
<#
.SYNOPSIS
    Runs the Shubbak P0 de-risking spike suite and captures transcripts.

.DESCRIPTION
    Produces the measurements behind docs/adr/0001-language-choice.md.

    S1  WH_KEYBOARD_LL callback latency under GC pressure   (automated)
    S2  Animation frame pacing at 144 Hz                     (automated)
    S3  NativeAOT size / startup / memory                    (automated, JIT vs AOT)
    S4  WinEvent fidelity and volume                         (INTERACTIVE)

    Each spike runs against both a JIT build and a NativeAOT build so the two
    can be compared directly. Control groups (no GC pressure, no DeferWindowPos
    batching) run alongside the real configurations so every number has a baseline.

.PARAMETER SkipAot
    Skip the NativeAOT publish and run JIT only. Useful for a fast iteration loop;
    the AOT numbers are required for the final ADR.

.PARAMETER SkipInteractive
    Skip S4, which needs a human to switch browser tabs.

.PARAMETER Quick
    Shorter runs, for validating the harness rather than producing final numbers.

.EXAMPLE
    pwsh tools/run-p0.ps1
    pwsh tools/run-p0.ps1 -Quick -SkipAot
#>
[CmdletBinding()]
param(
    [switch] $SkipAot,
    [switch] $SkipInteractive,
    [switch] $Quick
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'spikes/Shubbak.Spike/Shubbak.Spike.csproj'
$resultsDir = Join-Path $repoRoot 'docs/adr/p0-results'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'

if (-not (Test-Path -LiteralPath $project)) {
    throw "Spike project not found at $project"
}

New-Item -ItemType Directory -Force -Path $resultsDir | Out-Null

# Longer runs give tighter tail percentiles; -Quick trades that for speed.
$s1Events = if ($Quick) { 100000 } else { 1000000 }
$s2Seconds = if ($Quick) { 10 } else { 60 }
$s4Seconds = if ($Quick) { 30 } else { 90 }

$transcript = Join-Path $resultsDir "p0-$stamp.md"
$summary = [System.Collections.Generic.List[object]]::new()

function Write-Banner([string] $Text) {
    Write-Host ''
    Write-Host ('=' * 72) -ForegroundColor Cyan
    Write-Host "  $Text" -ForegroundColor Cyan
    Write-Host ('=' * 72) -ForegroundColor Cyan
}

function Invoke-Spike {
    param(
        [Parameter(Mandatory)] [string]   $Label,
        [Parameter(Mandatory)] [string]   $Exe,
        [Parameter(Mandatory)] [string[]] $Arguments,
        [string] $Mode = 'JIT',
        # Control groups exist to fail. A control group that PASSES is the
        # interesting result, because it means the thing it controls for does
        # not actually matter.
        [switch] $ExpectFail
    )

    Write-Banner "$Label  [$Mode]"
    Write-Host "> $Exe $($Arguments -join ' ')" -ForegroundColor DarkGray
    Write-Host ''

    $output = & $Exe @Arguments 2>&1 | Out-String
    $code = $LASTEXITCODE

    Write-Host $output

    $verdict = if ($output -match '(?m)^S\d:\s*(\w+)') { $Matches[1] } else { 'UNKNOWN' }

    if ($ExpectFail) {
        $verdict = if ($verdict -eq 'FAIL') { 'FAIL (expected)' } else { "$verdict (UNEXPECTED - control group passed)" }
    }

    $summary.Add([pscustomobject]@{
        Label    = $Label
        Mode     = $Mode
        Verdict  = $verdict
        ExitCode = $code
    })

    Add-Content -LiteralPath $transcript -Value @"

## $Label  [$Mode]

``````
$Exe $($Arguments -join ' ')
``````

``````text
$($output.TrimEnd())
``````
"@

    return $code
}

function Measure-Startup {
    <#
    .SYNOPSIS
        Measures cold-ish process startup externally.
    .DESCRIPTION
        `shubbak-spike ping` returns immediately from Main, so the wall time of the
        process is dominated by runtime initialisation. Measuring from outside
        avoids the trap of self-measurement, where reading Process.StartTime pulls
        in the diagnostics stack and inflates the very number being reported.
        The first iterations are discarded as warm-up (file cache, prefetch).
    #>
    param(
        [Parameter(Mandatory)] [string] $Exe,
        [int] $Iterations = 20,
        [int] $WarmUp = 5
    )

    $samples = [System.Collections.Generic.List[double]]::new()

    for ($i = 0; $i -lt ($Iterations + $WarmUp); $i++) {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        & $Exe ping | Out-Null
        $sw.Stop()
        if ($i -ge $WarmUp) { $samples.Add($sw.Elapsed.TotalMilliseconds) }
    }

    $sorted = $samples | Sort-Object
    [pscustomobject]@{
        Min    = $sorted[0]
        Median = $sorted[[int]($sorted.Count / 2)]
        Max    = $sorted[-1]
        Mean   = ($samples | Measure-Object -Average).Average
    }
}

# ---------------------------------------------------------------------------
Add-Content -LiteralPath $transcript -Value @"
# Shubbak P0 spike results

- Timestamp : $(Get-Date -Format 'o')
- Machine   : $env:COMPUTERNAME
- OS        : $([System.Environment]::OSVersion.VersionString)
- CPU cores : $([System.Environment]::ProcessorCount)
- SDK       : $(dotnet --version)
- Quick mode: $($Quick.IsPresent)
"@

Write-Banner 'Building (JIT)'
# NOTE: the JIT build must produce its own output directory. Publishing with
# PublishAot=true runs IncrementalClean over the shared bin path and deletes the
# framework-dependent apphost, so sharing a directory makes the two builds
# mutually destructive.
$jitOut = Join-Path $repoRoot 'spikes/Shubbak.Spike/bin/jit'
dotnet publish $project -c Release -r win-x64 -p:PublishAot=false -o $jitOut --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw 'JIT build failed.' }

$jitExe = Join-Path $jitOut 'shubbak-spike.exe'
if (-not (Test-Path -LiteralPath $jitExe)) { throw "JIT binary not found at $jitExe" }

$targets = @(, @{ Mode = 'JIT'; Exe = $jitExe })

# ---------------------------------------------------------------------------
if (-not $SkipAot) {
    Write-Banner 'Publishing (NativeAOT)'
    $aotOut = Join-Path $repoRoot 'spikes/Shubbak.Spike/bin/aot'

    $aotLog = Join-Path $resultsDir "aot-publish-$stamp.log"
    dotnet publish $project -c Release -r win-x64 -p:PublishAot=true -o $aotOut --nologo -v minimal 2>&1 |
        Tee-Object -FilePath $aotLog

    if ($LASTEXITCODE -ne 0) {
        Write-Warning 'NativeAOT publish FAILED. Continuing with JIT only; this is an ADR finding.'
        Add-Content -LiteralPath $transcript -Value "`n> **NativeAOT publish failed.** See ``$(Split-Path -Leaf $aotLog)``.`n"
    }
    else {
        # Trim/AOT warnings are a first-class result: they tell us which patterns
        # the real WM must avoid (reflection, dynamic serialization, etc).
        # Match only genuine ILxxxx trim/AOT diagnostics.
        $warnings = @(Select-String -Path $aotLog -Pattern 'warning IL[0-9]{4}' |
            ForEach-Object { $_.Line.Trim() } | Sort-Object -Unique)

        Add-Content -LiteralPath $transcript -Value @"

## NativeAOT publish

Trim / AOT analysis warnings: **$($warnings.Count)**

``````text
$($warnings -join "`n")
``````
"@
        Write-Host "AOT trim/analysis warnings: $($warnings.Count)" -ForegroundColor Yellow

        $aotExe = Join-Path $aotOut 'shubbak-spike.exe'
        if (Test-Path -LiteralPath $aotExe) {
            $targets += @{ Mode = 'AOT'; Exe = $aotExe }
        }
    }
}

# ---------------------------------------------------------------------------
foreach ($t in $targets) {
    $mode = $t.Mode
    $exe = $t.Exe

    # --- Startup, measured externally.
    Write-Banner "Startup measurement  [$mode]"
    $startup = Measure-Startup -Exe $exe
    Write-Host ("min={0:F1} ms  median={1:F1} ms  mean={2:F1} ms  max={3:F1} ms" -f `
            $startup.Min, $startup.Median, $startup.Mean, $startup.Max)

    Add-Content -LiteralPath $transcript -Value @"

## Process startup  [$mode]

Measured externally over 20 runs of ``shubbak-spike ping`` (5 warm-up runs discarded).

| min | median | mean | max |
| --- | --- | --- | --- |
| $('{0:F1}' -f $startup.Min) ms | $('{0:F1}' -f $startup.Median) ms | $('{0:F1}' -f $startup.Mean) ms | $('{0:F1}' -f $startup.Max) ms |
"@

    $summary.Add([pscustomobject]@{
        Label    = 'Startup (median)'
        Mode     = $mode
        Verdict  = ('{0:F1} ms' -f $startup.Median)
        ExitCode = 0
    })

    # --- S3: size / memory baseline.
    Invoke-Spike -Label 'S3 NativeAOT viability' -Mode $mode -Exe $exe -Arguments @('s3') | Out-Null

    # --- S1: real configuration, then the no-GC-pressure control group.
    Invoke-Spike -Label 'S1 keyboard hook latency (GC pressure)' -Mode $mode -Exe $exe `
        -Arguments @('s1', '--events', "$s1Events") | Out-Null

    Invoke-Spike -Label 'S1 keyboard hook latency (CONTROL: no GC pressure)' -Mode $mode -Exe $exe `
        -Arguments @('s1', '--events', "$s1Events", '--no-gc-pressure') | Out-Null

    # --- S2: real configuration, then two control groups.
    Invoke-Spike -Label 'S2 animation 144Hz / 20 windows (batched)' -Mode $mode -Exe $exe `
        -Arguments @('s2', '--windows', '20', '--hz', '144', '--seconds', "$s2Seconds") | Out-Null

    Invoke-Spike -Label 'S2 animation 144Hz / 20 windows (CONTROL: unbatched SetWindowPos)' -Mode $mode -Exe $exe `
        -ExpectFail -Arguments @('s2', '--windows', '20', '--hz', '144', '--seconds', "$s2Seconds", '--no-batch') | Out-Null

    # --- Headroom: how far past a realistic workspace can we push?
    Invoke-Spike -Label 'S2 animation 144Hz / 60 windows (headroom)' -Mode $mode -Exe $exe `
        -Arguments @('s2', '--windows', '60', '--hz', '144', '--seconds', "$s2Seconds") | Out-Null
}

# ---------------------------------------------------------------------------
if (-not $SkipInteractive) {
    Write-Banner 'S4 requires you'
    Write-Host @'
S4 measures whether EVENT_OBJECT_NAMECHANGE fires on browser TAB SWITCHES.
That is the exact bug in Zebar, and the answer decides how Taj gets live titles.

While it runs, please:
  1. Open a browser and switch between tabs several times.
  2. Switch focus between a few applications.
  3. Drag and resize a window.
  4. Minimise and restore a window.

'@ -ForegroundColor Yellow

    Read-Host 'Press ENTER to start S4'

    Invoke-Spike -Label 'S4 WinEvent fidelity and volume' -Mode 'JIT' -Exe $jitExe `
        -Arguments @('s4', '--seconds', "$s4Seconds") | Out-Null
}

# ---------------------------------------------------------------------------
Write-Banner 'Summary'
$summary | Format-Table -AutoSize

Add-Content -LiteralPath $transcript -Value @"

## Summary

| Spike | Mode | Verdict | Exit |
| --- | --- | --- | --- |
$($summary | ForEach-Object { "| $($_.Label) | $($_.Mode) | $($_.Verdict) | $($_.ExitCode) |" } | Out-String)
"@

Write-Host ''
Write-Host "Transcript written to: $transcript" -ForegroundColor Green

$failed = @($summary | Where-Object { $_.Verdict -eq 'FAIL' -or $_.Verdict -like '*UNEXPECTED*' })
if ($failed.Count -gt 0) {
    Write-Warning "$($failed.Count) spike run(s) reported FAIL. Review before writing the ADR."
    exit 1
}

exit 0
