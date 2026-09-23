<#
.SYNOPSIS
    Prints one line of line coverage per assembly from the Cobertura files coverlet
    writes, and a total.

.DESCRIPTION
    `dotnet test --collect:"XPlat Code Coverage"` leaves one coverage.cobertura.xml per
    test project under a GUID-named folder. This reads every one, adds up the lines
    per assembly across them - a library is often exercised by several test projects -
    and prints a table sorted by assembly name, so the CI log answers "how covered is
    the bar's host" without anyone downloading an artefact.

    Informational. Nothing here fails the build; see the note beside the Test step in
    build.yml for why a threshold was deliberately not added.

.PARAMETER Root
    The results directory the tests were told to write to.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Root
)

$ErrorActionPreference = 'Stop'

$files = Get-ChildItem -Path $Root -Recurse -Filter 'coverage.cobertura.xml' -File

if ($files.Count -eq 0) {
    Write-Warning "No coverage files under $Root; was the test step run with --collect?"
    return
}

# Assembly -> [covered lines, total lines]. Lines are counted from the classes so the
# same assembly reported by several test projects is summed rather than averaged.
$totals = @{}

foreach ($file in $files) {
    [xml] $report = Get-Content -LiteralPath $file.FullName -Raw

    foreach ($package in $report.coverage.packages.package) {
        $name = [string] $package.name

        # coverlet names the package after the assembly; the test assemblies themselves
        # are not interesting.
        if ($name -like '*.Tests') { continue }

        $covered = 0
        $total = 0

        foreach ($class in $package.classes.class) {
            foreach ($line in $class.lines.line) {
                $total++
                if ([int] $line.hits -gt 0) { $covered++ }
            }
        }

        if (-not $totals.ContainsKey($name)) { $totals[$name] = @(0, 0) }

        # A line seen by two test projects is one line; the per-class totals are the
        # same in each file, so keep the maximum covered count and one total.
        $totals[$name] = @([Math]::Max($totals[$name][0], $covered), [Math]::Max($totals[$name][1], $total))
    }
}

$allCovered = 0
$allTotal = 0

Write-Output ''
Write-Output ('{0,-24} {1,8} {2,8} {3,8}' -f 'Assembly', 'Covered', 'Lines', 'Percent')
Write-Output ('{0,-24} {1,8} {2,8} {3,8}' -f '--------', '-------', '-----', '-------')

foreach ($name in ($totals.Keys | Sort-Object)) {
    $covered, $total = $totals[$name]
    $allCovered += $covered
    $allTotal += $total

    $percent = if ($total -eq 0) { 0 } else { [Math]::Round(100.0 * $covered / $total, 1) }
    Write-Output ('{0,-24} {1,8} {2,8} {3,7}%' -f $name, $covered, $total, $percent)
}

$allPercent = if ($allTotal -eq 0) { 0 } else { [Math]::Round(100.0 * $allCovered / $allTotal, 1) }
Write-Output ('{0,-24} {1,8} {2,8} {3,7}%' -f 'total', $allCovered, $allTotal, $allPercent)
Write-Output ''
