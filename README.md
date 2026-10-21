# SSH Dashboard

<img src="dotnet/assets/appicon.png" alt="" width="88" align="left" hspace="16" vspace="4">

**A careful desktop dashboard for `~/.ssh`.** It lists every host in `known_hosts`
and `ssh_config`, every key pair, lets you remove entries safely, edit Host
blocks, and open an SSH session — all with timestamped backups and a local
activity journal.

Built with **.NET 10** and **Avalonia** for macOS, Windows and Linux.

**[Website and downloads →](https://jphermans.github.io/ssh-dashboard/)**

<br clear="left">

![.NET](https://img.shields.io/badge/.NET-10-512BD4)
![Avalonia](https://img.shields.io/badge/Avalonia-11.3-8B44AC)
![Platforms](https://img.shields.io/badge/platforms-macOS%20%7C%20Windows%20%7C%20Linux-11171F)
![Tests](https://img.shields.io/badge/tests-13%20passing-3FCF8E)
![Version](https://img.shields.io/badge/version-2.1.6-4C8DFF)

![Hosts view](docs/screenshots/hosts-dark.png)

![Keys view](docs/screenshots/keys-light.png)

## Features

- **Hosts** — merges `known_hosts` and `ssh_config` (including `Include`d files)
  into one list, showing address, user, port, key types and where each entry
  comes from. Hashed `known_hosts` entries are listed and removable too. Select
  rows with the checkboxes and remove them in one action.
- **Open SSH session** — button, Tools menu, footer action or double-click.
  Uses the `ssh_config` alias when there is one, so `ProxyJump`, `IdentityFile`
  and ports behave exactly as on the command line; otherwise
  `ssh user@address -p port`. The session opens in an in-app terminal window.
- **Configuration** — every `Host` block with HostName, User, Port and
  IdentityFile. Add a new block from the footer, Tools menu or `Cmd/Ctrl+N`;
  double-click to edit patterns and options. Comments and unknown options are
  preserved. `Match` and `Host *` blocks are read-only.
- **PuTTY keys** — import `.ppk` files with a file dialog (validated, copied into `~/.ssh`, named after the selected host) or drop them there directly. `.ppk` v2/v3 files are recognised with their fingerprint. Unencrypted RSA/Ed25519 PPKs are converted to a temporary OpenSSH key for sessions, then deleted; passphrase-protected PPKs prompt once and convert through `puttygen` when installed.
- **Keys** — private and public key files with type, SHA256 fingerprint, size
  and file permissions.
- **Activity** — a local journal of each removal, edit, import, undo and restore.
- **Backups** — a browser for every timestamped backup, with restore. Retention
  is 30 days or 200 files, whichever is smaller.
- **Undo** — restores every file touched by the last action, not just one.
- **Themes** — System, Dark and Light. System follows the OS, including changes
  while the app is open. The choice is persisted.
- **Resizable workspace** — draggable splitters for the sidebar and details
  rail, star-sized table columns, 1040×640 minimum window.
- **Export** — the current view to CSV.
- **Connection test** — TCP port check; on macOS it uses `/usr/bin/nc`, so no
  Local Network permission is needed.
- **Safe writes** — an exclusive write lock, atomic replacement and preserved
  permissions; non-UTF-8 comments survive deletion byte-for-byte.

## Safety model

Every write follows the same path:

1. The target file is copied to
   `~/.ssh/.sshdashboard/backups/<timestamp>-<name>`.
2. Deletion re-reads and re-parses the file, removing only lines or blocks that
   still match the selection.
3. Data is written to a temporary file in the same directory, the original
   permissions are applied, then an atomic rename replaces the file.
4. The action is appended to the activity journal.

The app never writes outside `~/.ssh`. `Include`d files and `Match` / global
`Host *` blocks are read-only; only the main `config` is edited.

## Download

All builds are self-contained — no .NET runtime required on the target.

| Platform | File |
| --- | --- |
| macOS 12+ (Apple Silicon + Intel) | [`SSH-Dashboard-2.1.6-macOS-universal.dmg`](https://github.com/jphermans/ssh-dashboard/releases/download/v2.1.6/SSH-Dashboard-2.1.6-macOS-universal.dmg) |
| Windows 10/11 x64 — installer | [`SSH-Dashboard-2.1.6-Setup.exe`](https://github.com/jphermans/ssh-dashboard/releases/download/v2.1.6/SSH-Dashboard-2.1.6-Setup.exe) |
| Windows 10/11 x64 — portable | [`SSH-Dashboard-2.1.6-windows-x64.zip`](https://github.com/jphermans/ssh-dashboard/releases/download/v2.1.6/SSH-Dashboard-2.1.6-windows-x64.zip) |
| Linux x64 | [`SSH-Dashboard-2.1.6-linux-x64.tar.gz`](https://github.com/jphermans/ssh-dashboard/releases/download/v2.1.6/SSH-Dashboard-2.1.6-linux-x64.tar.gz) |

Checksums: [`SHA256SUMS-2.1.6.txt`](https://github.com/jphermans/ssh-dashboard/releases/download/v2.1.6/SHA256SUMS-2.1.6.txt).

### macOS

Open the DMG and drag **SSH Dashboard** into *Applications*. The DMG is a
universal binary for Apple Silicon and Intel, macOS 12 or newer.

The build is ad-hoc signed and **not notarized**, so a downloaded copy is
quarantined by Gatekeeper. On first launch use **System Settings → Privacy &
Security → Open Anyway**, or right-click the app → **Open**. To clear the
quarantine flag directly:

```sh
xattr -dr com.apple.quarantine "/Applications/SSH Dashboard.app"
```

A fully double-click install needs a Developer ID certificate and Apple
notarization. The exact commands are in
[`docs/APP-STORE.md`](docs/APP-STORE.md), together with the Mac App Store
assessment (the current sandbox-dependent design is not App Store-ready).

### Windows installer

A branded EXE installer is built **manually**, never on a push:

1. Open **Actions → windows-installer → Run workflow**.
2. Enter the version (for example `2.1.6`) and run it.
3. The run attaches `SSH-Dashboard-<version>-Setup.exe` to the matching GitHub
   release, and also uploads it as a workflow artifact.

It installs per-user (no administrator prompt), creates a Start menu entry and
an optional desktop shortcut, registers an uninstaller in Apps & features, and
uses the app icon throughout. The wizard artwork lives in `installer/assets/`
and is regenerated by `installer/generate-artwork.py`.

### Windows and Linux binaries

Build a self-contained binary (no .NET runtime needed on the target):

```sh
# Windows x64
dotnet publish dotnet/src/SshDashboard.App -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -o out/win

# Linux x64
dotnet publish dotnet/src/SshDashboard.App -c Release -r linux-x64 \
  --self-contained true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -o out/linux
```

CI builds all three platforms on every push and publishes a tagged release
with checksums (see `.github/workflows/build.yml`).

## Build and run

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```sh
cd dotnet
dotnet restore
dotnet run --project src/SshDashboard.App
dotnet test
```

The `--check` switch prints what the app sees without opening a window:

```sh
dotnet run --project src/SshDashboard.App -- --check
```

## Keyboard shortcuts

| Shortcut | Action |
| --- | --- |
| `F5` | Rescan `~/.ssh` |
| `Delete` | Remove the selected rows or block |
| `Cmd/Ctrl + A` | Select all hosts |
| `Cmd/Ctrl + E` | Export the current view |
| `Cmd/Ctrl + N` | Add a Host block |
| `Cmd/Ctrl + ,` | Settings |
| `Cmd/Ctrl + Z` | Undo the last change, restoring every file it touched |
| `Cmd/Ctrl + 1…4` | Hosts, Configuration, Keys, Activity |
| `Cmd/Ctrl + Shift + T` | Open an SSH session |

## Project layout

```
dotnet/
  SshDashboard.slnx
  src/
    SshDashboard.Core/    parsers, store, backups, settings, platform helpers
    SshDashboard.App/     Avalonia UI, view models, dialogs, SSH launcher
  tests/
    SshDashboard.Core.Tests/
  assets/                 .icns and PNG icon sources
docs/screenshots/         README images (fictional demo data)
```

The store is the only component that writes; the UI calls it and reloads the
snapshot afterwards.

## Testing

```sh
dotnet test dotnet/tests/SshDashboard.Core.Tests
```

Eight tests cover known_hosts parsing (shared lines, hashed hosts, ports),
merging with `ssh_config`, safe deletion with backups, config editing with
comment preservation, restore and CSV escaping. Tests use a temporary SSH
folder and never touch your real `~/.ssh`.

## License

No license is granted; all rights reserved. Open an issue if you would like a
permissive license added.

---

<p align="center"><sub>Built by <b>JPHsystems</b> · macOS, Windows and Linux · .NET + Avalonia</sub></p>
