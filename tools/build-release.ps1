<#
.SYNOPSIS
    Builds everything a release ships: the portable zip, the MSI, their hashes, and
    the winget and Scoop manifests with the real values filled in.

.DESCRIPTION
    One script for the release workflow and for a maintainer's machine, so that
    what CI publishes can be produced and inspected locally before a tag exists.
    Run with no switches to do everything; run with switches to do one stage, which
    is how the workflow interleaves code signing between them:

      -Publish    NativeAOT-publish the five executables and the uiAccess build of
                  the window manager, then check each one: reports the right
                  version, has the right PE subsystem, carries the right manifest.
      -Stage      Lay out the zip and the MSI contents under artifacts\stage and
                  artifacts\stage-msi from the published (by then signed) binaries.
      -Pack       Compress the zip and the symbols archive; build the MSI.
      -Manifests  Hash the (by then signed) zip and MSI, write the .sha256 files,
                  and fill the winget and Scoop manifests into artifacts\winget and
                  artifacts\bucket.

    The version comes from Directory.Build.props and nowhere else. Nothing here
    signs anything: signing is the workflow's job, because the credentials live
    there.

.PARAMETER PortableWm
    Put the ordinary (non-uiAccess) window manager in the MSI instead of the
    uiAccess build. For testing an unsigned MSI locally: a uiAccess binary that is
    not signed does not start, so an unsigned MSI with the real build is an
    installer whose main program cannot run. Never used for a release.

.PARAMETER SkipTests
    Skip `dotnet test` before publishing. CI has already run them by the time it
    gets here; locally they are the first thing worth knowing.

.EXAMPLE
    .\tools\build-release.ps1
    .\tools\build-release.ps1 -Publish
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
    [string] $Configuration = 'Release'
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

$publishDir = Join-Path $artifacts 'publish'
$uiAccessDir = Join-Path $artifacts 'publish-uiaccess\Shubbak.Wm'
$stageDir = Join-Path $artifacts 'stage'
$stageMsiDir = Join-Path $artifacts 'stage-msi'
$symbolsDir = Join-Path $artifacts 'symbols'

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

function Get-Subsystem([string] $exe) {
    $stream = [System.IO.File]::OpenRead($exe)
    $reader = New-Object System.IO.BinaryReader($stream)
    try {
        $stream.Position = 0x3C
        $peHeader = $reader.ReadInt32()
        # PE signature (4) + COFF header (20) + the subsystem's offset in the optional header (68).
        $stream.Position = $peHeader + 4 + 20 + 68
        $reader.ReadUInt16()
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

function Test-Elevated {
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    ([System.Security.Principal.WindowsPrincipal]$identity).IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

$version = Get-Version
Write-Output "Shubbak $version"

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

    foreach ($project in $projects.Keys) {
        Group-Begin "Publish $project"
        Invoke-Checked "publish $project" {
            dotnet publish (Join-Path $root "src\$project") `
                --configuration $Configuration `
                --runtime win-x64 `
                -p:PublishAot=true `
                --output (Join-Path $publishDir $project) `
                --nologo
        }
        Group-End
    }

    # The uiAccess variant, into its own directory. It shares no output with the
    # ordinary build - see Shubbak.Wm.csproj - because the manifest is baked into the
    # apphost and a stale one is a binary that cannot start.
    Group-Begin 'Publish Shubbak.Wm (uiAccess)'
    Invoke-Checked 'publish Shubbak.Wm (uiAccess)' {
        dotnet publish (Join-Path $root 'src\Shubbak.Wm') `
            --configuration $Configuration `
            --runtime win-x64 `
            -p:PublishAot=true `
            -p:ShubbakUiAccess=true `
            --output $uiAccessDir `
            --nologo
    }
    Group-End

    Group-Begin 'Check the published binaries'
    $expected = "Shubbak $version"

    foreach ($project in $projects.Keys) {
        $exe = Join-Path $publishDir "$project\$($projects[$project])"
        if (-not (Test-Path $exe)) { throw "$project did not publish to $exe." }

        $reported = Get-ReportedVersion $exe
        if ($reported -ne $expected) { throw "$($projects[$project]) reports '$reported'; expected '$expected'." }

        $subsystem = Get-Subsystem $exe
        $want = $subsystems[$projects[$project]]
        if ($subsystem -ne $want) {
            throw "$($projects[$project]) has subsystem $subsystem; expected $want. A console daemon puts a window on the desktop at every logon."
        }

        Write-Output ("  {0,-16} {1}, subsystem {2}" -f $projects[$project], $reported, $subsystem)
    }

    $portableExe = Join-Path $publishDir 'Shubbak.Wm\shubbak-wm.exe'
    if (-not (Test-Manifest $portableExe $false)) { throw 'The portable shubbak-wm.exe does not carry the asInvoker/uiAccess=false manifest.' }
    Write-Output '  shubbak-wm.exe   manifest: uiAccess="false" (portable)'

    # Not run: a uiAccess binary refuses to start outside Program Files, and this one
    # is also not signed yet. What can be checked is what it carries.
    $uiAccessWm = Join-Path $uiAccessDir 'shubbak-wm.exe'
    if (-not (Test-Path $uiAccessWm)) { throw "The uiAccess build did not publish to $uiAccessWm." }
    if (-not (Test-Manifest $uiAccessWm $true)) { throw 'The uiAccess shubbak-wm.exe does not carry the uiAccess=true manifest.' }
    if ((Get-Subsystem $uiAccessWm) -ne 2) { throw 'The uiAccess shubbak-wm.exe is not a GUI-subsystem binary.' }
    Write-Output '  shubbak-wm.exe   manifest: uiAccess="true"  (installer), subsystem 2'
    Group-End
}

# ---- Stage -----------------------------------------------------------------------

if ($Stage) {
    Group-Begin 'Stage the zip and the MSI contents'

    foreach ($dir in $stageDir, $stageMsiDir, $symbolsDir) {
        if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }

    # Only the executables. NativeAOT emits a .pdb roughly four times the size of the
    # binary it describes, plus an XML doc file per referenced project - together
    # about 90% of the publish directory and none of it any use to somebody running
    # a window manager. The pdbs go to the symbols archive.
    foreach ($project in $projects.Keys) {
        Copy-Item (Join-Path $publishDir "$project\*.exe") $stageDir
        Copy-Item (Join-Path $publishDir "$project\*.pdb") $symbolsDir -ErrorAction SilentlyContinue
    }

    # What someone needs in front of them the first time they run it, plus the
    # pictures the readme points at, at the path it points at them by.
    Copy-Item (Join-Path $root 'LICENSE') $stageDir
    Copy-Item (Join-Path $root 'README.md') $stageDir
    Copy-Item (Join-Path $root 'docs\shubbak.example.kdl') $stageDir
    New-Item -ItemType Directory -Path (Join-Path $stageDir 'docs\assets') -Force | Out-Null
    Copy-Item (Join-Path $root 'docs\assets\*.png') (Join-Path $stageDir 'docs\assets')
    Copy-Item (Join-Path $root 'docs\*.md') (Join-Path $stageDir 'docs')

    # The MSI: the same, minus the readme and its pictures, plus the uiAccess window
    # manager in place of the portable one. The installer is the only place that
    # build can run, so it is the only place it goes.
    New-Item -ItemType Directory -Path (Join-Path $stageMsiDir 'docs') -Force | Out-Null
    foreach ($project in $projects.Keys) {
        if ($project -eq 'Shubbak.Wm') { continue }
        Copy-Item (Join-Path $publishDir "$project\*.exe") $stageMsiDir
    }

    if ($PortableWm) {
        Write-Warning 'Staging the portable window manager into the MSI (-PortableWm). This installer is for local testing only.'
        Copy-Item (Join-Path $publishDir 'Shubbak.Wm\shubbak-wm.exe') $stageMsiDir
    }
    else {
        $uiAccessWm = Join-Path $uiAccessDir 'shubbak-wm.exe'
        if (-not (Test-Manifest $uiAccessWm $true)) { throw "The window manager at $uiAccessWm does not carry the uiAccess manifest; refusing to build an installer around it." }
        Copy-Item $uiAccessWm $stageMsiDir
        Copy-Item (Join-Path $uiAccessDir 'shubbak-wm.pdb') (Join-Path $symbolsDir 'shubbak-wm.uiaccess.pdb') -ErrorAction SilentlyContinue
    }

    Copy-Item (Join-Path $root 'LICENSE') $stageMsiDir
    Copy-Item (Join-Path $root 'docs\shubbak.example.kdl') $stageMsiDir
    Copy-Item (Join-Path $root 'docs\*.md') (Join-Path $stageMsiDir 'docs')

    Write-Output "  zip: $((Get-ChildItem $stageDir -File -Recurse | ForEach-Object { $_.FullName.Substring($stageDir.Length + 1) }) -join ', ')"
    Write-Output "  msi: $((Get-ChildItem $stageMsiDir -File -Recurse | ForEach-Object { $_.FullName.Substring($stageMsiDir.Length + 1) }) -join ', ')"
    Group-End
}

# ---- Pack ------------------------------------------------------------------------

$zip = Join-Path $artifacts "shubbak-$version-win-x64.zip"
$symbolZip = Join-Path $artifacts "shubbak-$version-win-x64-symbols.zip"
$msi = Join-Path $artifacts "shubbak-$version-win-x64.msi"

if ($Pack) {
    Group-Begin 'Pack the zip'
    # Flat, with no directory prefix: both winget's NestedInstallerFiles and Scoop's
    # bin array name paths inside the archive, and a version-stamped top-level folder
    # would have to be edited into both on every release.
    Compress-Archive -Path (Join-Path $stageDir '*') -DestinationPath $zip -Force
    Compress-Archive -Path (Join-Path $symbolsDir '*') -DestinationPath $symbolZip -Force
    Write-Output ("  {0}  {1:N2} MB" -f (Split-Path $zip -Leaf), ((Get-Item $zip).Length / 1MB))
    Write-Output ("  {0}  {1:N2} MB" -f (Split-Path $symbolZip -Leaf), ((Get-Item $symbolZip).Length / 1MB))
    Group-End

    Group-Begin 'Build the MSI'
    $wixArgs = @(
        'build', (Join-Path $root 'packaging\msi\Shubbak.Installer.wixproj'),
        '--configuration', $Configuration,
        "-p:StageDir=$stageMsiDir",
        '--nologo'
    )

    # ICE validation needs an elevated process. CI runs elevated; a maintainer's
    # shell usually does not, and the build must still be possible there.
    if (-not (Test-Elevated)) {
        Write-Warning 'Not elevated: skipping ICE validation of the MSI. The release workflow runs it.'
        $wixArgs += '-p:SuppressValidation=true'
    }

    Invoke-Checked 'MSI build' { dotnet @wixArgs }

    $built = Join-Path $artifacts "msi\shubbak-$version-win-x64.msi"
    if (-not (Test-Path $built)) { throw "The MSI did not build to $built." }
    Copy-Item $built $msi -Force
    Write-Output ("  {0}  {1:N2} MB" -f (Split-Path $msi -Leaf), ((Get-Item $msi).Length / 1MB))
    Group-End
}

# ---- Manifests -------------------------------------------------------------------

if ($Manifests) {
    Group-Begin 'Hash the packages and fill the manifests'
    . (Join-Path $PSScriptRoot 'Msi.ps1')

    foreach ($file in $zip, $msi) {
        if (-not (Test-Path $file)) { throw "$file does not exist; run with -Pack first." }
    }

    $zipHash = (Get-FileHash $zip -Algorithm SHA256).Hash
    $msiHash = (Get-FileHash $msi -Algorithm SHA256).Hash
    $productCode = Get-MsiProperty -Path $msi -Name 'ProductCode'
    $msiVersion = Get-MsiProperty -Path $msi -Name 'ProductVersion'
    if ($msiVersion -ne $version) { throw "The MSI says it is version $msiVersion; the tree says $version." }
    $releaseDate = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd')

    # winget will not accept a manifest without the hash, and it is what tells anyone
    # downloading by hand that they got what this build produced.
    Set-Content "$zip.sha256" "$zipHash  $(Split-Path $zip -Leaf)"
    Set-Content "$msi.sha256" "$msiHash  $(Split-Path $msi -Leaf)"

    Write-Output "  zip sha256:  $zipHash"
    Write-Output "  msi sha256:  $msiHash"
    Write-Output "  ProductCode: $productCode"

    # The manifests in packaging\ are templates with placeholders for the values only
    # a build can know. Filled copies go to artifacts\, and the post-release workflow
    # commits them back. Each placeholder is replaced exactly where it belongs: the
    # zip's hash beside the zip's URL, the MSI's beside the MSI's.
    $wingetOut = Join-Path $artifacts 'winget'
    if (Test-Path $wingetOut) { Remove-Item $wingetOut -Recurse -Force }
    New-Item -ItemType Directory -Path $wingetOut | Out-Null

    foreach ($source in Get-ChildItem (Join-Path $root 'packaging\winget') -Filter '*.yaml') {
        $lines = Get-Content $source.FullName
        $pendingHash = $null

        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match '^\s*InstallerUrl:\s*\S+\.msi\s*$') { $pendingHash = $msiHash }
            elseif ($lines[$i] -match '^\s*InstallerUrl:\s*\S+\.zip\s*$') { $pendingHash = $zipHash }
            elseif ($lines[$i] -match '^(\s*InstallerSha256:\s*)\S+\s*$') {
                if (-not $pendingHash) { throw "$($source.Name):$($i + 1): InstallerSha256 before any InstallerUrl." }
                $lines[$i] = $Matches[1] + $pendingHash
                $pendingHash = $null
            }
            elseif ($lines[$i] -match '^(\s*ProductCode:\s*).*$') { $lines[$i] = $Matches[1] + "'$productCode'" }
            elseif ($lines[$i] -match '^(\s*ReleaseDate:\s*).*$') { $lines[$i] = $Matches[1] + $releaseDate }
        }

        $target = Join-Path $wingetOut $source.Name
        [System.IO.File]::WriteAllText($target, (($lines -join "`n") + "`n"), [System.Text.UTF8Encoding]::new($false))
    }

    $installerManifest = Get-Content (Join-Path $wingetOut 'MoaidHathot.Shubbak.installer.yaml') -Raw
    if ($installerManifest -match '0{64}|F{64}') { throw 'A placeholder hash survived into the filled installer manifest.' }
    if ($installerManifest -match '\{0{8}-') { throw 'A placeholder ProductCode survived into the filled installer manifest.' }

    if (Get-Command winget -ErrorAction SilentlyContinue) {
        Invoke-Checked 'winget validate' { winget validate --manifest $wingetOut }
    }
    else {
        Write-Warning 'winget is not on PATH; the filled manifests were not validated here.'
    }

    # Scoop: the bucket manifest, with the hash Scoop expects in lower case.
    $bucketOut = Join-Path $artifacts 'bucket'
    if (Test-Path $bucketOut) { Remove-Item $bucketOut -Recurse -Force }
    New-Item -ItemType Directory -Path $bucketOut | Out-Null
    $scoop = Get-Content (Join-Path $root 'bucket\shubbak.json') -Raw
    $scoop = [regex]::Replace($scoop, '"hash":\s*"[0-9a-fA-F]{64}"', ('"hash": "' + $zipHash.ToLowerInvariant() + '"'))
    [System.IO.File]::WriteAllText((Join-Path $bucketOut 'shubbak.json'), $scoop, [System.Text.UTF8Encoding]::new($false))

    Write-Output "  filled: $wingetOut, $bucketOut"

    if ($inActions) {
        "zip=$zip" >> $env:GITHUB_OUTPUT
        "msi=$msi" >> $env:GITHUB_OUTPUT
        "zip_sha256=$zipHash" >> $env:GITHUB_OUTPUT
        "msi_sha256=$msiHash" >> $env:GITHUB_OUTPUT
        "product_code=$productCode" >> $env:GITHUB_OUTPUT
    }
    Group-End
}
