<# .SYNOPSIS
Compare native Ayn reload retention with the same daemon/companions and normal config.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Current,
    [Parameter(Mandatory)] [string] $Previous,
    [Parameter(Mandatory)] [string] $Output,
    [int] $Rounds = 2,
    [int] $Batches = 6
)
$ErrorActionPreference='Stop'
foreach ($name in @('Current','Previous','Output')) { Set-Variable $name ([IO.Path]::GetFullPath((Get-Variable $name -ValueOnly))) }
if (Test-Path $Output) { throw "Refusing to overwrite $Output" }
New-Item -ItemType Directory -Path $Output | Out-Null
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dist=Join-Path $root 'dist'
$cli=Join-Path $dist 'shubbak.exe'
$meta=@{Current=(Get-FileHash $Current).Hash;Previous=(Get-FileHash $Previous).Hash;StartedUtc=[DateTime]::UtcNow.ToString('O');Rounds=$Rounds;Reloads=$Batches*10}
[IO.File]::WriteAllText((Join-Path $Output 'metadata.json'),($meta | ConvertTo-Json).Replace("`r",''))
try {
    for ($round=0; $round -lt $Rounds; $round++) {
        $versions=@('previous','current')
        if ($round % 2) { [Array]::Reverse($versions) }
        foreach ($version in $versions) {
            & $cli stop
            if ($LASTEXITCODE -ne 0) { throw 'Stop failed.' }
            Copy-Item $(if ($version -eq 'previous') {$Previous} else {$Current}) (Join-Path $dist 'ayn.exe') -Force
            Start-Process -FilePath (Join-Path $dist 'shubbak-wm.exe') -WorkingDirectory $dist
            Start-Sleep -Seconds 8
            $config=(& $cli query config-path | ConvertFrom-Json).path
            $configHash=(Get-FileHash $config).Hash
            $p=Get-Process -Name ayn
            $rows=[Collections.Generic.List[object]]::new()
            try {
                for ($batch=0; $batch -le $Batches; $batch++) {
                    if ($batch -gt 0) {
                        foreach ($i in 1..10) {
                            & $cli wm-reload-config *> $null
                            if ($LASTEXITCODE -ne 0) { throw 'Reload failed.' }
                            Start-Sleep -Milliseconds 300
                        }
                        Start-Sleep -Seconds 1
                    }
                    $p.Refresh()
                    $rows.Add([pscustomobject]@{Version=$version;Round=$round;Reloads=$batch*10;PrivateBytes=$p.PrivateMemorySize64;WorkingSet=$p.WorkingSet64;CpuMs=$p.TotalProcessorTime.TotalMilliseconds;Handles=$p.HandleCount;Threads=$p.Threads.Count})
                }
                if ((Get-FileHash $config).Hash -ne $configHash) { throw 'Config changed during the equal-reload comparison.' }
                [IO.File]::WriteAllText((Join-Path $Output "$version-r$round.json"),($rows | ConvertTo-Json).Replace("`r",''))
                $rows | Format-Table Version,Round,Reloads,@{Name='PrivateMiB';Expression={[Math]::Round($_.PrivateBytes/1MB,2)}},CpuMs,Handles,Threads -AutoSize
            }
            finally { $p.Dispose() }
        }
    }
}
finally {
    & $cli stop
    Copy-Item $Current (Join-Path $dist 'ayn.exe') -Force
    Start-Process -FilePath (Join-Path $dist 'shubbak-wm.exe') -WorkingDirectory $dist
}
