# Security

## What Shubbak is, from a security standpoint

Five native executables that run as the logged-on user, with no service, no driver,
no network access and no elevation. The window manager installs a low-level keyboard
hook and a set of window-event hooks, which is what a window manager is; the hook sees
every keystroke and forwards every one it has not been asked to claim, and the
`binding.fired` event reports only the chords Shubbak claimed - nothing typed into an
application can reach a subscriber through it.

The one boundary worth knowing about is the named pipe, `shubbak-v2-<SID>`. It is
scoped to your account and carries every command a keybinding can run. Two of those
are gated, because the pipe is scoped to your *account* and not to your *integrity
level*: any process running as you can reach it, including one that is not elevated
reaching a window manager that is.

- `shell-exec` is refused over the pipe unless `general { allow-shell-exec-over-ipc
  #true }`. Keybindings, rules and startup commands can always use it.
- `add-rule` and `remove-rule`, which edit the config file, are on by default and can
  be turned off with `general { allow-config-edits-over-ipc #false }` - on by default
  because every process that can reach the pipe can already reach the file.

The MSI build of the window manager carries `uiAccess`, which is what lets it move the
windows of elevated programs without being elevated itself. Windows grants that only to
a signed binary installed under `Program Files`, which is why the portable build does
not have it and why the MSI is the recommended install. Every release is Authenticode
signed through Azure Artifact Signing, with the release workflow authenticating by
OpenID Connect so no signing key exists to leak; a tag build refuses to run unsigned.

## Reporting a vulnerability

Please report privately, through
[a security advisory](https://github.com/MoaidHathot/Shubbak/security/advisories/new)
rather than an issue, and say which of the five programs, what an attacker in what
position could do, and how you found it. A `shubbak diagnose -o report.md` is welcome
if it helps show the problem, but read it first: it carries your config file and the
titles of your windows.

What to expect: an acknowledgement within a week, and a fix or a decision within
thirty days for anything that lets a less-privileged process do more through Shubbak
than it could do alone. This is one person's project; a fix that needs a release is a
release, which the [releasing notes](RELEASING.md) describe, and a fix that can be
made by a config setting will be documented before it is shipped.

## What is not a vulnerability

- Anything a process running as you could already do without Shubbak. The pipe is a
  convenience for that process, not a new capability.
- A `shell-exec` in your own config running what you told it to.
- The keyboard hook seeing keystrokes. It has to; what matters is that it forwards
  them and records only its own chords, and `docs/scripting.md` says so.
- A window manager that can be made to misplace, hide or resize windows by another
  process on the same desktop. It manages the desktop; the desktop is shared.

## Supported versions

The latest release. Security fixes are not backported: there is one line of releases,
and `winget upgrade` moves you along it.
