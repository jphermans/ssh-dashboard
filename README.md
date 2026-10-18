# SSH Dashboard

A desktop dashboard for your OpenSSH folder: every host in `~/.ssh/known_hosts`,
every block in `~/.ssh/config` and every key pair, in one place — with safe,
backed-up removal of the entries you no longer need.

Built with the Python standard library only (Tkinter), so there is nothing to
install beyond Python itself.

```
┌────────────────┬──────────────────────────────────────────────────────────┐
│  SSH Dashboard │  Known hosts                          [search]  ↻  ⋯    │
│  HOST & KEY…   │  27 known_hosts · 9 blocks · 3 keys                        │
│                │  ┌───────────────────────────────────────┬─────────────┐│
│  Known hosts 30│  │ ✓  Host      Address     User   Keys  │  DETAILS    ││
│  Configuration │  │    nas_lab    192.0.2…    root   ed2…  │  nas_lab     ││
│  Keys        3 │  │    PI5-AI    192.0.2…    jpher… ed2…  │  …          ││
│  Activity      │  │                                       │             ││
│                │  │ 2 hosts selected      Clear  Remove 2 │             ││
│  ~/.ssh        │  └───────────────────────────────────────┴─────────────┘│
└────────────────┴──────────────────────────────────────────────────────────┘
```

## Features

- **Known hosts** — groups the multiple key types recorded for a host into one
  row, resolves the matching `ssh_config` block (alias, user, port, identity
  file) and verifies SHA-256 fingerprints.
- **Selection & bulk removal** — tick rows with the checkbox column (or `⌘A`,
  `Space`), review the confirmation dialog and remove them in one action.
- **Configuration view** — inspect and **edit** every `Host` block: patterns,
  `HostName`, `User`, `Port`, `IdentityFile` and `ProxyJump`. Unknown options and
  comments are preserved; only literal blocks in the main config are writable.
- **Full menu bar** — a native macOS application menu (About, Settings, Hide,
  Quit) plus File, Edit, View, Tools, Window and Help menus, with standard
  accelerators that follow the platform's modifier key.
- **Export** — write the visible rows of any view to CSV.
- **Keys view** — type, bits, SHA-256 fingerprint, comment, passphrase status and
  file mode, with copy-to-clipboard and Reveal in Finder. Keys are read-only.
- **Details rail** — per-host key fingerprints, `known_hosts` line numbers,
  `ssh` command, and a live TCP reachability check.
- **Activity journal** — every removal and restore is logged locally.
- **Light / dark themes** — a slate dark theme and a grey-blue light theme.
- **Search, sort, keyboard shortcuts** and a first-class empty state.
- **Cross-platform** — macOS, Windows and Linux. On Windows the app is
  per-monitor DPI aware, has a taskbar icon, and uses `%USERPROFILE%\.ssh` with
  settings in `%APPDATA%\SSHDashboard`.

## Safety model

Deleting the wrong host is annoying; corrupting `known_hosts` is worse. The app:

1. writes a timestamped backup to `~/.ssh/.sshdashboard/backups/` **before**
   any write (`<stamp>__known_hosts`, `<stamp>__config`);
2. rewrites files atomically (temp file + `fsync` + `os.replace`) and preserves
   the original permissions (`0600`);
3. removes only the exact tokens you selected — if a line lists several hosts,
   only your selection is dropped and the rest of the line is preserved;
4. understands hashed `known_hosts` entries (`|1|salt|hash`) via HMAC-SHA1, so
   hashed hosts can be removed without disabling hashing;
5. re-parses `ssh_config` at write time and refuses stale edits;
6. never touches `Include`d files, global `Host *` blocks or wildcard blocks;
7. offers **Undo** in the toast after every removal.

## Requirements

- Python 3.9 or newer with **Tk 8.6+** (macOS Homebrew `python-tk`, python.org
  builds, Windows python.org builds, or a Linux `python3-tk` package).
- macOS, Windows or Linux. The UI is custom-drawn, so it looks identical
  everywhere; macOS also gets a native application menu.

> macOS's built-in Python 3.9 ships Tk 8.5, which cannot map this window. The
> app detects that, relaunches itself with a supported interpreter when one is
> installed, and otherwise explains how to install one. `./run.sh` only picks
> interpreters with Tk 8.6+.

## Run

```bash
./run.sh                     # macOS / Linux, picks the best Python with Tk
# or double-click "SSH Dashboard.command" (macOS)
```

Windows:

```bat
run.bat
:: or
py -3 ssh_dashboard.py
```

Any platform, explicitly:

```bash
python3 ssh_dashboard.py
python3 -m sshdash           # from the project directory
python3 ssh_dashboard.py --check   # print Tk/Python/paths, no window
```

## Keyboard

`⌘` on macOS, `Ctrl` on Windows and Linux.

| Shortcut | Action |
| --- | --- |
| `⌘R` | Rescan `~/.ssh` |
| `⌘F` | Focus search |
| `⌘E` | Export visible rows to CSV |
| `⌘A` / `⇧⌘A` | Select all visible / deselect all |
| `⌘Z` | Undo the last removal or edit |
| `⌘1` … `⌘4` | Switch view (hosts, config, keys, activity) |
| `⌘,` | Settings |
| `⌘/` | Keyboard shortcuts |
| `Space` | Tick the focused row |
| `Delete` / `Backspace` | Remove the selected rows |
| `Esc` | Clear search, then selection |
| Double-click a config row | Edit that Host block |
| Click a column header | Sort, click again to reverse |

## Project layout

```
ssh_dashboard.py          entry point
sshdash/
  model.py                immutable value objects (HostView, ConfigBlock, …)
  parsers.py              read-only known_hosts / ssh_config / key parsers
  store.py                the only writer: backups, atomic saves, deletion
  config.py               settings, activity journal, platform paths
  runtime.py              Tk capability check, platform setup (DPI, relaunch)
  ui/
    theme.py              palettes, typography, ttk styling
    widgets.py            rounded buttons, fields, toasts, editors, settings
    app.py                window, menu bar, layout and interactions
    branding.py           icon loading (works inside a PyInstaller bundle)
    assets/               icon PNGs, SSHDashboard.icns, SSHDashboard.ico
tests/
  test_core.py            parsers + store (including hashed entries)
  test_ui_smoke.py        builds the real window and drives every view
tools/
  make_icon.py            regenerate every icon size, .icns and .ico
  ui_probe.py             delete/undo flow against a throwaway HOME
  config_edit_probe.py    config editor flow against a throwaway HOME
  ui_stress.py            menus, dialogs and view churn
packaging/
  SSHDashboard.spec       PyInstaller one-folder build (macOS .app / .exe)
  build-macos.sh, build-windows.bat
```

## Tests

```bash
python3 -m unittest discover -s tests -v
```

Core tests run everywhere. UI smoke tests are skipped automatically when no
display is available; they never touch the real `~/.ssh` (they only read it).

## Packaging

The icon ships in three formats: PNGs for Tk, `.icns` for macOS bundles and
`.ico` for Windows executables. Regenerate them after design changes with:

```bash
python3 tools/make_icon.py       # requires Pillow
```

Build a standalone app (assets and icon are bundled by the spec):

```bash
./packaging/build-macos.sh       # dist/SSH Dashboard.app
packaging\build-windows.bat      # dist\SSH Dashboard\SSH Dashboard.exe
```

A prebuilt Apple-silicon build ships in `dist/`:

```
SSH Dashboard.app                          # double-click to run
SSH-Dashboard-1.0.0-macOS-arm64.zip        # portable copy of the .app
SSH-Dashboard-1.0.0-macOS-arm64.dmg        # drag-to-Applications installer
```

It is ad-hoc signed (`codesign --verify` passes) but **not notarized**, so a copy
downloaded on another Mac needs right-click -> Open the first time. The build is
arm64 only; build from an x86_64 Python for Intel Macs. `--check` works from the
packaged binary and is the quickest way to confirm a build:

```bash
"dist/SSH Dashboard.app/Contents/MacOS/SSH Dashboard" --check
```

The spec sets `io.jphsystems.sshdashboard` as the bundle identifier, version
1.0.0, and keeps `NSHighResolutionCapable` on macOS.

## Troubleshooting

- **Nothing happens / `ImportError: No module named '_tkinter'`** — your Python
  was built without Tk. Use `./run.sh` (`run.bat` on Windows), or
  `brew install python-tk@3.14`.
- **"SSH Dashboard needs Tk 8.6 or newer"** — the interpreter is too old; the
  message names the requirement and the install command.
- **Windows: no menu bar** — the menu is attached to the window; if you launch
  the `.py` with a console interpreter, the console window owns the focus. Use
  `run.bat` or the packaged executable.
- **An unexpected error dialog** — the traceback is appended to
  `~/Library/Application Support/SSHDashboard/error.log`.
- **Restore a removal** — click **Undo** in the toast, or copy the backup from
  `~/.ssh/.sshdashboard/backups/` over the original file.
- **Hashed known_hosts** — hashing is fully supported; hashed rows appear as
  `hashed · <digest>…` because a hash cannot be reversed, only verified.

## Configuration

Preferences live in `~/Library/Application Support/SSHDashboard/settings.json`
(macOS), `~/.config/sshdashboard/settings.json` (Linux) or
`%APPDATA%\SSHDashboard\settings.json` (Windows). They cover theme,
confirmation behaviour and default config-block removal.

On Windows the SSH folder is `%USERPROFILE%\.ssh`. System-wide OpenSSH files
under `C:\ProgramData\ssh` are intentionally not touched.
