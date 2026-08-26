# Releasing

What to do, in order, and what each step is guarding against.

Most of this is checked by CI rather than remembered. The list is short because
anything that could drift has been made into a build error or a workflow step
instead — the version, the manifest, the subsystem, the AOT publish.

## 1. Decide the version

One number, in `Directory.Build.props`:

```xml
<Version>0.9.0</Version>
```

Everything reads it from there: all four binaries report it through
`ShubbakVersion`, `shubbak diagnose` prints it, and the release workflow refuses to
run if the tag disagrees.

Both manifests in `src/Shubbak.Wm/` carry the four-part form of the same number in
their `assemblyIdentity`. The build fails if they do not match, and the error says
what to change them to. Windows ignores that field for an application that is not
side-by-side assembled, so it breaks nothing when wrong — which is exactly why it sat
at the SDK's placeholder for so long, and why it is now checked.

## 2. Update the changelog

Move `[Unreleased]` into a version heading with today's date, and add the comparison
links at the bottom.

Write it for somebody deciding whether to upgrade. `### Changed` matters more than
`### Added`, and anything with a visible consequence belongs there with the
consequence spelled out.

## 3. Check the test count

The README states it in two places and CI compares both against the tree. If you
added tests, run:

```pwsh
(Get-ChildItem tests -Recurse -Filter *.cs -File |
    Where-Object { $_.FullName -notmatch '\\obj\\|\\bin\\' } |
    Select-String -Pattern '^\s*\[(Fact|Theory)' -AllMatches).Count
```

and put that number in the README. This has been wrong on every occasion somebody
looked, which is why it is checked rather than trusted.

## 4. Tag it

```
git tag v0.9.0
git push origin v0.9.0
```

The tag must be `v` followed by exactly what is in `Directory.Build.props`. The
workflow compares them before it builds anything, because a release named one thing
and containing another only ever surfaces in a bug report months later quoting a
version that was never published.

## 5. Wait for the workflow, then publish

`.github/workflows/release.yml` runs on the tag and:

1. checks the tag against `<Version>`,
2. builds and tests the solution,
3. publishes all four binaries as NativeAOT, each to its own directory,
4. runs each one and checks it reports the expected version,
5. packs a flat zip with a SHA256, and a separate symbols archive,
6. opens a **draft** release.

It is a draft on purpose. Read the generated notes, paste the SHA256 into anything
that needs it, then publish by hand.

## 6. Update the package manifests

Both need the new version and the new SHA256, which the workflow prints and writes
alongside the zip.

**Scoop** — `bucket/shubbak.json`, in this repository. That path is not a preference:
Scoop looks for a `bucket` directory in the repository you add and falls back to the
root, so a manifest anywhere else is never found. Users get it from the bucket, so a
commit to `main` is the release, and `scoop update` picks it up.

**winget** — regenerate and submit from `packaging/winget/`:

```
wingetcreate update MoaidHathot.Shubbak `
    --version 0.9.0 `
    --urls https://github.com/MoaidHathot/Shubbak/releases/download/v0.9.0/shubbak-0.9.0-win-x64.zip `
    --submit
```

`wingetcreate` recomputes the hash itself, so it will disagree loudly if the release
asset was replaced after the workflow measured it. Do not replace release assets.

## Why there is no installer yet

`uiAccess` — moving windows that belong to elevated processes without running the
whole window manager as administrator — requires three things at once, and Windows
does not accept two out of three:

1. `app.uiaccess.manifest`, selected with `-p:ShubbakUiAccess=true`,
2. an Authenticode signature from a certificate the machine trusts,
3. installation under `%ProgramFiles%` or `%SystemRoot%\System32`.

A binary that asks for `uiAccess` and fails either of the last two does not fall back
to running without the privilege — it fails to launch, with a message
("A referral was returned from the server") that explains nothing.

So the portable zip is built *without* it, and users who need elevated windows managed
run the daemon elevated instead. When there is a certificate, the installer becomes a
second artefact from the same build, and a second winget manifest with
`Scope: machine`.

**When adding it, build the two variants into different output directories.** The
manifest is baked into the apphost, and MSBuild does not treat `ShubbakUiAccess` as an
input to it — so a shared directory is how a `uiAccess` apphost survives into a build
that did not ask for one, producing a binary that cannot start and a log that does not
say why.

Note also that `tests/Shubbak.Native.Tests/PrivilegeTests.cs` asserts `HasUiAccess` is
false, on the grounds that a build running from a source tree cannot have been granted
it. That stays true for the portable build, and stops being true the moment tests are
run from a signed install.

## Running the tests locally

`Shubbak.Native.Tests` creates real windows and refuses to run while a window manager
is managing them:

```
shubbak-wm is running. These tests create real windows, which it will manage,
move and conceal - any result would be measuring the window manager rather than
the code under test. Stop it and run them again.
```

That is the guard working. Stop your own Shubbak, or trust CI, which runs on a clean
agent. The IPC tests collide with a running daemon's pipe for the same reason.
