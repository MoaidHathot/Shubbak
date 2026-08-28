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
git tag -a v0.9.0 -m "Shubbak 0.9.0 - the first public release"
git push origin v0.9.0
```

The tag must be `v` followed by exactly what is in `Directory.Build.props`. The
workflow compares them before it builds anything, because a release named one thing
and containing another only ever surfaces in a bug report months later quoting a
version that was never published.

Annotated (`-a`) rather than lightweight, so the tag records who made the release and
when. The workflow accepts either; `git describe` and `git for-each-ref` do not treat
them alike, and a release is the one kind of tag worth being able to attribute.

If the workflow fails, delete the tag from both places before retrying - the version
check runs before the build, but the test and publish steps run after, so a tag can
outlive the run that rejected it:

```
git push --delete origin v0.9.0
git tag -d v0.9.0
```

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

The SHA256 is printed by the "Stage the package" step and written to
`shubbak-<version>-win-x64.zip.sha256` beside the zip. Until the draft is published
both are reachable only when authenticated — a draft release is not served to anonymous
callers, and neither is the build artefact — so the hash for step 6 either comes out of
the workflow log or waits until the release is public.

## 6. Update the package manifests

Both need the new version and the new SHA256, which the workflow prints and writes
alongside the zip.

**Scoop** — `bucket/shubbak.json`, in this repository. That path is not a preference:
Scoop looks for a `bucket` directory in the repository you add and falls back to the
root, so a manifest anywhere else is never found. Users get it from the bucket, so a
commit to `main` is the release, and `scoop update` picks it up.

**winget** — the verb depends on whether the package is already in `winget-pkgs`.

Check first, because the wrong one fails in a way that reads like a broken tool rather
than a wrong command:

```
winget show MoaidHathot.Shubbak
```

**If that finds nothing, this is a first submission.** `wingetcreate update` operates on
a manifest already in the upstream repository and cannot create one, so it answers a
first release with a "package not found" that says nothing about what to do instead.
Submit the manifests in `packaging/winget/`, which are maintained here and already
validate:

```
winget validate --manifest packaging\winget
wingetcreate submit --token <pat> packaging\winget
```

Edit `InstallerSha256` and `ReleaseDate` by hand first; the URL and `PackageVersion`
are already right if the version was set in step 1.

**Once it is upstream, later releases use `update`**, which fetches the asset, computes
the hash and opens the pull request in one step:

```
wingetcreate update MoaidHathot.Shubbak `
    --version 0.9.0 `
    --urls https://github.com/MoaidHathot/Shubbak/releases/download/v0.9.0/shubbak-0.9.0-win-x64.zip `
    --submit
```

`wingetcreate` recomputes the hash itself, so it will disagree loudly if the release
asset was replaced after the workflow measured it. Do not replace release assets.

The PAT needs `public_repo`. `wingetcreate` forks `microsoft/winget-pkgs` to your
account and opens the pull request from there, so the fork is a normal consequence
rather than something to undo.

### The icon winget will not show

The `defaultLocale` schema has an `Icons` field, and `docs/assets/shubbak-wm.png` is
exactly what it takes — a 256-pixel PNG at a public URL. It is deliberately not in the
manifest, because `winget validate` answers it with:

```
Manifest Warning: Field usage requires verified publishers. [Icons]
```

So it would display nothing, and would put a restricted field in front of a reviewer
who has no reason to expect one. Add it once the publisher is verified, and pin
`IconUrl` and `IconSha256` to the release tag rather than to `main` — a hash against a
moving branch goes stale the next time `tools/make-icons.ps1` runs.

Nothing is lost meanwhile: winget falls back to the icon compiled into the executable,
which the same script draws.

Scoop has no icon field at all. It could gain Start Menu shortcuts with icons through
`shortcuts`, but that would put four entries in the Start Menu for a window manager and
three programs it starts itself, which is a worse trade than a missing picture.

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
