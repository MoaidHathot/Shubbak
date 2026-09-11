<#
.SYNOPSIS
    Checks that every KDL snippet in the readme and the docs loads without a
    diagnostic.

.DESCRIPTION
    The documentation shows configuration, and configuration rots: a setting is
    renamed, a section moves, and the snippet a newcomer copies first is the one that
    no longer parses. So every ```kdl block in the given Markdown files is written out
    and run through `shubbak check-config`, and a warning is as much a failure as an
    error - the documentation should not teach anything the parser then complains
    about.

    A block whose first line is a bare `bind` is wrapped in `keybindings { }`, because
    that is how the docs show a single binding. Everything else must be a complete
    file on its own. Blocks marked with a preceding HTML comment
    `<!-- fragment -->` are skipped.

.PARAMETER Cli
    Path to shubbak.exe. Defaults to the Release build under src/Shubbak.Cli/bin.

.EXAMPLE
    .\tools\check-doc-snippets.ps1
#>
[CmdletBinding()]
param(
    [string] $Cli,
    [string[]] $Files = @('README.md', 'docs\getting-started.md', 'docs\configuration.md', 'docs\taj.md', 'docs\dalil.md', 'docs\ayn.md', 'docs\scripting.md')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot

if (-not $Cli) {
    # This machine's architecture, because a tree that has been published for both
    # holds an ARM64 shubbak.exe too and an x64 machine cannot run that one.
    $rid = 'win-' + [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()

    # The published, native CLI when a release build has been made - which is what
    # ships, and which also survives the publish: a NativeAOT publish removes the
    # managed apphost that `dotnet build` left in bin, so after tools/build-release.ps1
    # the artifacts directory is the one place a runnable shubbak.exe is certain to be.
    $candidates = @(
        (Join-Path $root "artifacts\publish\$rid\Shubbak.Cli\shubbak.exe"),
        (Join-Path $root "src\Shubbak.Cli\bin\Release\net10.0-windows10.0.19041.0\$rid\shubbak.exe")
    )
    $Cli = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1

    if (-not $Cli) { throw "shubbak.exe was not found at any of:`n  $($candidates -join "`n  ")`nBuild first, or pass -Cli." }
}

$scratch = Join-Path ([System.IO.Path]::GetTempPath()) ("shubbak-doc-snippets-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null

$failures = 0
$checked = 0

try {
    foreach ($relative in $Files) {
        $path = Join-Path $root $relative
        if (-not (Test-Path $path)) { throw "$relative does not exist." }

        $text = [System.IO.File]::ReadAllText($path)
        $index = 0

        foreach ($match in [regex]::Matches($text, '(?s)(<!--\s*fragment\s*-->\s*)?```kdl\r?\n(.*?)```')) {
            $index++
            if ($match.Groups[1].Success) {
                Write-Output ("skip  {0,-26} block {1} (fragment)" -f $relative, $index)
                continue
            }

            $body = $match.Groups[2].Value
            $first = ($body -split "`n" | Where-Object { $_.Trim() -and -not $_.Trim().StartsWith('//') } | Select-Object -First 1)
            if ($first -and $first.Trim() -match '^bind ') { $body = "keybindings {`n$body`n}" }

            $file = Join-Path $scratch (($relative -replace '[\\/.]', '_') + "-$index.kdl")
            [System.IO.File]::WriteAllText($file, $body)

            $output = (& $Cli check-config $file 2>&1 | Out-String)
            $exit = $LASTEXITCODE
            $checked++

            if ($exit -ne 0 -or $output -match '\bwarning\b') {
                $failures++
                Write-Output ("FAIL  {0,-26} block {1}" -f $relative, $index)
                $output -split "`n" | Where-Object { $_ -match 'error|warning' } | ForEach-Object { '        ' + ($_ -replace [regex]::Escape($scratch + '\'), '') }
            }
            else {
                Write-Output ("ok    {0,-26} block {1}" -f $relative, $index)
            }
        }
    }
}
finally {
    Remove-Item $scratch -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Output ''
if ($failures -gt 0) { throw "$failures of $checked documentation snippet(s) do not load cleanly." }
Write-Output "All $checked documentation snippets load without a diagnostic."
