<#
.SYNOPSIS
    Installs an MSI silently, checks what it put on the machine, uninstalls it, and
    checks that everything is gone again.

.DESCRIPTION
    The installer's real test. ICE validation says a package is well formed; this
    says it does what it is for on a real machine - which for the ARM64 package is
    the only check there is, because Windows Installer on an x64 machine refuses to
    open it at all. CI runs it on the runner for each architecture; the runner is
    elevated and disposable, which is what an installer test wants.

    Everything the package claims is checked: the files under Program Files, the
    machine PATH entry, the registry keys, the Start Menu shortcut, the Apps &
    Features entry - and that the installed shubbak.exe reports the version, when this
    machine can run it. Then the same list is checked to be gone after uninstall, with
    the Program Files directory removed.

    Needs elevation; refuses without it rather than failing halfway.

.EXAMPLE
    .\tools\test-msi.ps1 -Path artifacts\shubbak-0.10.0-win-x64.msi -Version 0.10.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Path,
    [Parameter(Mandatory)] [string] $Version
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([System.Security.Principal.WindowsPrincipal]$identity).IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Installing an MSI needs an elevated shell. Run this elevated, or let CI run it.'
}

$msi = (Resolve-Path -LiteralPath $Path).Path
$installDir = Join-Path $env:ProgramFiles 'Shubbak'
$shortcut = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Shubbak.lnk'
$machineEnvironment = 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Environment'
$failures = 0

function Check([bool] $ok, [string] $what) {
    if ($ok) { Write-Output "ok    $what" } else { Write-Output "FAIL  $what"; $script:failures++ }
}

function Invoke-Msiexec([string] $verb, [string] $log) {
    $process = Start-Process msiexec -ArgumentList "/$verb `"$msi`" /qn /norestart /l*v `"$log`"" -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        Write-Output "msiexec /$verb exited $($process.ExitCode). The end of the log:"
        Get-Content $log -Tail 60 | ForEach-Object { "    $_" }
        throw "msiexec /$verb failed."
    }
}

function Get-MachinePath {
    # Read raw, so %SystemRoot% and friends come back as written rather than expanded.
    $key = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SYSTEM\CurrentControlSet\Control\Session Manager\Environment')
    try { [string]$key.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) }
    finally { $key.Dispose() }
}

function Test-MachinePathHas([string] $directory) {
    $wanted = $directory.TrimEnd('\')
    @((Get-MachinePath) -split ';' | Where-Object { $_.TrimEnd('\') -ieq $wanted }).Count -gt 0
}

function Get-ArpEntry {
    Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall' |
        ForEach-Object { Get-ItemProperty $_.PSPath } |
        Where-Object { $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -eq 'Shubbak' } |
        Select-Object -First 1
}

function Get-Machine([string] $exe) {
    $bytes = [System.IO.File]::ReadAllBytes($exe)
    $pe = [BitConverter]::ToInt32($bytes, 0x3C)
    [BitConverter]::ToUInt16($bytes, $pe + 4)
}

function Test-CanRun([string] $exe) {
    $machine = Get-Machine $exe
    switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
        'Arm64' { $true }                 # ARM64 runs x64 through emulation as well
        'X64' { $machine -eq 0x8664 }
        default { $false }
    }
}

Write-Output "Installing $(Split-Path $msi -Leaf)"
Check (-not (Test-Path $installDir)) "$installDir does not exist before the install"
Check (-not (Test-MachinePathHas $installDir)) 'the machine PATH does not have the install directory before the install'

$installLog = Join-Path ([System.IO.Path]::GetTempPath()) 'shubbak-msi-install.log'
Invoke-Msiexec 'i' $installLog

Write-Output ''
Write-Output 'After install:'
foreach ($file in 'shubbak-wm.exe', 'shubbak.exe', 'taj.exe', 'dalil.exe', 'ayn.exe', 'LICENSE', 'shubbak.example.kdl', 'docs\getting-started.md', 'docs\configuration.md', 'docs\troubleshooting.md') {
    Check (Test-Path (Join-Path $installDir $file)) "$file is installed"
}

Check (Test-MachinePathHas $installDir) 'the machine PATH has the install directory'
Check (Test-Path $shortcut) 'the Start Menu shortcut exists'

$installFolder = (Get-ItemProperty 'HKLM:\SOFTWARE\Shubbak' -ErrorAction SilentlyContinue).InstallFolder
Check ($installFolder -and $installFolder.TrimEnd('\') -ieq $installDir) "HKLM\SOFTWARE\Shubbak\InstallFolder is $installDir (got '$installFolder')"
$registeredVersion = (Get-ItemProperty 'HKLM:\SOFTWARE\Shubbak' -ErrorAction SilentlyContinue).Version
Check ($registeredVersion -eq $Version) "HKLM\SOFTWARE\Shubbak\Version is $Version (got '$registeredVersion')"

$arp = Get-ArpEntry
Check ($null -ne $arp) 'Apps & Features lists Shubbak'
if ($arp) {
    Check ($arp.DisplayVersion -eq $Version) "Apps & Features version is $Version (got '$($arp.DisplayVersion)')"
    Check ($arp.Publisher -eq 'Moaid Hathot') "Apps & Features publisher is Moaid Hathot (got '$($arp.Publisher)')"
}

$cli = Join-Path $installDir 'shubbak.exe'
if (Test-CanRun $cli) {
    $out = New-TemporaryFile
    try {
        $process = Start-Process -FilePath $cli -ArgumentList '--version' -RedirectStandardOutput $out -NoNewWindow -Wait -PassThru
        $reported = (Get-Content $out -Raw).Trim()
        Check ($process.ExitCode -eq 0 -and $reported -eq "Shubbak $Version") "the installed shubbak.exe reports 'Shubbak $Version' (got '$reported', exit $($process.ExitCode))"
    }
    finally { Remove-Item $out -Force -ErrorAction SilentlyContinue }
}
else {
    Write-Output ('skip  the installed shubbak.exe cannot run on this machine (machine type 0x{0:X4})' -f (Get-Machine $cli))
}

Write-Output ''
Write-Output 'Uninstalling'
$uninstallLog = Join-Path ([System.IO.Path]::GetTempPath()) 'shubbak-msi-uninstall.log'
Invoke-Msiexec 'x' $uninstallLog

Write-Output ''
Write-Output 'After uninstall:'
Check (-not (Test-Path $installDir)) "$installDir is gone"
Check (-not (Test-MachinePathHas $installDir)) 'the machine PATH no longer has the install directory'
Check (-not (Test-Path $shortcut)) 'the Start Menu shortcut is gone'
Check (-not (Test-Path 'HKLM:\SOFTWARE\Shubbak')) 'HKLM\SOFTWARE\Shubbak is gone'
Check (-not (Test-Path 'HKCU:\Software\Shubbak')) 'HKCU\Software\Shubbak is gone'
Check ($null -eq (Get-ArpEntry)) 'Apps & Features no longer lists Shubbak'

Write-Output ''
if ($failures -gt 0) { throw "$failures check(s) failed. Logs: $installLog, $uninstallLog" }
Write-Output "The installer does what it says, and undoes it."
