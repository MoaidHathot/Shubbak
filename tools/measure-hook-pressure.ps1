<# .SYNOPSIS
Compare a real production hook under in-process GC pressure with an equally loaded
process that installs no hook. The input receiver and injector are outside that process.
#>
[CmdletBinding()]
param(
    [string] $Probe = (Join-Path $PSScriptRoot '../artifacts/input-probe/shubbak-input-probe.exe'),
    [string] $Output = (Join-Path $PSScriptRoot '../artifacts/hook-pressure'),
    [ValidateRange(2, 1000000)] [int] $Samples = 20000
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$Probe = [IO.Path]::GetFullPath($Probe)
$Output = [IO.Path]::GetFullPath($Output)
if (Test-Path $Output) { throw "Refusing to overwrite $Output" }
New-Item -ItemType Directory -Path $Output | Out-Null
& (Join-Path $root 'dist/shubbak.exe') stop
if ($LASTEXITCODE -ne 0) { throw 'Could not stop the normal session.' }
try {
    foreach ($pressure in @($false, $true)) {
        foreach ($round in 0..1) {
            $modes = if ($round -eq 0) { @($false, $true) } else { @($true, $false) }
            foreach ($hook in $modes) {
                $label = "gc-$pressure-hook-$hook-r$round"
                $info = [Diagnostics.ProcessStartInfo]::new($Probe)
                $info.UseShellExecute = $false
                $info.CreateNoWindow = $true
                $info.RedirectStandardInput = $true
                $info.RedirectStandardOutput = $true
                $info.RedirectStandardError = $true
                foreach ($arg in @('hook-host','--seconds','150')) { $info.ArgumentList.Add($arg) }
                if ($pressure) { $info.ArgumentList.Add('--gc') }
                if (-not $hook) { $info.ArgumentList.Add('--no-hook') }
                $hostProcess = [Diagnostics.Process]::Start($info)
                $errors = $hostProcess.StandardError.ReadToEndAsync()
                try {
                    $ready = $hostProcess.StandardOutput.ReadLineAsync()
                    if (-not $ready.Wait(10000) -or $ready.Result -ne 'ready') { throw 'Hook host did not start.' }
                    $summary = $hostProcess.StandardOutput.ReadToEndAsync()
                    $result = & $Probe receive --samples $Samples --warmup 1000 --interval-us 2000 --label $label --raw-output (Join-Path $Output "$label.csv")
                    if ($LASTEXITCODE -ne 0) { throw "Input integrity failed: $result" }
                    [IO.File]::AppendAllText((Join-Path $Output 'results.jsonl'), ($result -join "`n") + "`n")
                    $hostProcess.StandardInput.Close()
                    if (-not $hostProcess.WaitForExit(10000)) { throw 'Hook host did not stop.' }
                    if ($hostProcess.ExitCode -ne 0) { throw "Hook host failed: $($errors.Result)" }
                    [IO.File]::WriteAllText((Join-Path $Output "$label-host.json"), $summary.Result.Replace("`r", ''))
                    $data = $result | ConvertFrom-Json
                    Write-Output ("{0}: p99={1:F3} ms, p99.9={2:F3}, max={3:F3}; {4}" -f $label, $data.Delivery.P99Ms, $data.Delivery.P999Ms, $data.Delivery.MaxMs, $summary.Result.Trim())
                }
                finally {
                    if (-not $hostProcess.HasExited) { $hostProcess.Kill($true); $hostProcess.WaitForExit(5000) | Out-Null }
                    $hostProcess.Dispose()
                }
            }
        }
    }
}
finally { Start-Process -FilePath (Join-Path $root 'dist/shubbak-wm.exe') -WorkingDirectory (Join-Path $root 'dist') }
