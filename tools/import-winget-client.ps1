<#
.SYNOPSIS
    Imports the pinned WinGet client module, with a hash-checked Gallery CDN fallback.
#>

function Import-WinGetClientPinned {
    [CmdletBinding()]
    param(
        [Version] $Version = '1.29.380',
        [string] $CacheRoot = $(if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() })
    )

    try {
        Install-Module -Name Microsoft.WinGet.Client -RequiredVersion $Version -Force -Scope CurrentUser -AllowClobber -Repository PSGallery -ErrorAction Stop
        Import-Module Microsoft.WinGet.Client -RequiredVersion $Version -Global -ErrorAction Stop
        return
    }
    catch {
        Write-Warning "Gallery installation failed for Microsoft.WinGet.Client $Version; trying its pinned CDN package. $($_.Exception.Message)"
    }

    # The Gallery API returned 404 for this version while its official CDN still
    # served the package. Keep exactly the reviewed version, and verify its bytes
    # before extracting or importing code that can see the workflow token.
    $hashes = @{
        '1.29.380' = '3469E5747EB6B100E51FED3F2057386B5BA60BC8955A6669B5C2EB562E316619'
    }
    $expected = $hashes[$Version.ToString()]
    if (-not $expected) { throw "No approved CDN package hash for Microsoft.WinGet.Client $Version." }

    $directory = Join-Path $CacheRoot ("shubbak-winget-client-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $package = Join-Path $directory 'module.nupkg'
    $module = Join-Path $directory 'module'
    try {
        # No authorization header: GH_TOKEN belongs to the module's GitHub requests,
        # not to the public Gallery CDN download.
        $uri = "https://cdn.powershellgallery.com/packages/microsoft.winget.client.$Version.nupkg"
        Invoke-WebRequest -Uri $uri -OutFile $package -TimeoutSec 120 -ErrorAction Stop
        $actual = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash
        if ($actual -ne $expected) { throw "Microsoft.WinGet.Client $Version package hash mismatch: expected $expected, received $actual." }

        # .NET rejects entries that would escape the destination directory.
        [IO.Compression.ZipFile]::ExtractToDirectory($package, $module)
        $manifest = Join-Path $module 'Microsoft.WinGet.Client.psd1'
        Import-Module -Name $manifest -Global -ErrorAction Stop
        Remove-Item -LiteralPath $package -Force
        Write-Output "Imported hash-verified Microsoft.WinGet.Client $Version from the official Gallery CDN."
        # Loaded DLLs keep this private cache in use for the PowerShell process's
        # lifetime. RUNNER_TEMP is discarded with the job; no user module is replaced.
    }
    catch {
        Remove-Item -LiteralPath $directory -Recurse -Force -ErrorAction SilentlyContinue
        throw
    }
}
