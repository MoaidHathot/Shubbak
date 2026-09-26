<#
.SYNOPSIS
    Checks - or fixes - the test count docs/architecture.md states.

.DESCRIPTION
    CI counts every [Fact] and [Theory] under tests/ and fails when the two figures in
    docs/architecture.md disagree with it, because the count was wrong on every
    occasion somebody looked. This is the same count, for the person about to push.

        pwsh -File tools/check-test-count.ps1          # says whether the page is right
        pwsh -File tools/check-test-count.ps1 -Fix     # and rewrites the figures if not

    Exits non-zero when the page is wrong and -Fix was not given.
#>
[CmdletBinding()]
param(
    [string] $Root = (Join-Path $PSScriptRoot '..'),
    [switch] $Fix
)

$ErrorActionPreference = 'Stop'

$page = Join-Path $Root 'docs' 'architecture.md'

# Recursive, and the same pattern CI uses: anchored to the start of a line, so an
# `Except([Fact.CameraInUse` in a test body is not counted as a test.
$files = Get-ChildItem -Path (Join-Path $Root 'tests') -Recurse -Filter *.cs -File |
    Where-Object { $_.FullName -notmatch '\\obj\\|\\bin\\' }

$declared = ($files | Select-String -Pattern '^\s*\[(Fact|Theory)' -AllMatches).Count

$text = Get-Content -LiteralPath $page -Raw
$claimed = [regex]::Matches($text, '(\d+) test methods') | ForEach-Object { [int]$_.Groups[1].Value } | Sort-Object -Unique

if ($claimed.Count -eq 0) {
    throw "$page no longer states a test count; CI checks for one."
}

$wrong = @($claimed | Where-Object { $_ -ne $declared })

if ($wrong.Count -eq 0) {
    Write-Host "docs/architecture.md says $declared test methods, and the tree declares $declared."
    exit 0
}

if (-not $Fix) {
    Write-Host "docs/architecture.md says $($claimed -join ', ') test methods; the tree declares $declared."
    Write-Host "Run with -Fix to rewrite the figures."
    exit 1
}

$updated = [regex]::Replace($text, '\d+ test methods', "$declared test methods")
[System.IO.File]::WriteAllText($page, $updated, [System.Text.UTF8Encoding]::new($false))

Write-Host "docs/architecture.md now says $declared test methods."
