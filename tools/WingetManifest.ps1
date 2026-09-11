<#
.SYNOPSIS
    Reads and rewrites the winget installer manifest by installer entry.

.DESCRIPTION
    Dot-source this file. The installer manifest lists one entry per architecture and
    installer type, and the fields that vary per entry - the hash, the ProductCode -
    have to be filled or reset for the right one. Rather than each script walking the
    YAML with its own regexes, this file does it once: Read-WingetInstallers turns the
    manifest into objects, and Update-WingetInstallerManifest rewrites chosen fields
    in place, calling back with the architecture and installer type each line belongs
    to, and leaving every other byte of the file alone - comments included.

    It understands exactly the shape packaging/winget/*.installer.yaml has: a list
    under Installers whose items start with `- Architecture:`, an InstallerType per
    item, and the per-item fields at a deeper indent. It is not a YAML parser.
#>

Set-StrictMode -Version Latest

<#
.SYNOPSIS
    One object per installer entry: Architecture, InstallerType, InstallerUrl,
    InstallerSha256, ProductCode.
#>
function Read-WingetInstallers {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $Path)

    $installers = [System.Collections.Generic.List[pscustomobject]]::new()
    $current = $null

    foreach ($line in Get-Content -LiteralPath $Path) {
        if ($line -match '^\s*-\s*Architecture:\s*(\S+)') {
            $current = [pscustomobject]@{ Architecture = $Matches[1]; InstallerType = $null; InstallerUrl = $null; InstallerSha256 = $null; ProductCode = $null }
            $installers.Add($current)
            continue
        }

        if ($null -eq $current) { continue }

        if ($line -match '^\s*InstallerType:\s*(\S+)' -and -not $current.InstallerType) { $current.InstallerType = $Matches[1] }
        elseif ($line -match '^\s*InstallerUrl:\s*(\S+)') { $current.InstallerUrl = $Matches[1] }
        elseif ($line -match "^\s*InstallerSha256:\s*'?([0-9A-Fa-f]{64})'?") { $current.InstallerSha256 = $Matches[1] }
        elseif ($line -match "^\s*ProductCode:\s*'?(\{[0-9A-Fa-f-]{36}\})'?" -and -not $current.ProductCode) { $current.ProductCode = $Matches[1] }
    }

    $installers
}

<#
.SYNOPSIS
    Rewrites InstallerUrl, InstallerSha256, ProductCode and ReleaseDate lines.

.PARAMETER Resolve
    Called for each such line with the field name, the architecture and installer
    type of the entry it belongs to (both $null for the top-level ReleaseDate), and
    the current value. Return the new value, or $null to leave the line as it is.
#>
function Update-WingetInstallerManifest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [scriptblock] $Resolve
    )

    $lines = Get-Content -LiteralPath $Path
    $arch = $null
    $type = $null

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]

        if ($line -match '^\s*-\s*Architecture:\s*(\S+)') {
            $arch = $Matches[1]
            $type = $null
            continue
        }

        # The first InstallerType after an Architecture is the entry's own; the one
        # inside AppsAndFeaturesEntries repeats it and must not reset anything.
        if ($arch -and -not $type -and $line -match '^\s*InstallerType:\s*(\S+)') {
            $type = $Matches[1]
            continue
        }

        if ($line -match '^(\s*)(InstallerUrl|InstallerSha256|ProductCode|ReleaseDate):\s*(.*?)\s*$') {
            $indent = $Matches[1]
            $field = $Matches[2]
            $current = $Matches[3]

            $new = & $Resolve $field $arch $type $current
            if ($null -ne $new) { $lines[$i] = "$indent${field}: $new" }
        }
    }

    [System.IO.File]::WriteAllText((Resolve-Path -LiteralPath $Path).Path, (($lines -join "`n") + "`n"), [System.Text.UTF8Encoding]::new($false))
}

<#
.SYNOPSIS
    The placeholder hash a template carries for one installer entry.

.DESCRIPTION
    Sixty-four of one hex digit, a different digit per entry, because winget warns
    when two installers share a hash and the templates are validated as they are.
    Anything matching this shape in a filled manifest is a value the build failed
    to replace.
#>
function Get-PlaceholderHash {
    param([string] $Architecture, [string] $InstallerType)

    $digit = switch ("$Architecture/$InstallerType") {
        'x64/wix' { '0' }
        'x64/zip' { '1' }
        'arm64/wix' { '2' }
        'arm64/zip' { '3' }
        default { 'F' }
    }

    $digit * 64
}

<#
.SYNOPSIS
    Whether a value is a placeholder hash rather than a real one.
#>
function Test-PlaceholderHash {
    param([string] $Value)
    $Value -match "^'?([0-9A-Fa-f])\1{63}'?$"
}

<#
.SYNOPSIS
    The placeholder ProductCode a template carries.
#>
function Get-PlaceholderProductCode {
    '{00000000-0000-0000-0000-000000000000}'
}
