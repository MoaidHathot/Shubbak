# Releasing

What to do, in order, and what each step is guarding against.

Most of this is checked by CI rather than remembered. The list is short because
anything that could drift has been made into a build error, a workflow step or a
script instead - the version, the manifests, the subsystem, the AOT publish, the
signature, the hashes.

## What a release is

One tag, `v<version>`, and from it one workflow run that produces:

| Artefact | What it is |
|---|---|
| `shubbak-<v>-win-x64.msi` | Per-machine installer: the five executables under `%ProgramFiles%\Shubbak`, on the machine `PATH`, in Apps & Features. Carries the **uiAccess** build of `shubbak-wm`, which is what lets it tile windows of elevated programs without being elevated. Signed. |
| `shubbak-<v>-win-x64.zip` | The portable build: five executables plus the readme, licence, example config and docs, flat. What Scoop and `winget --scope user` install. Signed executables. |
| `*.sha256` | One per package. What the manifests carry and what a person downloading by hand can check. |
| `shubbak-<v>-win-x64-symbols.zip` | The `.pdb` files, for reading a crash report's stack. |
| `winget/`, `bucket/` | The package manifests with the real hashes, `ProductCode` and date filled in. Only in the workflow's uploaded artefact, not on the release page. |

winget serves both packages under one identifier, `MoaidHathot.Shubbak`. It prefers
the MSI when both apply - its built-in precedence puts `msi`/`wix` ahead of
`portable` - and falls through to the zip for `--scope user`.

## Once: the signing setup

The release workflow signs with [Azure Artifact Signing](https://learn.microsoft.com/azure/artifact-signing/)
(formerly Trusted Signing), authenticating through GitHub's OpenID Connect so no
secret key ever exists anywhere. This is done once.

1. **Azure**: an Artifact Signing account with a completed identity validation and a
   Public Trust certificate profile. Note the account's region endpoint
   (`https://<region>.codesigning.azure.net`), the account name and the profile name.
2. **Entra**: an app registration with a federated credential for GitHub Actions.
   Issuer `https://token.actions.githubusercontent.com`, subject
   `repo:MoaidHathot/Shubbak:environment:release`, audience `api://AzureADTokenExchange`.
   The subject is the *environment*, not the tag: a federated credential matches its
   subject exactly, and one per tag would be one per release.
3. **Role**: the app registration gets **Artifact Signing Certificate Profile Signer** on
   the certificate profile (or the account).
4. **GitHub**: Settings → Environments → `release` (create it; no protection rules are
   needed, though a required reviewer is a reasonable one). On that environment, or on
   the repository:

   | Kind | Name | Value |
   |---|---|---|
   | secret | `AZURE_TENANT_ID` | the Entra tenant |
   | secret | `AZURE_CLIENT_ID` | the app registration's application (client) id |
   | secret | `AZURE_SUBSCRIPTION_ID` | the subscription holding the signing account |
   | variable | `SIGNING_ENDPOINT` | e.g. `https://eus.codesigning.azure.net` |
   | variable | `SIGNING_ACCOUNT` | the Artifact Signing account name |
   | variable | `SIGNING_CERTIFICATE_PROFILE` | the certificate profile name |

A tag build **refuses to run unsigned**: if any of the six is missing, the workflow
stops before building. A `workflow_dispatch` run signs when they are present and
builds unsigned when they are not, which is how the pipeline is exercised before
the Azure side is ready.

The certificate is short-lived (three days) and the signature is timestamped by the
service's own authority, which is what keeps it valid afterwards;
`tools/check-signatures.ps1` fails the build if a file is signed but not timestamped.

Also once, for the post-release automation: a **classic** personal access token with
`public_repo` scope as the `WINGET_TOKEN` repository secret, and a fork of
`microsoft/winget-pkgs` under the same account. See "After publishing" below.

## 1. Write the changelog as you go

Under `## [Unreleased]`. Write it for somebody deciding whether to upgrade:
`### Changed` matters more than `### Added`, and anything with a visible consequence
belongs there with the consequence spelled out.

## 2. Prepare the version

```pwsh
.\tools\prepare-release.ps1 -Version 0.10.0
```

One command sets the version everywhere it is written - `Directory.Build.props`, the
two application manifests, the winget and Scoop manifests and their URLs and pinned
documentation links - and turns `[Unreleased]` into a dated
`## [0.10.0]` entry with the compare links at the bottom. Then it runs

```pwsh
.\tools\check-release-consistency.ps1 -ForRelease
```

which is the same check the release workflow runs first, and fails if anything
disagrees. Running the prepare script again for the same version is safe: it re-dates
the entry and folds anything that has landed under `[Unreleased]` since into it.

Review the diff, run the tests, commit.

## 3. Build it locally

```pwsh
.\tools\build-release.ps1
```

Exactly what the workflow does minus the signing: publishes the five executables and
the uiAccess window manager, checks each one (version, PE subsystem, embedded
manifest), stages, packs the zip, builds the MSI, hashes, and fills the manifests
into `artifacts\winget` and `artifacts\bucket`. Run it before tagging, because a tag
is public and a failed tag build has to be cleaned up (below).

The unsigned MSI it produces contains a uiAccess `shubbak-wm.exe` that **will not
start** - Windows refuses an unsigned uiAccess binary with "A referral was returned
from the server". To test the installer's mechanics locally, build one around the
ordinary window manager:

```pwsh
.\tools\build-release.ps1 -Stage -Pack -Manifests -PortableWm
```

Never ship that one.

## 4. Test it

On a clean machine or a Windows Sandbox, with the artefacts from a `workflow_dispatch`
run (signed) or the local build (unsigned, `-PortableWm`):

- `msiexec /i shubbak-<v>-win-x64.msi` - then in a **new** terminal: `where shubbak`
  finds `C:\Program Files\Shubbak\shubbak.exe`; Apps & Features lists Shubbak with its
  icon; the Start Menu has one entry.
- `shubbak config init` → `shubbak-wm --foreground` → windows tile, the bar appears on
  every monitor, the tray icon is there, `alt+space` opens the palette,
  `alt+shift+space` cycles the layout. `shubbak status` answers.
- With the signed MSI only: Task Manager tiles. `shubbak inspect` on it says so rather
  than reporting an integrity-level refusal.
- `shubbak autostart enable`, log off and on: it starts, and starts the other three.
- `shubbak stop` stops all four and returns 0 within ten seconds.
- Upgrade: start everything, then `msiexec /i <newer>.msi /qn`. The four processes go
  away, the files are replaced, and `shubbak-wm` comes back on its own (Restart
  Manager restarts what asked to be restarted; the window manager asks, and its startup
  commands bring back the other three). The return code is 0, not 3010.
- `winget install --manifest artifacts\winget` (with `winget settings --enable
  LocalManifestFiles`) installs the MSI; `--scope user` installs the zip; `winget
  uninstall` removes either cleanly. `HKCU\...\Run\Shubbak` is the only thing left
  behind, and `shubbak autostart disable` before uninstalling avoids that.
- Explorer double-click on the zip's `shubbak.exe`: with the signed build, at worst
  SmartScreen's "unrecognised app" (a new certificate has no reputation yet); never
  "unknown publisher".

## 5. Tag

```pwsh
.\tools\prepare-release.ps1 -Version 0.10.0 -Tag -Push
```

`-Tag` refuses to run with uncommitted changes, because the tag must name the commit
that contains the release edits, and creates the annotated tag; `-Push` pushes it,
which starts the workflow. Annotated rather than lightweight so the tag records who
made the release and when.

The workflow then, in order: checks the tag against `<Version>` and runs the
consistency check; refuses if signing is not configured; builds and tests; publishes;
signs the executables and checks the signatures; stages and packs; builds the MSI;
signs it; hashes everything and fills the manifests; uploads the artefact; opens a
**draft** release with the packages and their hashes attached.

If it fails, delete the tag from both places before retrying - the version check runs
before the build, but the test and publish steps run after, so a tag can outlive the
run that rejected it:

```
git push --delete origin v0.10.0
git tag -d v0.10.0
```

## 6. Publish

Read the draft's generated notes, check the attached files against the workflow log's
hashes, and publish. Until it is published nothing downstream can see the assets: a
draft release is not served to anonymous callers, so neither winget nor Scoop can
fetch from it.

## 7. After publishing

Publishing the release triggers `.github/workflows/winget.yml`, which:

- commits the filled manifests from the workflow artefact back to `packaging/winget`
  and `bucket/shubbak.json` on `main` - for Scoop, that commit **is** the release,
  since Scoop reads the bucket straight from this repository;
- if the package already exists in `microsoft/winget-pkgs`, opens the version update
  pull request there with `winget-releaser` (which uses Komac to match the MSI and the
  zip to the right installer entries).

**The first submission is manual**, because the update tooling can only update a
package that exists. Download the `winget/` directory from the workflow artefact - or
take the committed `packaging/winget` after the workflow above has run - and:

```
winget validate --manifest packaging\winget
wingetcreate submit --token <pat> packaging\winget
```

`wingetcreate` forks `microsoft/winget-pkgs` to your account and opens the pull
request from there. Then watch the pull request: the bot validates the manifests,
downloads both installers, checks the hashes, installs them in a VM and reports.

### The icon winget will not show

The `defaultLocale` schema has an `Icons` field, and `docs/assets/shubbak-wm.png` is
exactly what it takes. It is deliberately not in the manifest, because `winget
validate` answers it with `Field usage requires verified publishers`, so it would
display nothing and put a restricted field in front of a reviewer. winget falls back
to the icon compiled into the executable, which the same script draws. Add it once the
publisher is verified, pinned to the tag rather than to `main`.

## Why there is an installer

`uiAccess` - moving windows that belong to elevated processes without running the
whole window manager as administrator - requires three things at once, and Windows
does not accept two out of three:

1. `app.uiaccess.manifest`, selected with `-p:ShubbakUiAccess=true`,
2. an Authenticode signature from a certificate the machine trusts,
3. installation under `%ProgramFiles%` or `%SystemRoot%\System32`.

A binary that asks for `uiAccess` and fails either of the last two does not fall back
to running without the privilege - it fails to launch. So the portable zip is built
*without* it and the MSI is built *with* it, from separate output directories
(`Shubbak.Wm.csproj` keeps the two variants apart, because the manifest is baked into
the apphost and a shared directory let one leak into the other).

`tests/Shubbak.Native.Tests/PrivilegeTests.cs` asserts `HasUiAccess` is false, on the
grounds that a build running from a source tree cannot have been granted it. That
stays true for every build the tests run against.

### What the installer does, and does not do

`packaging/msi/Package.wxs`. It installs the five executables, the licence, the
example config and the two docs under `%ProgramFiles%\Shubbak`; appends that directory
to the machine `PATH` (and removes it on uninstall); writes
`HKLM\SOFTWARE\Shubbak\InstallFolder` and `Version`; registers in Apps & Features with
the icon, the project URL and the getting-started link; and puts one shortcut in the
Start Menu, for the window manager. `MSIRMSHUTDOWN=1` tells Windows Installer to force
any process that ignores Restart Manager, so a silent upgrade never ends in "restart
required". Major upgrades are by a fixed `UpgradeCode`
(`{FCFFCA55-3741-45D5-9410-3429B9DF9BFD}`, also in the winget manifest; never change
it) and a per-build `ProductCode`.

It does **not** start anything, register anything to start at logon, or touch the
user's files. `shubbak autostart enable` is the user's decision, and an installer
running elevated is the wrong process to write a per-user Run key from. The config
under `~\.config\shubbak` and the session under `%LOCALAPPDATA%\Shubbak` survive an
uninstall for the same reason.

### How an upgrade while running works

A silent install - what winget runs - uses Restart Manager to close whatever holds
the files being replaced. The window manager, the bar and the palette each have a
top-level window that answers `WM_QUERYENDSESSION` and `WM_ENDSESSION`: the window
manager saves the session, un-conceals every window and leaves; the bar and the
palette just leave. The watcher has no window and is force-closed, which is harmless
- its leases die with its pipe connection. The window manager alone registers with
`RegisterApplicationRestart`, so Restart Manager starts it again once the files are
in place, and its startup commands start the other three. The same messages arrive at
logoff, which is why a logoff now saves the session cleanly too.

## ARM64

Not shipped yet, and closer than it looks. What is done:

- The code has no architecture-specific paths: no intrinsics, no explicit layouts,
  and the hand-rolled COM calls use `delegate* unmanaged`, whose calling-convention
  modifier ARM64 ignores.
- `Shubbak.Native`, the one project that must compile for a concrete architecture
  (CsWin32 shapes some Win32 structures per platform), follows the runtime identifier
  of the executable being built. `dotnet publish -r win-arm64 -p:PublishAot=true`
  compiles every project for ARM64 and stops only at the platform linker on a machine
  without the ARM64 C++ build tools - which the `windows-latest` runner image has.
- GitHub also offers a `windows-11-arm` runner image, so the ARM64 binaries could be
  started (`--version`, the subsystem check) and the tests run natively.

What remains, roughly in order:

1. `tools/build-release.ps1`: loop over `win-x64` and `win-arm64`; artefacts become
   `shubbak-<v>-win-<arch>.{zip,msi}`; the manifest-filling step pairs each
   `InstallerUrl` with its hash by architecture and extension, not extension alone.
2. `packaging/msi`: build once per architecture (`InstallerPlatform` `arm64`,
   `OutputName` per arch). Same `UpgradeCode`; a different `ProductCode` per build as
   now.
3. `packaging/winget`: two more `Installers` entries with `Architecture: arm64` (MSI
   and zip). `bucket/shubbak.json`: an `arm64` block beside `64bit`, in `architecture`
   and `autoupdate`.
4. `release.yml`: sign both sets (the folder filters already recurse), upload both.
   `winget.yml`: the download patterns and `installers-regex` widen to both.
5. A `windows-11-arm` job in `build.yml` that runs `dotnet test` and starts the ARM64
   binaries, so an ARM64 build is exercised before it is released.
6. The performance numbers in ADR 0001 are x64; take them again on ARM64.

Until then the x64 build is what an ARM64 machine gets, and it runs there through
Windows' x64 emulation: the keyboard hook and the window event hooks are
out-of-process, so emulation costs CPU, not correctness.

## Running the tests locally

`Shubbak.Native.Tests` creates real windows and refuses to run while a window manager
is managing them:

```
shubbak-wm is running. These tests create real windows, which it will manage,
move and conceal - any result would be measuring the window manager rather than
the code under test. Stop it and run them again.
```

That is the guard working. `shubbak stop`, run them, `shubbak-wm` - or trust CI, which
runs on a clean agent. The IPC tests collide with a running daemon's pipe for the same
reason.
