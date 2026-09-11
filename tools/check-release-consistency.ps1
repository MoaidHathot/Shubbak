<#
.SYNOPSIS
    Checks that everything which carries the version agrees with Directory.Build.props.

.DESCRIPTION
    The version is written in one place and copied into several: the two application
    manifests, the winget manifests and their URLs, the Scoop manifest, the changelog,
    the readme. Each copy has been wrong at least once. This script compares them all
    and names the file to fix, so the comparison is made by a machine on every push
    rather than by a person on release day.

    Without -ForRelease it checks what must hold on every commit. With it, it also
    checks what must hold at the moment of tagging: a dated changelog entry for the
    version, an empty Unreleased section, and a readme that says the version is out.

.EXAMPLE
    .\tools\check-release-consistency.ps1
    .\tools\check-release-consistency.ps1 -ForRelease
#>
[CmdletBinding()]
param(
    [switch] $ForRelease
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$failures = [System.Collections.Generic.List[string]]::new()

. (Join-Path $PSScriptRoot 'WingetManifest.ps1')

# The architectures a release ships, as winget spells them and as the runtime
# identifiers spell them. Every package manifest must name exactly these.
$architectures = [ordered]@{ 'x64' = 'win-x64'; 'arm64' = 'win-arm64' }

function Read-Text([string] $relative) {
    [System.IO.File]::ReadAllText((Join-Path $root $relative))
}

function Check([bool] $ok, [string] $what, [string] $fix) {
    if ($ok) {
        Write-Output "ok    $what"
    }
    else {
        Write-Output "FAIL  $what"
        Write-Output "      $fix"
        $failures.Add($what)
    }
}

$props = [xml](Read-Text 'Directory.Build.props')
$versionNode = $props.SelectSingleNode('/Project/PropertyGroup/Version')
if (-not $versionNode) { throw 'Directory.Build.props has no <Version>.' }
$version = [string]$versionNode.InnerText.Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Directory.Build.props <Version> is '$version'; expected major.minor.patch." }

Write-Output "Directory.Build.props: $version"
Write-Output ''

$escaped = [regex]::Escape($version)

# The application manifests: assemblyIdentity carries the four-part form. The build
# checks the same thing; this catches it without a build.
foreach ($manifest in 'src\Shubbak.Wm\app.manifest', 'src\Shubbak.Wm\app.uiaccess.manifest') {
    $text = Read-Text $manifest
    Check ($text.Contains("version=`"$version.0`" name=`"Shubbak.Wm`"")) `
        "$manifest declares assemblyIdentity $version.0" `
        "Set version=`"$version.0`" on the assemblyIdentity element."
}

# winget: the version field and every URL that embeds the version - the release
# asset URLs and the documentation links pinned to the tag.
foreach ($file in Get-ChildItem (Join-Path $root 'packaging\winget') -Filter '*.yaml') {
    $relative = "packaging\winget\$($file.Name)"
    $text = Get-Content $file.FullName -Raw

    Check ($text -match "(?m)^PackageVersion:\s*$escaped\s*$") `
        "$relative has PackageVersion $version" `
        "Set PackageVersion: $version."

    foreach ($match in [regex]::Matches($text, '(?m)^\s*InstallerUrl:\s*(\S+)')) {
        $url = $match.Groups[1].Value
        Check ($url -match "/releases/download/v$escaped/shubbak-$escaped-win-(x64|arm64)\.(msi|zip)$") `
            "$relative installer URL names v$version ($url)" `
            "The asset URL must be .../releases/download/v$version/shubbak-$version-win-<x64|arm64>.<msi|zip>."
    }

    foreach ($match in [regex]::Matches($text, 'github\.com/MoaidHathot/Shubbak/blob/v(\d+\.\d+\.\d+)/')) {
        $pinned = $match.Groups[1].Value
        Check ($pinned -eq $version) `
            "$relative pins a documentation link to v$pinned" `
            "Pinned links must point at blob/v$version/."
    }
}

# winget: one MSI and one zip per architecture, each URL naming the architecture of
# the entry it sits in. A hash under the wrong architecture would validate and then
# fail on every install.
$installers = @(Read-WingetInstallers -Path (Join-Path $root 'packaging\winget\MoaidHathot.Shubbak.installer.yaml'))
foreach ($arch in $architectures.Keys) {
    foreach ($type in 'wix', 'zip') {
        $entry = $installers | Where-Object { $_.Architecture -eq $arch -and $_.InstallerType -eq $type }
        Check (@($entry).Count -eq 1) "winget manifest has one $arch $type installer" "Add or deduplicate the $arch $type entry under Installers."
        if (@($entry).Count -eq 1) {
            $suffix = "-$($architectures[$arch])." + $(if ($type -eq 'zip') { 'zip' } else { 'msi' })
            Check ($entry.InstallerUrl -and $entry.InstallerUrl.EndsWith($suffix)) `
                "winget $arch $type entry points at a $suffix asset" `
                "The InstallerUrl under Architecture: $arch, InstallerType: $type must end in $suffix."
        }
    }
}
Check (@($installers).Count -eq 2 * $architectures.Count) 'winget manifest has no other installers' "Expected $(2 * $architectures.Count) entries, found $(@($installers).Count)."

# Scoop.
$scoopText = Read-Text 'bucket\shubbak.json'
$scoop = $scoopText | ConvertFrom-Json
Check ($scoop.version -eq $version) 'bucket\shubbak.json has version' "Set `"version`": `"$version`"."
$scoopBlocks = @{ '64bit' = 'win-x64'; 'arm64' = 'win-arm64' }
foreach ($block in $scoopBlocks.Keys) {
    $rid = $scoopBlocks[$block]
    Check ($null -ne $scoop.architecture.$block -and $scoop.architecture.$block.url -match "/releases/download/v$escaped/shubbak-$escaped-$rid\.zip$") `
        "bucket\shubbak.json $block URL names v$version and $rid" `
        "The $block URL must be .../releases/download/v$version/shubbak-$version-$rid.zip."
    Check ($null -ne $scoop.autoupdate.architecture.$block -and $scoop.autoupdate.architecture.$block.url -like "*shubbak-`$version-$rid.zip") `
        "bucket\shubbak.json autoupdate has a $block block" `
        "Add autoupdate.architecture.$block with the `$version URL for $rid."
}

# The WiX project reads $(Version) directly and needs no check. The MSI's UpgradeCode
# must match what the winget manifest says it is, or winget cannot correlate an
# installed copy with the package.
$wxs = Read-Text 'packaging\msi\Package.wxs'
$installerYaml = Read-Text 'packaging\winget\MoaidHathot.Shubbak.installer.yaml'
$wxsUpgrade = [regex]::Match($wxs, 'UpgradeCode="(\{[0-9A-Fa-f-]{36}\})"').Groups[1].Value
$yamlUpgrade = [regex]::Match($installerYaml, "UpgradeCode:\s*'(\{[0-9A-Fa-f-]{36}\})'").Groups[1].Value
Check ($wxsUpgrade -and $wxsUpgrade -eq $yamlUpgrade) `
    'the MSI UpgradeCode matches the winget manifest' `
    "Package.wxs has $wxsUpgrade; the installer manifest has $yamlUpgrade. They must be the same GUID, and it must never change."

# Informational: placeholders are expected in the templates and filled by the build.
if (@($installers | Where-Object { Test-PlaceholderHash $_.InstallerSha256 }).Count -gt 0) {
    Write-Output 'note  packaging\winget still has placeholder hashes; tools\build-release.ps1 fills them.'
}

if ($ForRelease) {
    Write-Output ''
    Write-Output 'Release checks:'

    $changelog = Read-Text 'CHANGELOG.md'
    $lines = $changelog -split "`n"

    $heading = [regex]::Match($changelog, "(?m)^## \[$escaped\] - (\d{4}-\d{2}-\d{2})\s*$")
    Check $heading.Success "CHANGELOG.md has a dated entry for $version" "Add '## [$version] - YYYY-MM-DD' (tools\prepare-release.ps1 does this)."

    # Unreleased must be empty: anything in it is either part of this release, and
    # belongs under its heading, or is not, and should not be on the tag.
    $unreleasedAt = [array]::FindIndex($lines, [Predicate[string]]{ param($l) $l.TrimEnd() -eq '## [Unreleased]' })
    if ($unreleasedAt -ge 0) {
        $content = @()
        for ($i = $unreleasedAt + 1; $i -lt $lines.Count -and $lines[$i] -notmatch '^## \['; $i++) {
            if ($lines[$i].Trim()) { $content += $lines[$i] }
        }
        Check ($content.Count -eq 0) 'CHANGELOG.md Unreleased section is empty' "Move its $($content.Count) line(s) under [$version] or out of the tag."
    }

    Check ($changelog -match "(?m)^\[$escaped\]:\s*https://github\.com/MoaidHathot/Shubbak/") `
        "CHANGELOG.md has a link reference for [$version]" `
        "Add '[$version]: https://github.com/MoaidHathot/Shubbak/compare/v<previous>...v$version' at the bottom."

    foreach ($doc in 'getting-started', 'configuration', 'taj', 'dalil', 'ayn', 'scripting', 'faq', 'troubleshooting') {
        Check (Test-Path (Join-Path $root "docs\$doc.md")) "docs\$doc.md exists" 'The zip and the MSI ship it, and the readme links to it.'
    }
}

Write-Output ''
if ($failures.Count -gt 0) {
    throw "$($failures.Count) check(s) failed."
}

Write-Output "All version references agree on $version."
