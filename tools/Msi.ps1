<#
.SYNOPSIS
    Reads rows out of an MSI database without installing it.

.DESCRIPTION
    Dot-source this file to get Get-MsiTable and Get-MsiProperty. They open the
    package read-only through the Windows Installer automation interface, so they
    work on any Windows machine with no elevation and no extra tools - which is what
    lets the release script read the ProductCode into the winget manifest, and lets
    anyone check what an installer will do before running it.

    Windows Installer SQL has no IN operator and no ORDER BY worth relying on, so
    the functions take a plain table or a whole SELECT and leave filtering to
    PowerShell.

.EXAMPLE
    . tools\Msi.ps1
    Get-MsiProperty artifacts\msi\shubbak-0.10.0-win-x64.msi ProductCode
    Get-MsiTable artifacts\msi\shubbak-0.10.0-win-x64.msi File | Format-Table
#>

Set-StrictMode -Version Latest

function Get-MsiTable {
    <#
    .SYNOPSIS
        Returns every row of a table, or of an arbitrary SELECT, as objects.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Table,
        [string[]] $Columns
    )

    $Path = (Resolve-Path -LiteralPath $Path).Path

    $installer = New-Object -ComObject WindowsInstaller.Installer
    try {
        # 0 = msiOpenDatabaseModeReadOnly.
        $database = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, [object[]]@($Path, 0))

        if (-not $Columns) {
            $Columns = @(Get-MsiTable -Path $Path -Table '_Columns' -Columns 'Table', 'Name' |
                Where-Object { $_.Table -eq $Table } |
                ForEach-Object { $_.Name })

            if ($Columns.Count -eq 0) { throw "The package has no table named '$Table'." }
        }

        # Windows Installer SQL quotes identifiers with backquotes. Built with a
        # variable rather than inline, because a backquote inside a PowerShell
        # double-quoted string is an escape character and doubling it reads wrong.
        $bt = [string][char]96
        $sql = 'SELECT ' + (($Columns | ForEach-Object { "$bt$_$bt" }) -join ', ') + " FROM $bt$Table$bt"
        $view = $database.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $database, [object[]]@($sql))
        $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, [object[]]@($null)) | Out-Null

        while ($true) {
            $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
            if ($null -eq $record) { break }

            $row = [ordered]@{}
            for ($i = 0; $i -lt $Columns.Count; $i++) {
                $row[$Columns[$i]] = $record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, [object[]]@([int]($i + 1)))
            }
            [pscustomobject]$row
        }

        $view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null) | Out-Null
    }
    finally {
        [System.Runtime.InteropServices.Marshal]::ReleaseComObject($installer) | Out-Null
    }
}

function Get-MsiProperty {
    <#
    .SYNOPSIS
        Returns one value from the Property table, or $null if it is not set.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Name
    )

    Get-MsiTable -Path $Path -Table 'Property' -Columns 'Property', 'Value' |
        Where-Object { $_.Property -eq $Name } |
        Select-Object -First 1 -ExpandProperty Value
}
