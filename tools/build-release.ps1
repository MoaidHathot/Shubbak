<#
.SYNOPSIS
    Builds everything a release ships: the portable zips, the MSIs, their hashes, and
    the winget and Scoop manifests with the real values filled in - for every
    architecture Shubbak targets.

.DESCRIPTION
    One script for the release workflow and for a maintainer's machine, so that
    what CI publishes can be produced and inspected locally before a tag exists.
    Run with no switches to do everything; run with switches to do one stage, which
    is how the workflow interleaves code signing between them:

      -Publish    NativeAOT-publish the five executables and the uiAccess build of
                  the window manager, for each runtime identifier, then check each
                  one: the right machine type in its PE header, the right subsystem,
                  the right manifest, and - when this machine can run it - the right
                  reported version.
      -Stage      Lay out the zip and the MSI contents per architecture under
                  artifacts\stage\<rid> and artifacts\stage-msi\<rid> from the
                  published (by then signed) binaries.
      -Pack       Compress the zips and the symbols archives; build the MSIs.
      -Manifests  Hash the (by then signed) zips and MSIs, write the .sha256 files and
                  SHA256SUMS.txt, and fill the winget and Scoop manifests into
                  artifacts\winget and artifacts\bucket.

    The version comes from Directory.Build.props and nowhere else. Nothing here
    signs anything: signing is the workflow's job, because the credentials live
    there.

.PARAMETER Rids
    The runtime identifiers to build for. Both by default. Publishing for an
    architecture other than this machine's needs that architecture's C++ build tools
    (the linker); -Rids win-x64 is the way to get a complete, fast build on a machine
    without them.

.PARAMETER PortableWm
    Put the ordinary (non-uiAccess) window manager in the MSIs instead of the
    uiAccess build. For testing an unsigned MSI locally: a uiAccess binary that is
    not signed does not start, so an unsigned MSI with the real build is an
    installer whose main program cannot run. Never used for a release.

.PARAMETER SkipTests
    Skip `dotnet test` before publishing. CI has already run them by the time it
    gets here; locally they are the first thing worth knowing.

.EXAMPLE
    .\tools\build-release.ps1
    .\tools\build-release.ps1 -Publish -Rids win-x64
    .\tools\build-release.ps1 -Stage -Pack -Manifests -PortableWm
#>
[CmdletBinding()]
param(
    [switch] $Publish,
    [switch] $Stage,
    [switch] $Pack,
    [switch] $Manifests,
    [switch] $PortableWm,
    [switch] $SkipTests,
    [string] $Configuration = 'Release',
    [ValidateSet('win-x64', 'win-arm64')]
    [string[]] $Rids = @('win-x64', 'win-arm64')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$inActions = $env:GITHUB_ACTIONS -eq 'true'

if (-not ($Publish -or $Stage -or $Pack -or $Manifests)) {
    $Publish = $Stage = $Pack = $Manifests = $true
}

# The executables, by project. The order matters nowhere except in the output.
$projects = [ordered]@{
    'Shubbak.Wm'  = 'shubbak-wm.exe'
    'Shubbak.Cli' = 'shubbak.exe'
    'Taj'         = 'taj.exe'
    'Dalil'       = 'dalil.exe'
    'Ayn'         = 'ayn.exe'
}

# 2 = IMAGE_SUBSYSTEM_WINDOWS_GUI, 3 = IMAGE_SUBSYSTEM_WINDOWS_CUI. The daemon and its
# companions must be GUI or they put a console window on the desktop at every logon.
$subsystems = @{
    'shubbak-wm.exe' = 2
    'shubbak.exe'    = 3
    'taj.exe'        = 2
    'dalil.exe'      = 2
    'ayn.exe'        = 2
}

# IMAGE_FILE_MACHINE_* for the PE header's machine field.
$machines = @{ 'win-x64' = 0x8664; 'win-arm64' = 0xAA64 }

function Get-Arch([string] $rid) { $rid.Substring('win-'.Length) }

function Group-Begin([string] $title) {
    if ($inActions) { Write-Output "::group::$title" } else { Write-Output ''; Write-Output "== $title ==" }
}

function Group-End { if ($inActions) { Write-Output '::endgroup::' } }

function Invoke-Checked([string] $what, [scriptblock] $command) {
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed with exit code $LASTEXITCODE." }
}

function Get-Version {
    $props = [xml](Get-Content (Join-Path $root 'Directory.Build.props'))
    $node = $props.SelectSingleNode('/Project/PropertyGroup/Version')
    if (-not $node) { throw 'Directory.Build.props has no <Version>.' }
    [string]$node.InnerText.Trim()
}

function Read-PeHeader([string] $exe) {
    $stream = [System.IO.File]::OpenRead($exe)
    $reader = New-Object System.IO.BinaryReader($stream)
    try {
        $stream.Position = 0x3C
        $peHeader = $reader.ReadInt32()
        # PE signature (4), then the COFF header, whose first field is the machine.
        $stream.Position = $peHeader + 4
        $machine = $reader.ReadUInt16()
        # PE signature (4) + COFF header (20) + the subsystem's offset in the optional header (68).
        $stream.Position = $peHeader + 4 + 20 + 68
        $subsystem = $reader.ReadUInt16()
        @{ Machine = [int]$machine; Subsystem = [int]$subsystem }
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

function Test-Manifest([string] $exe, [bool] $uiAccess) {
    # The application manifest is embedded as UTF-8 XML, so the attribute can be
    # found with a plain byte search. That is cruder than parsing the resource and
    # exactly as reliable for a yes/no question.
    $text = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($exe))
    $want = if ($uiAccess) { 'uiAccess="true"' } else { 'uiAccess="false"' }
    $unwanted = if ($uiAccess) { 'uiAccess="false"' } else { 'uiAccess="true"' }
    $text.Contains($want) -and -not $text.Contains($unwanted)
}

function Get-ReportedVersion([string] $exe) {
    # Redirected rather than run inline: four of the five are GUI-subsystem
    # binaries, so the shell does not wait for them and their output would arrive
    # after the check had moved on.
    $out = New-TemporaryFile
    try {
        $process = Start-Process -FilePath $exe -ArgumentList '--version' `
            -RedirectStandardOutput $out -NoNewWindow -Wait -PassThru
        if ($process.ExitCode -ne 0) { throw "$exe --version exited $($process.ExitCode)." }
        (Get-Content $out -Raw).Trim()
    }
    finally {
        Remove-Item $out -Force -ErrorAction SilentlyContinue
    }
}

function Test-CanRun([string] $rid) {
    # An ARM64 machine runs x64 binaries through emulation; an x64 machine runs
    # nothing but x64. So the version check happens where it can and the machine-type
    # check happens everywhere, and CI runs the ARM64 binaries on an ARM64 runner.
    switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
        'Arm64' { $true }
        'X64' { (Get-Arch $rid) -eq 'x64' }
        default { $false }
    }
}

function Test-Elevated {
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    ([System.Security.Principal.WindowsPrincipal]$identity).IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-PublishDir([string] $rid, [string] $project) { Join-Path $artifacts "publish\$rid\$project" }
function Get-UiAccessDir([string] $rid) { Join-Path $artifacts "publish-uiaccess\$rid\Shubbak.Wm" }
function Get-StageDir([string] $rid) { Join-Path $artifacts "stage\$rid" }
function Get-StageMsiDir([string] $rid) { Join-Path $artifacts "stage-msi\$rid" }
function Get-SymbolsDir([string] $rid) { Join-Path $artifacts "symbols\$rid" }
function Get-ZipPath([string] $rid) { Join-Path $artifacts "shubbak-$version-$rid.zip" }
function Get-SymbolZipPath([string] $rid) { Join-Path $artifacts "shubbak-$version-$rid-symbols.zip" }
function Get-MsiPath([string] $rid) { Join-Path $artifacts "shubbak-$version-$rid.msi" }

$version = Get-Version
Write-Output "Shubbak $version for $($Rids -join ', ')"

# ---- Publish ---------------------------------------------------------------------

if ($Publish) {
    if (-not $SkipTests) {
        Group-Begin 'Build and test'
        Push-Location $root
        try {
            Invoke-Checked 'dotnet build' { dotnet build --configuration $Configuration --nologo -v q }
            Invoke-Checked 'dotnet test' { dotnet test --no-build --configuration $Configuration --verbosity quiet }
        }
        finally { Pop-Location }
        Group-End
    }

    foreach ($rid in $Rids) {
        foreach ($project in $projects.Keys) {
            Group-Begin "Publish $project ($rid)"
            Invoke-Checked "publish $project ($rid)" {
                dotnet publish (Join-Path $root "src\$project") `
                    --configuration $Configuration `
                    --runtime $rid `
                    -p:PublishAot=true `
                    --output (Get-PublishDir $rid $project) `
                    --nologo
            }
            Group-End
        }

        # The uiAccess variant, into its own directory. It shares no output with the
        # ordinary build - see Shubbak.Wm.csproj - because the manifest is baked into
        # the apphost and a stale one is a binary that cannot start.
        Group-Begin "Publish Shubbak.Wm (uiAccess, $rid)"
        Invoke-Checked "publish Shubbak.Wm (uiAccess, $rid)" {
            dotnet publish (Join-Path $root 'src\Shubbak.Wm') `
                --configuration $Configuration `
                --runtime $rid `
                -p:PublishAot=true `
                -p:ShubbakUiAccess=true `
                --output (Get-UiAccessDir $rid) `
                --nologo
        }
        Group-End
    }

    Group-Begin 'Check the published binaries'
    $expected = "Shubbak $version"

    foreach ($rid in $Rids) {
        $canRun = Test-CanRun $rid
        Write-Output ("  {0}{1}" -f $rid, $(if ($canRun) { '' } else { '  (not runnable on this machine; header checks only)' }))

        foreach ($project in $projects.Keys) {
            $name = $projects[$project]
            $exe = Join-Path (Get-PublishDir $rid $project) $name
            if (-not (Test-Path $exe)) { throw "$project ($rid) did not publish to $exe." }

            $header = Read-PeHeader $exe
            if ($header.Machine -ne $machines[$rid]) {
                throw ("{0} ({1}) has machine type 0x{2:X4}; expected 0x{3:X4}." -f $name, $rid, $header.Machine, $machines[$rid])
            }

            $want = $subsystems[$name]
            if ($header.Subsystem -ne $want) {
                throw "$name ($rid) has subsystem $($header.Subsystem); expected $want. A console daemon puts a window on the desktop at every logon."
            }

            $reported = if ($canRun) { Get-ReportedVersion $exe } else { '(not run)' }
            if ($canRun -and $reported -ne $expected) { throw "$name ($rid) reports '$reported'; expected '$expected'." }

            Write-Output ("    {0,-16} machine 0x{1:X4}, subsystem {2}, {3}" -f $name, $header.Machine, $header.Subsystem, $reported)
        }

        $portableExe = Join-Path (Get-PublishDir $rid 'Shubbak.Wm') 'shubbak-wm.exe'
        if (-not (Test-Manifest $portableExe $false)) { throw "The portable shubbak-wm.exe ($rid) does not carry the asInvoker/uiAccess=false manifest." }
        Write-Output '    shubbak-wm.exe   manifest: uiAccess="false" (portable)'

        # Not run: a uiAccess binary refuses to start outside Program Files, and this
        # one is also not signed yet. What can be checked is what it carries.
        $uiAccessWm = Join-Path (Get-UiAccessDir $rid) 'shubbak-wm.exe'
        if (-not (Test-Path $uiAccessWm)) { throw "The uiAccess build ($rid) did not publish to $uiAccessWm." }
        if (-not (Test-Manifest $uiAccessWm $true)) { throw "The uiAccess shubbak-wm.exe ($rid) does not carry the uiAccess=true manifest." }
        $uiHeader = Read-PeHeader $uiAccessWm
        if ($uiHeader.Machine -ne $machines[$rid]) { throw ("The uiAccess shubbak-wm.exe ({0}) has machine type 0x{1:X4}." -f $rid, $uiHeader.Machine) }
        if ($uiHeader.Subsystem -ne 2) { throw "The uiAccess shubbak-wm.exe ($rid) is not a GUI-subsystem binary." }
        Write-Output ('    shubbak-wm.exe   manifest: uiAccess="true"  (installer), machine 0x{0:X4}, subsystem 2' -f $uiHeader.Machine)
    }
    Group-End
}

# ---- Stage -----------------------------------------------------------------------

if ($Stage) {
    foreach ($rid in $Rids) {
        Group-Begin "Stage the zip and the MSI contents ($rid)"

        $stageDir = Get-StageDir $rid
        $stageMsiDir = Get-StageMsiDir $rid
        $symbolsDir = Get-SymbolsDir $rid

        foreach ($dir in $stageDir, $stageMsiDir, $symbolsDir) {
            if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
        }

        # Only the executables. NativeAOT emits a .pdb roughly four times the size of
        # the binary it describes, plus an XML doc file per referenced project -
        # together about 90% of the publish directory and none of it any use to
        # somebody running a window manager. The pdbs go to the symbols archive.
        foreach ($project in $projects.Keys) {
            Copy-Item (Join-Path (Get-PublishDir $rid $project) '*.exe') $stageDir
            Copy-Item (Join-Path (Get-PublishDir $rid $project) '*.pdb') $symbolsDir -ErrorAction SilentlyContinue
        }

        # What someone needs in front of them the first time they run it, plus the
        # pictures the readme points at, at the path it points at them by.
        Copy-Item (Join-Path $root 'LICENSE') $stageDir
        Copy-Item (Join-Path $root 'README.md') $stageDir
        Copy-Item (Join-Path $root 'docs\shubbak.example.kdl') $stageDir
        New-Item -ItemType Directory -Path (Join-Path $stageDir 'docs\assets') -Force | Out-Null
        Copy-Item (Join-Path $root 'docs\assets\*.png') (Join-Path $stageDir 'docs\assets')
        Copy-Item (Join-Path $root 'docs\*.md') (Join-Path $stageDir 'docs')

        # The MSI: the same, minus the readme and its pictures, plus the uiAccess
        # window manager in place of the portable one. The installer is the only place
        # that build can run, so it is the only place it goes.
        New-Item -ItemType Directory -Path (Join-Path $stageMsiDir 'docs') -Force | Out-Null
        foreach ($project in $projects.Keys) {
            if ($project -eq 'Shubbak.Wm') { continue }
            Copy-Item (Join-Path (Get-PublishDir $rid $project) '*.exe') $stageMsiDir
        }

        if ($PortableWm) {
            Write-Warning "Staging the portable window manager into the $rid MSI (-PortableWm). This installer is for local testing only."
            Copy-Item (Join-Path (Get-PublishDir $rid 'Shubbak.Wm') 'shubbak-wm.exe') $stageMsiDir
        }
        else {
            $uiAccessWm = Join-Path (Get-UiAccessDir $rid) 'shubbak-wm.exe'
            if (-not (Test-Manifest $uiAccessWm $true)) { throw "The window manager at $uiAccessWm does not carry the uiAccess manifest; refusing to build an installer around it." }
            Copy-Item $uiAccessWm $stageMsiDir
            Copy-Item (Join-Path (Get-UiAccessDir $rid) 'shubbak-wm.pdb') (Join-Path $symbolsDir 'shubbak-wm.uiaccess.pdb') -ErrorAction SilentlyContinue
        }

        Copy-Item (Join-Path $root 'LICENSE') $stageMsiDir
        Copy-Item (Join-Path $root 'docs\shubbak.example.kdl') $stageMsiDir
        Copy-Item (Join-Path $root 'docs\*.md') (Join-Path $stageMsiDir 'docs')

        Write-Output "  zip: $((Get-ChildItem $stageDir -File -Recurse | ForEach-Object { $_.FullName.Substring($stageDir.Length + 1) }) -join ', ')"
        Write-Output "  msi: $((Get-ChildItem $stageMsiDir -File -Recurse | ForEach-Object { $_.FullName.Substring($stageMsiDir.Length + 1) }) -join ', ')"
        Group-End
    }
}

# ---- Pack ------------------------------------------------------------------------

if ($Pack) {
    $elevated = Test-Elevated
    if (-not $elevated) { Write-Warning 'Not elevated: skipping ICE validation of the MSIs. The release workflow runs it.' }

    foreach ($rid in $Rids) {
        $arch = Get-Arch $rid
        $zip = Get-ZipPath $rid
        $symbolZip = Get-SymbolZipPath $rid

        Group-Begin "Pack the zip ($rid)"
        # Flat, with no directory prefix: both winget's NestedInstallerFiles and
        # Scoop's bin array name paths inside the archive, and a version-stamped
        # top-level folder would have to be edited into both on every release.
        Compress-Archive -Path (Join-Path (Get-StageDir $rid) '*') -DestinationPath $zip -Force
        Compress-Archive -Path (Join-Path (Get-SymbolsDir $rid) '*') -DestinationPath $symbolZip -Force
        Write-Output ("  {0}  {1:N2} MB" -f (Split-Path $zip -Leaf), ((Get-Item $zip).Length / 1MB))
        Write-Output ("  {0}  {1:N2} MB" -f (Split-Path $symbolZip -Leaf), ((Get-Item $symbolZip).Length / 1MB))
        Group-End

        Group-Begin "Build the MSI ($rid)"
        $wixArgs = @(
            'build', (Join-Path $root 'packaging\msi\Shubbak.Installer.wixproj'),
            '--configuration', $Configuration,
            "-p:StageDir=$(Get-StageMsiDir $rid)",
            "-p:InstallerPlatform=$arch",
            '--nologo'
        )

        # ICE validation needs an elevated process, and it needs Windows Installer to
        # be able to open the package as one it could install - which an x64 machine
        # cannot do for an ARM64 package ("not supported by this processor type"). The
        # authoring is identical for both, so the x64 run covers it; the ARM64 package
        # is installed and uninstalled for real on an ARM64 runner instead.
        $hostIsArm64 = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64'
        if (-not $elevated -or ($arch -eq 'arm64' -and -not $hostIsArm64)) {
            $wixArgs += '-p:SuppressValidation=true'
        }

        Invoke-Checked "MSI build ($rid)" { dotnet @wixArgs }

        $built = Join-Path $artifacts "msi\shubbak-$version-$rid.msi"
        if (-not (Test-Path $built)) { throw "The MSI did not build to $built." }
        Copy-Item $built (Get-MsiPath $rid) -Force
        Write-Output ("  {0}  {1:N2} MB" -f (Split-Path (Get-MsiPath $rid) -Leaf), ((Get-Item (Get-MsiPath $rid)).Length / 1MB))
        Group-End
    }
}

# ---- Manifests -------------------------------------------------------------------

if ($Manifests) {
    Group-Begin 'Hash the packages and fill the manifests'
    . (Join-Path $PSScriptRoot 'Msi.ps1')
    . (Join-Path $PSScriptRoot 'WingetManifest.ps1')

    # The packages, by architecture and kind, from whichever RIDs were built. A
    # manifest entry for an architecture that was not built keeps its placeholder,
    # and the check below then fails - a release is all architectures or nothing.
    $hashes = @{}
    $productCodes = @{}
    $sums = [System.Collections.Generic.List[string]]::new()

    foreach ($rid in $Rids) {
        $arch = Get-Arch $rid
        foreach ($file in (Get-ZipPath $rid), (Get-MsiPath $rid)) {
            if (-not (Test-Path $file)) { throw "$file does not exist; run with -Pack first." }
            $hash = (Get-FileHash $file -Algorithm SHA256).Hash
            $leaf = Split-Path $file -Leaf
            $kind = [System.IO.Path]::GetExtension($file).TrimStart('.')
            $hashes["$arch/$kind"] = $hash
            # winget will not accept a manifest without the hash, and it is what tells
            # anyone downloading by hand that they got what this build produced.
            Set-Content "$file.sha256" "$hash  $leaf"
            $sums.Add("$hash  $leaf")
            Write-Output ("  {0,-34} {1}" -f $leaf, $hash)
        }

        $msi = Get-MsiPath $rid
        $productCodes[$arch] = Get-MsiProperty -Path $msi -Name 'ProductCode'
        $msiVersion = Get-MsiProperty -Path $msi -Name 'ProductVersion'
        if ($msiVersion -ne $version) { throw "The $rid MSI says it is version $msiVersion; the tree says $version." }
        Write-Output ("  {0,-34} ProductCode {1}" -f (Split-Path $msi -Leaf), $productCodes[$arch])
    }

    Set-Content (Join-Path $artifacts 'SHA256SUMS.txt') ($sums -join "`n")
    $releaseDate = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd')

    # The manifests in packaging\ are templates with placeholders for the values only
    # a build can know. Filled copies go to artifacts\, and the post-release workflow
    # commits them back. Each value lands in the entry it belongs to, by architecture
    # and installer type.
    $wingetOut = Join-Path $artifacts 'winget'
    if (Test-Path $wingetOut) { Remove-Item $wingetOut -Recurse -Force }
    New-Item -ItemType Directory -Path $wingetOut | Out-Null
    Copy-Item (Join-Path $root 'packaging\winget\*.yaml') $wingetOut

    Update-WingetInstallerManifest -Path (Join-Path $wingetOut 'MoaidHathot.Shubbak.installer.yaml') -Resolve {
        param($field, $arch, $type, $current)
        switch ($field) {
            'InstallerSha256' {
                $kind = if ($type -eq 'zip') { 'zip' } else { 'msi' }
                if ($hashes.ContainsKey("$arch/$kind")) { $hashes["$arch/$kind"] } else { $null }
            }
            'ProductCode' { if ($productCodes.ContainsKey($arch)) { "'$($productCodes[$arch])'" } else { $null } }
            'ReleaseDate' { $releaseDate }
            default { $null }
        }
    }

    foreach ($installer in Read-WingetInstallers -Path (Join-Path $wingetOut 'MoaidHathot.Shubbak.installer.yaml')) {
        if (-not $installer.InstallerSha256 -or (Test-PlaceholderHash $installer.InstallerSha256)) {
            throw "The $($installer.Architecture) $($installer.InstallerType) entry still has a placeholder hash. Build every architecture the manifest lists."
        }
        if ($installer.InstallerType -eq 'wix' -and $installer.ProductCode -eq (Get-PlaceholderProductCode)) {
            throw "The $($installer.Architecture) MSI entry still has a placeholder ProductCode."
        }
    }

    if (Get-Command winget -ErrorAction SilentlyContinue) {
        Invoke-Checked 'winget validate' { winget validate --manifest $wingetOut }
    }
    else {
        Write-Warning 'winget is not on PATH; the filled manifests were not validated here.'
    }

    # Scoop: the bucket manifest, with the hash Scoop expects in lower case, in the
    # block for each architecture. Line by line, so the file keeps its shape and its
    # history stays readable.
    $bucketOut = Join-Path $artifacts 'bucket'
    if (Test-Path $bucketOut) { Remove-Item $bucketOut -Recurse -Force }
    New-Item -ItemType Directory -Path $bucketOut | Out-Null

    $scoopArch = $null
    $scoopLines = Get-Content (Join-Path $root 'bucket\shubbak.json')
    for ($i = 0; $i -lt $scoopLines.Count; $i++) {
        if ($scoopLines[$i] -match '^\s*"(64bit|arm64)":\s*\{') { $scoopArch = if ($Matches[1] -eq '64bit') { 'x64' } else { 'arm64' }; continue }
        if ($scoopArch -and $scoopLines[$i] -match '^(\s*"hash":\s*")[0-9a-fA-F]{64}(".*)$') {
            if ($hashes.ContainsKey("$scoopArch/zip")) {
                $scoopLines[$i] = $Matches[1] + $hashes["$scoopArch/zip"].ToLowerInvariant() + $Matches[2]
            }
            $scoopArch = $null
        }
    }
    [System.IO.File]::WriteAllText((Join-Path $bucketOut 'shubbak.json'), (($scoopLines -join "`n") + "`n"), [System.Text.UTF8Encoding]::new($false))

    $scoop = Get-Content (Join-Path $bucketOut 'shubbak.json') -Raw | ConvertFrom-Json
    foreach ($block in $scoop.architecture.PSObject.Properties) {
        if (Test-PlaceholderHash $block.Value.hash) { throw "The Scoop manifest's $($block.Name) block still has a placeholder hash." }
    }

    Write-Output "  filled: $wingetOut, $bucketOut"

    if ($inActions) {
        foreach ($key in $hashes.Keys) {
            "$($key -replace '/', '_')_sha256=$($hashes[$key])" >> $env:GITHUB_OUTPUT
        }
        "sha256sums<<SHA256SUMS" >> $env:GITHUB_OUTPUT
        $sums >> $env:GITHUB_OUTPUT
        "SHA256SUMS" >> $env:GITHUB_OUTPUT
    }
    Group-End
}
