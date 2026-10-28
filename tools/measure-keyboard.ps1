<#
.SYNOPSIS
  Measures tagged input delivery through native Shubbak builds, on an idle desktop.
.DESCRIPTION
  Uses test-owned windows and isolated daemon state. Stops the normal session for the
  run and restores it afterwards. Results are JSONL plus per-case diagnostic reports.
  Run this separately from builds/tests: those would be uncontrolled load.
#>
[CmdletBinding()]
param(
    [string] $Probe = (Join-Path $PSScriptRoot '../artifacts/input-probe-before/shubbak-input-probe.exe'),
    [string] $Current = (Join-Path $PSScriptRoot '../dist/shubbak-wm.exe'),
    [string] $Previous,
    [string] $Output = (Join-Path $PSScriptRoot '../artifacts/keyboard-baseline'),
    [ValidateRange(2, 1000000)] [int] $Samples = 20000,
    [ValidateRange(0, 100000)] [int] $Warmup = 1000,
    [ValidateRange(0, 1000000)] [int] $IntervalUs = 2000,
    [ValidateRange(1, 64)] [int] $Burst = 1,
    [ValidateRange(16, 512)] [int] $GpuIterations = 512,
    [ValidateRange(0, 10000)] [int] $IpcIntervalMs = 0,
    [ValidateRange(1, 24)] [int] $IpcClients = 4,
    [string[]] $Workloads = @('idle', 'cpu', 'animation', 'ipc'),
    [ValidateRange(1, 10)] [int] $Rounds = 2,
    [switch] $Bound
)

$ErrorActionPreference = 'Stop'
if (($Samples % 2) -ne 0) { throw 'Samples must contain complete down/up pairs.' }
if (($Warmup % 2) -ne 0) { throw 'Warmup must contain complete down/up pairs.' }
if ($Bound -and 'suspended' -in $Workloads) { throw 'A suspended daemon intentionally does not handle bound keys.' }
foreach ($workload in $Workloads) {
    if ($workload -notin @('idle', 'cpu', 'animation', 'ipc', 'suspended', 'gpu', 'gpu-cpu')) { throw "Unknown workload: $workload" }
}
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$Probe = [IO.Path]::GetFullPath($Probe)
$Current = [IO.Path]::GetFullPath($Current)
$Output = [IO.Path]::GetFullPath($Output)
$cli = Join-Path $root 'dist/shubbak.exe'
$window = Join-Path $root 'tests/Shubbak.TestWindow/bin/Release/net10.0-windows10.0.19041.0/win-x64/Shubbak.TestWindow.exe'
foreach ($file in @($Probe, $Current, $cli, $window)) {
    if (-not (Test-Path -LiteralPath $file)) { throw "Required binary missing: $file" }
}
if ($Previous) { $Previous = [IO.Path]::GetFullPath($Previous); if (-not (Test-Path $Previous)) { throw "Missing previous build: $Previous" } }
New-Item -ItemType Directory -Path $Output -Force | Out-Null
$results = Join-Path $Output 'results.jsonl'
if (Test-Path $results) { throw "Refusing to overwrite an existing measurement: $results" }
$metadata = @{
    StartedUtc = [DateTime]::UtcNow.ToString('O')
    OperatingSystem = [Environment]::OSVersion.VersionString
    LogicalProcessors = [Environment]::ProcessorCount
    Samples = $Samples; Warmup = $Warmup; IntervalUs = $IntervalUs; Burst = $Burst; GpuIterations = $GpuIterations
    Rounds = $Rounds; Bound = [bool]$Bound; Workloads = $Workloads; IpcIntervalMs = $IpcIntervalMs; IpcClients = $IpcClients
    Probe = (Get-FileHash $Probe).Hash
    Current = (Get-FileHash $Current).Hash
    Previous = $(if ($Previous) { (Get-FileHash $Previous).Hash } else { $null })
}
[IO.File]::WriteAllText((Join-Path $Output 'metadata.json'), ($metadata | ConvertTo-Json).Replace("`r", ''), [Text.UTF8Encoding]::new($false))

function Start-Child([string] $File, [string[]] $Arguments, [string] $Instance = '', [string] $State = '', [switch] $Ready, [switch] $WindowReady) {
    $info = [Diagnostics.ProcessStartInfo]::new($File)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($arg in $Arguments) { $info.ArgumentList.Add($arg) }
    if ($Instance) { $info.Environment['SHUBBAK_INSTANCE'] = $Instance }
    else { $info.Environment.Remove('SHUBBAK_INSTANCE') | Out-Null }
    if ($State) { $info.Environment['SHUBBAK_STATE_DIR'] = $State }
    $p = [Diagnostics.Process]::Start($info)
    $errorText = $p.StandardError.ReadToEndAsync()
    $windowHandle = 0L
    if ($Ready -or $WindowReady) {
        $line = $p.StandardOutput.ReadLineAsync()
        $valid = $line.Wait(15000)
        if ($valid) { $valid = if ($WindowReady) { [long]::TryParse($line.Result, [ref]$windowHandle) -and $windowHandle -ne 0 } else { $line.Result -eq 'ready' } }
        if (-not $valid) {
            if (-not $p.HasExited) { $p.Kill($true); $p.WaitForExit(5000) | Out-Null }
            throw "Child failed to become ready: $File $Arguments; $($errorText.Result)"
        }
    }
    [pscustomobject]@{ Process = $p; Output = $p.StandardOutput.ReadToEndAsync(); Error = $errorText; ExitCode = $null; WindowHandle = $windowHandle }
}

function End-Child($Child) {
    if ($null -eq $Child) { return }
    $p = $Child.Process
    if (-not $p.HasExited) {
        if ($p.ProcessName -eq 'Shubbak.TestWindow') { $p.CloseMainWindow() | Out-Null }
        $p.StandardInput.Close()
        if (-not $p.WaitForExit(15000)) { $p.Kill($true); $p.WaitForExit(5000) | Out-Null }
    }
    if ($p.HasExited) { $Child.ExitCode = $p.ExitCode }
    $p.Dispose()
}

function Invoke-Cli([string] $Instance, [string[]] $Arguments) {
    $child = Start-Child $cli $Arguments $Instance
    try {
        if (-not $child.Process.WaitForExit(15000)) { throw "CLI timed out: $Arguments" }
        [pscustomobject]@{ Code = $child.Process.ExitCode; Text = $child.Output.Result; Error = $child.Error.Result }
    }
    finally { End-Child $child }
}

function Save-Text([string] $Path, [string] $Text) {
    [IO.File]::WriteAllText($Path, $Text.Replace("`r", ''), [Text.UTF8Encoding]::new($false))
}

function Process-Counters {
    $snapshot = @{}
    foreach ($p in [Diagnostics.Process]::GetProcesses()) {
        try { $snapshot[$p.Id] = @{ Name=$p.ProcessName; CpuMs=$p.TotalProcessorTime.TotalMilliseconds; PrivateBytes=$p.PrivateMemorySize64 } }
        catch [ComponentModel.Win32Exception] { }
        catch [InvalidOperationException] { }
        finally { $p.Dispose() }
    }
    return $snapshot
}

$config = @'
general {
    default-layout "splith"
    reload-on-save #false
}
animation {
    enabled #true
    fps 60
    window-move duration=140 curve="ease-out"
}
workspaces { workspace "input-probe"; }
rules {
    rule "only our noise windows" {
        match { !process "Shubbak.TestWindow"; }
        do { ignore; }
    }
}
keybindings {
    bind "f23" { signal "input-probe"; }
    bind "f22" { wm-toggle-suspend; }
'@
foreach ($mod in @('alt', 'alt+shift', 'ctrl+alt', 'ctrl+shift')) {
    foreach ($f in 1..24) { $config += "`n    bind `"$mod+f$f`" { signal `"unused`"; }" }
}
$config += "`n}`n"

$normal = @(Get-Process -Name shubbak-wm -ErrorAction SilentlyContinue)
foreach ($p in $normal) {
    if ($p.Path -ne (Join-Path $root 'dist/shubbak-wm.exe')) { throw "Unexpected daemon running: $($p.Path)" }
    $p.Dispose()
}
if ($normal.Count -gt 0) {
    & $cli stop
    if ($LASTEXITCODE -ne 0) { throw 'Could not stop the normal session.' }
}

try {
    foreach ($workload in $Workloads) {
        for ($round = 0; $round -lt $Rounds; $round++) {
            $versions = if ($Bound -or $workload -in @('animation', 'ipc', 'suspended')) { @('current') } else { @('off', 'current') }
            if ($Previous -and $workload -ne 'suspended') { $versions = @($versions | Where-Object { $_ -ne 'current' }) + @('previous', 'current') }
            if (($round % 2) -eq 1) { [Array]::Reverse($versions) }
            foreach ($version in $versions) {
                $label = "$workload-$version-r$round-$(if ($Bound) {'bound'} else {'pass'})"
                $state = Join-Path $Output $label
                New-Item -ItemType Directory -Path $state | Out-Null
                $configPath = Join-Path $state 'config.kdl'
                Save-Text $configPath $config
                $instance = "input-$([Guid]::NewGuid().ToString('N').Substring(0,12))"
                $daemon = $null
                $diagnosticFailure = $null
                $loaders = [Collections.Generic.List[object]]::new()
                $windows = [Collections.Generic.List[object]]::new()
                try {
                    if ($version -ne 'off') {
                        if ($workload -eq 'animation') {
                            foreach ($i in 1..2) { $windows.Add((Start-Child $window @('--test-window') -WindowReady)) }
                        }
                        $binary = if ($version -eq 'previous') { $Previous } else { $Current }
                        $daemon = Start-Child $binary @('--config', $configPath, '--log-file') $instance $state
                        $deadline = [Diagnostics.Stopwatch]::StartNew()
                        do {
                            if ($daemon.Process.HasExited) { throw "Daemon exited: $($daemon.Error.Result)" }
                            $ready = Invoke-Cli $instance @('status')
                            if ($ready.Code -eq 0) { break }
                            Start-Sleep -Milliseconds 100
                        } while ($deadline.Elapsed.TotalSeconds -lt 15)
                        if ($ready.Code -ne 0) { throw 'Daemon did not start.' }
                        # Allow its initial adoption/focus to settle before the ignored
                        # receiver takes the keyboard. This is outside the measurement.
                        Start-Sleep -Milliseconds 250
                        if ($workload -eq 'animation') {
                            $adoption = [Diagnostics.Stopwatch]::StartNew()
                            do {
                                foreach ($w in $windows) { if ($w.Process.HasExited) { throw "A window helper exited: $($w.Error.Result)" } }
                                $query = Invoke-Cli $instance @('query','windows')
                                $present = @($query.Text | ConvertFrom-Json | ForEach-Object { $_.handle })
                                $missing = @($windows | Where-Object { $_.WindowHandle -notin $present })
                                if ($query.Code -eq 0 -and $missing.Count -eq 0 -and $present.Count -eq 2) { break }
                                if ($adoption.Elapsed.TotalSeconds -gt 10) { throw "The animation windows were not adopted: $($query.Text)" }
                                Start-Sleep -Milliseconds 50
                            } while ($true)
                        }
                        if ($workload -eq 'suspended') {
                            $suspend = Invoke-Cli $instance @('wm-suspend')
                            if ($suspend.Code -ne 0) { throw $suspend.Error }
                        }
                    }
                    if ($workload -in @('gpu', 'gpu-cpu')) {
                        $loaders.Add((Start-Child $Probe @('gpu-load', '--seconds', '180', '--iterations', "$GpuIterations") -Ready))
                        Start-Sleep -Seconds 2
                        if (Get-Command nvidia-smi -ErrorAction SilentlyContinue) {
                            $gpu = & nvidia-smi --query-gpu=name,utilization.gpu,power.draw --format=csv
                            Save-Text (Join-Path $state 'gpu.csv') ($gpu -join "`n")
                        }
                    }
                    if ($workload -in @('cpu', 'gpu-cpu')) {
                        $loaders.Add((Start-Child $Probe @('cpu-load', '--threads', '28', '--seconds', '180') -Ready))
                    }
                    if ($workload -eq 'animation') {
                        $handles = ($windows | ForEach-Object WindowHandle) -join ','
                        $loaders.Add((Start-Child $Probe @('churn', '--animate', '--handles', $handles, '--instance', $instance, '--seconds', '180') -Ready))
                    }
                    if ($workload -eq 'ipc') {
                        foreach ($i in 1..$IpcClients) { $loaders.Add((Start-Child $Probe @('churn', '--instance', $instance, '--seconds', '180', '--interval-ms', "$IpcIntervalMs") -Ready)) }
                    }
                    if ($workload -in @('gpu', 'gpu-cpu')) {
                        # Start cadence recording after all load workers have started;
                        # process startup/teardown is not a graphics frame measurement.
                        $loaders[0].Process.StandardInput.WriteLine('measure')
                        $loaders[0].Process.StandardInput.Flush()
                    }

                    $args = @('receive', '--samples', "$Samples", '--warmup', "$Warmup", '--interval-us', "$IntervalUs", '--burst', "$Burst", '--label', $label, '--raw-output', (Join-Path $state 'events.csv'))
                    if ($Bound) { $args += @('--bound', '--instance', $instance) }
                    $beforeProcesses = Process-Counters
                    $measurement = Start-Child $Probe $args
                    try {
                        if (-not $measurement.Process.WaitForExit(120000)) { throw 'Input probe timed out.' }
                        $text = $measurement.Output.Result.Trim()
                        Save-Text (Join-Path $state 'result.json') $text
                        if ($measurement.Process.ExitCode -ne 0) { throw "Invalid measurement $label`: $text $($measurement.Error.Result)" }
                        foreach ($loader in $loaders) {
                            if ($loader.Process.HasExited) { throw "Load generator exited early: $($loader.Error.Result) $($loader.Output.Result)" }
                        }
                        foreach ($w in $windows) { if ($w.Process.HasExited) { throw "An animation target exited early: $($w.Error.Result)" } }
                        $result = $text | ConvertFrom-Json
                        if ($result.Missing -ne 0 -or $result.WrongEdges -ne 0 -or $result.LeakedBoundEvents -ne 0) { throw "Input integrity failed: $text" }
                        $afterProcesses = Process-Counters
                        $resources = foreach ($id in $afterProcesses.Keys) {
                            if ($beforeProcesses.ContainsKey($id)) {
                                $cpu = $afterProcesses[$id].CpuMs - $beforeProcesses[$id].CpuMs
                                if ($cpu -gt 0 -or ($null -ne $daemon -and $id -eq $daemon.Process.Id)) {
                                    [pscustomobject]@{ Id=$id; Name=$afterProcesses[$id].Name; CpuMs=$cpu; PrivateBytes=$afterProcesses[$id].PrivateBytes }
                                }
                            }
                        }
                        Save-Text (Join-Path $state 'resources.json') ($resources | Sort-Object CpuMs -Descending | ConvertTo-Json)
                        Write-Output ("{0}: n={1}, p50={2:F3} ms, p99={3:F3}, p99.9={4:F3}, max={5:F3}" -f $label, $result.Delivery.Count, $result.Delivery.P50Ms, $result.Delivery.P99Ms, $result.Delivery.P999Ms, $result.Delivery.MaxMs)
                    }
                    finally { End-Child $measurement }
                    if ($null -ne $daemon) {
                        try {
                            $diagnosis = Invoke-Cli $instance @('diagnose')
                            if ($diagnosis.Code -ne 0) { throw $diagnosis.Error }
                            Save-Text (Join-Path $state 'diagnostic.md') $diagnosis.Text
                            if ($workload -eq 'animation') {
                                if ($diagnosis.Text -notmatch '\*\*Managed windows\*\*: 2\b' -or
                                    $diagnosis.Text -notmatch 'frame p99 [^\r\n]*\((\d+) frames\)' -or [int]$Matches[1] -lt 30) {
                                    throw 'Animation workload was not active: expected two managed windows and at least 30 animation-frame samples.'
                                }
                            }
                        }
                        catch {
                            $diagnosticFailure = $_.Exception.Message
                            Save-Text (Join-Path $state 'diagnostic-error.txt') $diagnosticFailure
                        }
                    }
                }
                finally {
                    # Tell every load to stop before waiting on any one. Leaving the
                    # CPU saturator active while the browser shut down made a healthy
                    # graphics worker exceed the old five-second teardown budget.
                    foreach ($loader in $loaders) {
                        if (-not $loader.Process.HasExited) { $loader.Process.StandardInput.Close() }
                    }
                    $loadIndex = 0
                    foreach ($loader in $loaders) {
                        End-Child $loader
                        Save-Text (Join-Path $state "load-$loadIndex.txt") ($loader.Output.Result + $loader.Error.Result)
                        $loadIndex++
                    }
                    $windowIndex = 0
                    foreach ($w in $windows) {
                        End-Child $w
                        Save-Text (Join-Path $state "window-$windowIndex.json") (@{ Handle=$w.WindowHandle; ExitCode=$w.ExitCode; Output=$w.Output.Result; Error=$w.Error.Result } | ConvertTo-Json)
                        $windowIndex++
                    }
                    if ($null -ne $daemon) {
                        try {
                            if ($diagnosticFailure -and -not $daemon.Process.HasExited) {
                                $afterLoad = Invoke-Cli $instance @('diagnose')
                                Save-Text (Join-Path $state 'diagnostic-after-load.md') $afterLoad.Text
                            }
                            if (-not $daemon.Process.HasExited) { $null = Invoke-Cli $instance @('wm-exit') }
                        }
                        finally { End-Child $daemon }
                    }
                }
                foreach ($loader in $loaders) {
                    if ($loader.ExitCode -ne 0) { throw "Load generator failed: $($loader.Output.Result) $($loader.Error.Result)" }
                }
                if ($diagnosticFailure) { throw $diagnosticFailure }
                # Only accepted cases enter the aggregate: teardown and diagnostic
                # failures formerly left an apparently valid row behind.
                [IO.File]::AppendAllText($results, $text + "`n", [Text.UTF8Encoding]::new($false))
            }
        }
    }
}
finally {
    # This task intentionally stopped the normal daemon before preparing the probe;
    # restore it even if no normal daemon was running at this script's entry.
    Start-Process -FilePath (Join-Path $root 'dist/shubbak-wm.exe') -WorkingDirectory (Join-Path $root 'dist')
}
