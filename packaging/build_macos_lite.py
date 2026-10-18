"""Build the lightweight macOS app bundle.

No Python runtime is embedded. The launcher detects an installed Python 3.9+/Tk
8.6, shows which version it will use, and explains how to install Python when
none is available.

    python3 packaging/build_macos_lite.py
"""

from __future__ import annotations

import os
import plistlib
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DIST = ROOT / "dist"
APP_NAME = "SSH Dashboard"
VERSION = "1.0.1"
BUNDLE_ID = "io.jphsystems.sshdashboard"
APP = DIST / f"{APP_NAME}.app"
ICNS = ROOT / "sshdash" / "ui" / "assets" / "SSHDashboard.icns"

LAUNCHER = r'''#!/bin/bash
# SSH Dashboard launcher — detects Python, explains how to install it, then runs.
set -u

HERE="$(cd "$(dirname "$0")" && pwd)"
APP_DIR="$(cd "$HERE/../Resources/app" && pwd)"

# Never write __pycache__ inside the signed bundle.
export PYTHONDONTWRITEBYTECODE=1

CANDIDATES=(
  python3.14 python3.13 python3.12 python3.11 python3.10 python3
  /opt/homebrew/bin/python3.14 /opt/homebrew/bin/python3.13 /opt/homebrew/bin/python3.12
  /usr/local/bin/python3.14 /usr/local/bin/python3.13 /usr/local/bin/python3.12
  /Library/Frameworks/Python.framework/Versions/Current/bin/python3
  /Library/Frameworks/Python.framework/Versions/3.14/bin/python3
  /Library/Frameworks/Python.framework/Versions/3.13/bin/python3
)

show_dialog() {
  # $1 = message, $2 = action button, $3 = icon (note|caution)
  osascript - "$1" "$2" "$3" <<'APPLESCRIPT'
on run argv
  set messageText to item 1 of argv
  set actionLabel to item 2 of argv
  set iconName to item 3 of argv
  if iconName is "caution" then
    display dialog messageText buttons {"Cancel", actionLabel} default button actionLabel with title "SSH Dashboard" with icon caution
  else
    display dialog messageText buttons {"Quit", actionLabel} default button actionLabel with title "SSH Dashboard" with icon note
  end if
  return button returned of result
end run
APPLESCRIPT
}

PYTHON=""
PY_VERSION=""
TK_VERSION=""
BASE_PYTHON=""

for candidate in "${CANDIDATES[@]}"; do
  if command -v "$candidate" >/dev/null 2>&1; then
    if [ -z "$BASE_PYTHON" ]; then
      if "$candidate" -c 'import sys; assert sys.version_info >= (3, 9)' >/dev/null 2>&1; then
        BASE_PYTHON="$candidate"
      fi
    fi
    PROBE="$("$candidate" -c 'import sys, tkinter; print(sys.version.split()[0] + "|" + str(tkinter.TkVersion))' 2>/dev/null || true)"
    if [ -n "$PROBE" ]; then
      PYTHON="$candidate"
      PY_VERSION="${PROBE%%|*}"
      TK_VERSION="${PROBE##*|}"
      break
    fi
  fi
done

if [ -z "$PYTHON" ]; then
  if [ -n "$BASE_PYTHON" ]; then
    MESSAGE="Python is installed at $BASE_PYTHON, but its Tk library is missing, so SSH Dashboard cannot draw its window.

Fix it with Homebrew:
  brew install python-tk@3.14

Or install the full Python build from python.org, which includes Tk."
  else
    MESSAGE="SSH Dashboard needs Python 3.9 or newer with Tk 8.6.

Download Python from python.org — the macOS installer includes Tk:
  https://www.python.org/downloads/macos/

Or with Homebrew:
  brew install python-tk@3.14

Then open SSH Dashboard again."
  fi
  CHOICE="$(show_dialog "$MESSAGE" "Download Python" "caution")"
  if [ "$CHOICE" = "Download Python" ]; then
    open "https://www.python.org/downloads/macos/"
  fi
  exit 1
fi

# Confirm the detected runtime once per Python/Tk version.
ACK_DIR="$HOME/Library/Application Support/SSHDashboard"
ACK_FILE="$ACK_DIR/python-ack"
mkdir -p "$ACK_DIR" 2>/dev/null || true
STAMP="$PYTHON|$PY_VERSION|$TK_VERSION"

if [ "$(cat "$ACK_FILE" 2>/dev/null || true)" != "$STAMP" ]; then
  MESSAGE="Python $PY_VERSION with Tk $TK_VERSION detected.

SSH Dashboard will use:
$PYTHON

This confirmation appears once per Python version."
  CHOICE="$(show_dialog "$MESSAGE" "Continue" "note")"
  if [ "$CHOICE" != "Continue" ]; then
    exit 0
  fi
  printf '%s' "$STAMP" > "$ACK_FILE"
fi

exec "$PYTHON" "$APP_DIR/ssh_dashboard.py" "$@"
'''


def copytree(src: Path, dst: Path) -> None:
    shutil.copytree(
        src, dst,
        ignore=shutil.ignore_patterns("__pycache__", "*.pyc", ".DS_Store"),
    )


def main() -> None:
    if sys.platform != "darwin":
        sys.exit("The .app builder only runs on macOS")

    if APP.exists():
        shutil.rmtree(APP)
    macos = APP / "Contents" / "MacOS"
    resources = APP / "Contents" / "Resources"
    app_src = resources / "app"
    macos.mkdir(parents=True)
    resources.mkdir(parents=True)
    app_src.mkdir(parents=True)

    shutil.copy2(ROOT / "ssh_dashboard.py", app_src / "ssh_dashboard.py")
    copytree(ROOT / "sshdash", app_src / "sshdash")
    shutil.copy2(ICNS, resources / "SSHDashboard.icns")

    launcher = macos / APP_NAME
    launcher.write_text(LAUNCHER, encoding="utf-8")
    os.chmod(launcher, 0o755)

    info = {
        "CFBundleName": APP_NAME,
        "CFBundleDisplayName": APP_NAME,
        "CFBundleExecutable": APP_NAME,
        "CFBundleIdentifier": BUNDLE_ID,
        "CFBundleIconFile": "SSHDashboard.icns",
        "CFBundlePackageType": "APPL",
        "CFBundleShortVersionString": VERSION,
        "CFBundleVersion": "1",
        "CFBundleInfoDictionaryVersion": "6.0",
        "LSMinimumSystemVersion": "11.0",
        "NSHighResolutionCapable": True,
        "NSHumanReadableCopyright": "\u00a9 2026 JPHsystems",
    }
    with (APP / "Contents" / "Info.plist").open("wb") as handle:
        plistlib.dump(info, handle)
    (APP / "Contents" / "PkgInfo").write_text("APPL????", encoding="ascii")

    subprocess.run(
        ["codesign", "--force", "--deep", "--sign", "-", str(APP)],
        check=True, capture_output=True,
    )

    zip_path = DIST / f"SSH-Dashboard-{VERSION}-macOS-lite.zip"
    if zip_path.exists():
        zip_path.unlink()
    subprocess.run(
        ["ditto", "-c", "-k", "--sequesterRsrc", "--keepParent", str(APP), str(zip_path)],
        check=True,
    )

    size = sum(path.stat().st_size for path in APP.rglob("*") if path.is_file())
    print(f"Built {APP}")
    print(f"  bundle : {size / 1024:.0f} KB")
    print(f"  zip    : {zip_path} ({zip_path.stat().st_size / 1024:.0f} KB)")
    print("  Python : detected at launch, not bundled")


if __name__ == "__main__":
    main()
