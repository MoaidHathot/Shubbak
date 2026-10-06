<#
.SYNOPSIS
    Checks that every example script parses, and that every example the docs point
    at exists.

.DESCRIPTION
    The scripts under examples/ are run by people, not by CI: each wants a live window
    manager, a key pressed, a bar watched. What CI can hold them to is cheaper and
    still worth having. Every .ps1 is put through PowerShell's own parser, so a typo
    cannot ship as "the example is broken" - which, for the one thing a newcomer tries
    first, is the worst bug there is. And every examples/ path a docs page or the
    examples' own readme links to has to exist, since a renamed script would otherwise
    leave a dead link that nothing notices until somebody clicks it.

    The C# examples are built by the solution, so they are covered by `dotnet build`.

        pwsh -File tools/check-examples.ps1

    Exits non-zero on the first script that does not parse or link that does not resolve.
#>
[CmdletBinding()]
param(
    [string] $Root = (Join-Path $PSScriptRoot '..')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$examples = Join-Path $Root 'examples'
$failures = 0
$checked = 0

# ---- every script parses ----------------------------------------------------------

foreach ($script in Get-ChildItem -Path $examples -Recurse -Filter *.ps1 -File) {
    $checked++
    $tokens = $null
    $errors = $null

    [System.Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$tokens, [ref]$errors) | Out-Null

    $relative = $script.FullName.Substring($Root.Length).TrimStart('\', '/')

    if ($errors.Count -eq 0) {
        Write-Output ("ok    {0}" -f $relative)
        continue
    }

    $failures++
    Write-Output ("FAIL  {0}" -f $relative)
    foreach ($error in $errors) {
        Write-Output ("        {0}:{1}: {2}" -f $error.Extent.StartLineNumber, $error.Extent.StartColumnNumber, $error.Message)
    }
}

# ---- every link into examples/ resolves -------------------------------------------

# The pages that link to examples: the extending page, the examples' readmes, and the
# top-level readme. A markdown link whose target begins with the examples directory,
# relative to the page it is on.
$pages = @(
    (Join-Path $Root 'README.md'),
    (Join-Path $Root 'docs' 'extending.md')
) + @(Get-ChildItem -Path $examples -Recurse -Filter *.md -File | ForEach-Object { $_.FullName })

foreach ($page in $pages) {
    if (-not (Test-Path $page)) { continue }

    $pageDir = Split-Path $page -Parent
    $text = Get-Content -LiteralPath $page -Raw

    foreach ($match in [regex]::Matches($text, '\]\(([^)#\s]+)(?:#[^)]*)?\)')) {
        $target = $match.Groups[1].Value
        if ($target -match '^[a-z]+:') { continue }                                 # a URL

        $resolved = [System.IO.Path]::GetFullPath((Join-Path $pageDir $target))
        if (-not $resolved.StartsWith([System.IO.Path]::GetFullPath($examples), [System.StringComparison]::OrdinalIgnoreCase)) { continue }

        $checked++
        $relativePage = $page.Substring($Root.Length).TrimStart('\', '/')

        if (Test-Path -LiteralPath $resolved) {
            Write-Output ("ok    {0} -> {1}" -f $relativePage, $target)
        }
        else {
            $failures++
            Write-Output ("FAIL  {0} -> {1} (does not exist)" -f $relativePage, $target)
        }
    }
}

Write-Output ''

if ($failures -gt 0) {
    throw "$failures of $checked example check(s) failed."
}

Write-Output "All $checked example checks pass: every script parses and every link into examples/ resolves."
