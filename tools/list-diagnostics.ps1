<#
.SYNOPSIS
    Regenerates docs/diagnostics.md from the diagnostic codes in the source.

.DESCRIPTION
    Every Diagnostic.Error / Diagnostic.Warning call site names its code as a string
    literal followed by its message, so the catalogue can be read off the source rather
    than kept by hand. A handful of codes are routed through helper methods whose
    message is built elsewhere; those carry a description here.

    Run from the repository root:

        pwsh -File tools/list-diagnostics.ps1

    CI does not run this; it is for the person who added a code. The page carries the
    same note.
#>
[CmdletBinding()]
param(
    [string] $Root = (Join-Path $PSScriptRoot '..'),
    [string] $Output = (Join-Path $PSScriptRoot '..' 'docs' 'diagnostics.md')
)

$ErrorActionPreference = 'Stop'

$files = Get-ChildItem (Join-Path $Root 'src') -Recurse -Filter *.cs |
    Where-Object { $_.FullName -notmatch '\\obj\\|\\bin\\' }

$results = [ordered]@{}

foreach ($file in $files) {
    $text = Get-Content -LiteralPath $file.FullName -Raw

    foreach ($m in [regex]::Matches($text, 'Diagnostic\.(Error|Warning|Info)\(\s*"((?:SHB|TAJ|DAL|AYN)\d{4})"\s*,\s*(\$?"(?:[^"\\]|\\.)*")')) {
        $code = $m.Groups[2].Value
        if ($results.Contains($code)) { continue }

        $message = $m.Groups[3].Value.TrimStart('$').Trim('"').Replace('\"', '"').Replace('{{', '{').Replace('}}', '}')
        $results[$code] = [pscustomobject]@{ Severity = $m.Groups[1].Value; Message = $message }
    }
}

# Codes whose message is assembled by a helper, described here instead.
$described = @{
    'AYN0002' = @('Warning', "'<device> <key>' names no context; the default is used, or the fact is not reported.")
    'SHB0427' = @('Warning', 'Unknown top-level section; it will be ignored.')
    'SHB0428' = @('Warning', "Unknown setting in a section ('general', 'gaps', 'window-effects' and the like); it will be ignored.")
    'TAJ0013' = @('Warning', "Unknown setting in 'bar'; it will be ignored.")
    'TAJ0014' = @('Warning', 'Unknown setting in a profile; it will be ignored.')
    'TAJ0015' = @('Warning', 'Unknown setting in a zone; it will be ignored.')
    'TAJ0017' = @('Warning', "Unknown setting in a 'when' block; it will be ignored.")
    'TAJ0018' = @('Warning', 'Unknown setting on a source; it will be ignored.')
    'TAJ0019' = @('Warning', 'Unknown setting on a rule; it will be ignored.')
}

foreach ($code in $described.Keys) {
    $results[$code] = [pscustomobject]@{ Severity = $described[$code][0]; Message = $described[$code][1] }
}

# Every code the source mentions should be on the page, one way or the other.
foreach ($file in $files) {
    $text = Get-Content -LiteralPath $file.FullName -Raw

    foreach ($m in [regex]::Matches($text, '"((?:SHB|TAJ|DAL|AYN)\d{4})"')) {
        if (-not $results.Contains($m.Groups[1].Value)) {
            Write-Warning "$($m.Groups[1].Value) in $($file.Name) has no message the script can read; add it to `$described."
        }
    }
}

$sorted = $results.GetEnumerator() | Sort-Object Name

$families = @(
    @('SHB', 'The window manager and the command parser'),
    @('TAJ', 'The bar'),
    @('DAL', 'The palette'),
    @('AYN', 'The watcher')
)

$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('# Diagnostics')
$lines.Add('')
$lines.Add('Every complaint the five programs make about a configuration file has a code, so it')
$lines.Add('can be searched for and talked about. The letters say who is complaining - **SHB** the')
$lines.Add("window manager's loader and command parser, **TAJ** the bar, **DAL** the palette, **AYN**")
$lines.Add('the watcher - and `shubbak check-config` runs all four over one file and prints each')
$lines.Add('with a line, a column and a caret.')
$lines.Add('')
$lines.Add('An **error** stops the file being loaded: the window manager keeps the configuration')
$lines.Add('it had, or starts on the built-in one, and says so. A **warning** is a line the loader')
$lines.Add('could read but suspects - a misspelt key, a name nothing declares, a value it has')
$lines.Add('repaired - and the file loads with that line read as the message says. The message')
$lines.Add('templates below show what is filled in as `{name}`; most come with a hint that names')
$lines.Add('the fix.')
$lines.Add('')
$lines.Add('This page is generated from the source by `tools/list-diagnostics.ps1`; edit the')
$lines.Add('code, not the page.')

foreach ($family in $families) {
    $lines.Add('')
    $lines.Add("## $($family[0]) - $($family[1])")
    $lines.Add('')
    $lines.Add('| Code | Severity | Says |')
    $lines.Add('|---|---|---|')

    foreach ($entry in $sorted | Where-Object { $_.Name.StartsWith($family[0]) }) {
        $message = $entry.Value.Message.Replace('|', '\|')
        $lines.Add("| ``$($entry.Name)`` | $($entry.Value.Severity) | $message |")
    }
}

$content = ($lines -join "`n") + "`n"
[System.IO.File]::WriteAllText($Output, $content, [System.Text.UTF8Encoding]::new($false))

Write-Host "$($sorted.Count) codes written to $Output"
