<#
.SYNOPSIS
    Makes sure a winget client recent enough to validate the manifests is on PATH.

.DESCRIPTION
    The manifests use schema 1.12, which needs a client of at least that version. The
    GitHub runner images ship whatever App Installer the base image had, which may be
    older or absent, so CI asks first and installs only when it must - through
    Microsoft.WinGet.Client's Repair-WinGetPackageManager, which is Microsoft's own
    route for putting the latest stable client on a machine, dependencies included.

    The update goes through GitHub's API - one call to find the release, one for its
    assets - which the module makes anonymously unless GH_TOKEN or GITHUB_TOKEN is in
    the environment. Anonymous calls share a rate limit with everything else on the
    machine's address, and on a hosted runner that is everyone's builds; CI sets the
    token and this script says when it has none, so the failure reads as what it is.

    Prints the version it ends up with. Exits non-zero if no usable client can be had.

.EXAMPLE
    .\tools\ensure-winget.ps1
#>
[CmdletBinding()]
param(
    [Version] $Minimum = '1.12.0'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-WingetVersion {
    $command = Get-Command winget -ErrorAction SilentlyContinue
    if (-not $command) {
        # The App Execution Alias lives here and is not always on PATH in a fresh session.
        $alias = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\winget.exe'
        if (Test-Path $alias) { $command = Get-Command $alias -ErrorAction SilentlyContinue }
    }
    if (-not $command) { return $null }

    try {
        $text = (& $command.Source --version 2>$null | Select-Object -First 1)
        if ($text -match 'v?(\d+\.\d+\.\d+)') { return @{ Version = [Version]$Matches[1]; Path = $command.Source } }
    }
    catch { }

    return $null
}

$found = Get-WingetVersion
if ($found -and $found.Version -ge $Minimum) {
    Write-Output "winget $($found.Version) at $($found.Path)"
    exit 0
}

Write-Output ($(if ($found) { "winget $($found.Version) is older than $Minimum; updating." } else { 'winget is not available; installing.' }))

if (-not ($env:GH_TOKEN -or $env:GITHUB_TOKEN)) {
    Write-Warning 'No GH_TOKEN or GITHUB_TOKEN in the environment: the WinGet module will call GitHub anonymously, and a hosted runner shares that rate limit with every other build on its address.'
}

# Repair-WinGetPackageManager installs or updates App Installer and its framework
# dependencies. -Latest takes the newest stable; -Force reinstalls even when a client
# of some version is already present.
Install-Module -Name Microsoft.WinGet.Client -Force -Scope CurrentUser -AllowClobber -Repository PSGallery
Import-Module Microsoft.WinGet.Client
Repair-WinGetPackageManager -Latest -Force

$found = Get-WingetVersion
if (-not $found) { throw 'winget is still not available after Repair-WinGetPackageManager.' }
if ($found.Version -lt $Minimum) { throw "winget $($found.Version) is still older than $Minimum after Repair-WinGetPackageManager." }

# Later steps in the same job find it through PATH.
if ($env:GITHUB_PATH) { Split-Path $found.Path -Parent >> $env:GITHUB_PATH }

Write-Output "winget $($found.Version) at $($found.Path)"
