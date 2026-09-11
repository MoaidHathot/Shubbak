<#
.SYNOPSIS
    Prepares the tree for a release: sets the version everywhere, dates the changelog,
    checks the result, and - when asked - tags.

.DESCRIPTION
    The version lives in Directory.Build.props and is copied into the application
    manifests, the winget and Scoop manifests and their URLs, the changelog and the
    readme. This script makes all of those edits at once so they cannot drift, then
    runs tools\check-release-consistency.ps1 -ForRelease to prove it.

    It is safe to run more than once for the same version: a second run re-dates the
    changelog entry to today and folds anything that has since landed under
    [Unreleased] into it, which is what you want when the tag waits on testing.

    Tagging is separate and explicit. -Tag refuses to run with uncommitted changes,
    because the tag must name a commit that contains the edits this script made.

.PARAMETER Version
    The version to release, as major.minor.patch.

.PARAMETER Tag
    After the checks pass, create the annotated tag v<Version> on HEAD.

.PARAMETER Push
    With -Tag, push the tag to origin. That is what starts the release workflow.

.EXAMPLE
    .\tools\prepare-release.ps1 -Version 0.10.0
    # review, test, commit
    .\tools\prepare-release.ps1 -Version 0.10.0 -Tag -Push
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version,
    [switch] $Tag,
    [switch] $Push
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$today = (Get-Date).ToString('yyyy-MM-dd')
$utf8 = [System.Text.UTF8Encoding]::new($false)

. (Join-Path $PSScriptRoot 'WingetManifest.ps1')

function Edit-File([string] $relative, [scriptblock] $transform) {
    $path = Join-Path $root $relative
    $before = [System.IO.File]::ReadAllText($path)
    $after = & $transform $before
    if ($after -ne $before) {
        [System.IO.File]::WriteAllText($path, $after, $utf8)
        Write-Output "edited  $relative"
    }
    else {
        Write-Output "as-is   $relative"
    }
}

$props = [xml][System.IO.File]::ReadAllText((Join-Path $root 'Directory.Build.props'))
$previous = [string]$props.SelectSingleNode('/Project/PropertyGroup/Version').InnerText.Trim()
Write-Output "Version: $previous -> $Version"
Write-Output ''

# ---- The version, everywhere it is written -------------------------------------------

Edit-File 'Directory.Build.props' {
    param($t) [regex]::Replace($t, '<Version>\d+\.\d+\.\d+</Version>', "<Version>$Version</Version>")
}

foreach ($manifest in 'src\Shubbak.Wm\app.manifest', 'src\Shubbak.Wm\app.uiaccess.manifest') {
    Edit-File $manifest {
        param($t) [regex]::Replace($t, 'version="\d+\.\d+\.\d+\.\d+" name="Shubbak.Wm"', "version=`"$Version.0`" name=`"Shubbak.Wm`"")
    }
}

foreach ($file in Get-ChildItem (Join-Path $root 'packaging\winget') -Filter '*.yaml') {
    Edit-File "packaging\winget\$($file.Name)" {
        param($t)
        $t = [regex]::Replace($t, '(?m)^(PackageVersion:[ \t]*)\d+\.\d+\.\d+[ \t]*$', "`${1}$Version")
        $t = [regex]::Replace($t, '/releases/download/v\d+\.\d+\.\d+/shubbak-\d+\.\d+\.\d+-(win-(?:x64|arm64))\.', "/releases/download/v$Version/shubbak-$Version-`${1}.")
        $t = [regex]::Replace($t, 'github\.com/MoaidHathot/Shubbak/blob/v\d+\.\d+\.\d+/', "github.com/MoaidHathot/Shubbak/blob/v$Version/")
        $t
    }
}

# Back to placeholders, entry by entry: the hashes and ProductCodes of the previous
# version are wrong for this one, and a wrong hash looks exactly like a right one.
$installerManifest = Join-Path $root 'packaging\winget\MoaidHathot.Shubbak.installer.yaml'
$before = [System.IO.File]::ReadAllText($installerManifest)
Update-WingetInstallerManifest -Path $installerManifest -Resolve {
    param($field, $arch, $type, $current)
    switch ($field) {
        'InstallerSha256' { "'$(Get-PlaceholderHash $arch $type)'" }
        'ProductCode' { "'$(Get-PlaceholderProductCode)'" }
        'ReleaseDate' { $today }
        default { $null }
    }
}
Write-Output ($(if ([System.IO.File]::ReadAllText($installerManifest) -ne $before) { 'edited  ' } else { 'as-is   ' }) + 'packaging\winget\MoaidHathot.Shubbak.installer.yaml (placeholders)')

Edit-File 'bucket\shubbak.json' {
    param($t)
    $t = [regex]::Replace($t, '"version":\s*"\d+\.\d+\.\d+"', "`"version`": `"$Version`"")
    $t = [regex]::Replace($t, '/releases/download/v\d+\.\d+\.\d+/shubbak-\d+\.\d+\.\d+-(win-(?:x64|arm64))\.zip', "/releases/download/v$Version/shubbak-$Version-`${1}.zip")
    # Placeholders per block, matching the winget template's digits for the zips.
    $lines = $t -split "`n"
    $arch = $null
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^\s*"(64bit|arm64)":\s*\{') { $arch = if ($Matches[1] -eq '64bit') { 'x64' } else { 'arm64' }; continue }
        if ($arch -and $lines[$i] -match '^(\s*"hash":\s*")[0-9a-fA-F]{64}(".*)$') {
            $lines[$i] = $Matches[1] + (Get-PlaceholderHash $arch 'zip') + $Matches[2]
            $arch = $null
        }
    }
    $lines -join "`n"
}


# ---- The changelog -----------------------------------------------------------------

Edit-File 'CHANGELOG.md' {
    param($t)
    $nl = "`n"
    $lines = [System.Collections.Generic.List[string]]($t -split $nl)

    $unreleasedAt = $lines.FindIndex({ param($l) $l.TrimEnd() -eq '## [Unreleased]' })
    if ($unreleasedAt -lt 0) { throw 'CHANGELOG.md has no "## [Unreleased]" heading.' }

    # The Unreleased body: everything up to the next version heading.
    $end = $unreleasedAt + 1
    while ($end -lt $lines.Count -and $lines[$end] -notmatch '^## \[') { $end++ }
    $body = $lines.GetRange($unreleasedAt + 1, $end - ($unreleasedAt + 1))
    $lines.RemoveRange($unreleasedAt + 1, $body.Count)

    # Trim blank lines at both ends of the body; the heading layout adds its own.
    while ($body.Count -gt 0 -and -not $body[0].Trim()) { $body.RemoveAt(0) }
    while ($body.Count -gt 0 -and -not $body[$body.Count - 1].Trim()) { $body.RemoveAt($body.Count - 1) }

    $existingAt = $lines.FindIndex({ param($l) $l -match "^## \[$([regex]::Escape($Version))\]" })

    if ($existingAt -ge 0) {
        # Second run for the same version: re-date it and put anything new at the top
        # of its body, after the heading and its blank line.
        $lines[$existingAt] = "## [$Version] - $today"
        if ($body.Count -gt 0) {
            $lines.InsertRange($existingAt + 2, [string[]]($body + @('')))
        }
    }
    else {
        if ($body.Count -eq 0) { throw 'CHANGELOG.md has nothing under [Unreleased] to release.' }
        $insert = @('', "## [$Version] - $today", '') + $body + @('')
        $lines.InsertRange($unreleasedAt + 1, [string[]]$insert)
    }

    # Link references at the bottom. [Unreleased] compares from this tag; this version
    # compares from the one before it, which is the next dated heading down the file.
    $text = ($lines -join $nl)
    $following = [regex]::Matches($text, '(?m)^## \[(\d+\.\d+\.\d+)\] - ') |
        ForEach-Object { $_.Groups[1].Value } |
        Where-Object { $_ -ne $Version } |
        Select-Object -First 1

    $unreleasedLink = "[Unreleased]: https://github.com/MoaidHathot/Shubbak/compare/v$Version...HEAD"
    $versionLink = if ($following) {
        "[$Version]: https://github.com/MoaidHathot/Shubbak/compare/v$following...v$Version"
    } else {
        "[$Version]: https://github.com/MoaidHathot/Shubbak/releases/tag/v$Version"
    }

    if ($text -match '(?m)^\[Unreleased\]:.*$') { $text = [regex]::Replace($text, '(?m)^\[Unreleased\]:.*$', $unreleasedLink) }
    else { $text = $text.TrimEnd($nl) + $nl + $nl + $unreleasedLink + $nl }

    $versionPattern = "(?m)^\[$([regex]::Escape($Version))\]:.*$"
    if ($text -match $versionPattern) { $text = [regex]::Replace($text, $versionPattern, $versionLink) }
    else { $text = [regex]::Replace($text, '(?m)^(\[Unreleased\]:.*)$', "`$1$nl$versionLink") }

    $text.TrimEnd($nl) + $nl
}

# ---- Prove it ------------------------------------------------------------------------

Write-Output ''
& (Join-Path $PSScriptRoot 'check-release-consistency.ps1') -ForRelease

# ---- Tag -----------------------------------------------------------------------------

if ($Tag) {
    Write-Output ''
    Push-Location $root
    try {
        $dirty = git status --porcelain
        if ($dirty) {
            throw "The working tree has uncommitted changes. Commit the release edits first, then tag:`n$($dirty -join "`n")"
        }

        $existing = git tag --list "v$Version"
        if ($existing) { throw "Tag v$Version already exists. Delete it from both places before retrying (see RELEASING.md)." }

        git tag -a "v$Version" -m "Shubbak $Version"
        if ($LASTEXITCODE -ne 0) { throw 'git tag failed.' }
        Write-Output "tagged  v$Version at $(git rev-parse --short HEAD)"

        if ($Push) {
            git push origin "v$Version"
            if ($LASTEXITCODE -ne 0) { throw 'git push failed.' }
            Write-Output "pushed  v$Version - the release workflow is running: https://github.com/MoaidHathot/Shubbak/actions/workflows/release.yml"
        }
        else {
            Write-Output "Push it with: git push origin v$Version"
        }
    }
    finally { Pop-Location }
}
elseif ($Push) {
    Write-Warning '-Push does nothing without -Tag.'
}
