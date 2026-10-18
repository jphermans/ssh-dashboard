"""Build the lightweight macOS app bundle.

Unlike the PyInstaller build, this bundle contains no Python runtime: it ships
the sources plus a launcher that uses an installed Python 3.9+/Tk 8.6, and shows
a download prompt when none is available. Result: ~1 MB instead of ~30 MB.

    python3 packaging/build_macos_lite.py
"""

from __future__ import annotations

import os
import plistlib
import shutil
import stat
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DIST = ROOT / "dist"
APP_NAME = "SSH Dashboard"
VERSION = "1.0.0"
BUNDLE_ID = "io.jphsystems.sshdashboard"
APP = DIST / f"{APP_NAME}.app"
ICNS = ROOT / "sshdash" / "ui" / "assets" / "SSHDashboard.icns"

LAUNCHER = r'''#!/bin/bash
# SSH Dashboard launcher: runs the app with an installed Python (3.9+, Tk 8.6+).
set -u

HERE="$(cd "$(dirname "$0")" && pwd)"
APP_DIR="$(cd "$HERE/../Resources/app" && pwd)"

# Never write __pycache__ inside the signed bundle.
export PYTHONDONTWRITEBYTECODE=1

find_python() {
  local candidates=(
    python3.14 python3.13 python3.12 python3.11 python3.10 python3
    /opt/homebrew/bin/python3.14 /opt/homebrew/bin/python3.13 /opt/homebrew/bin/python3.12
    /usr/local/bin/python3.14 /usr/local/bin/python3.13 /usr/local/bin/python3.12
  )
  local candidate
  for candidate in "${candidates[@]}"; do
    if command -v "$candidate" >/dev/null 2>&1; then
      if "$candidate" -c 'import tkinter,sys; assert sys.version_info >= (3,9); assert float(tkinter.TkVersion) >= 8.6' >/dev/null 2>&1; then
        printf '%s' "$candidate"
        return 0
      fi
    fi
  done
  return 1
}

PYTHON="$(find_python || true)"

if [ -z "$PYTHON" ]; then
  CHOICE="$(osascript <<'APPLESCRIPT'
try
  display dialog "SSH Dashboard needs Python 3.9 or newer with Tk 8.6.

Download Python from python.org, install it, then open SSH Dashboard again." buttons {"Cancel", "Download Python"} default button "Download Python" with title "Python required" with icon caution
  return button returned of result
on error
  return "Cancel"
end try
APPLESCRIPT
)"
  if [ "$CHOICE" = "Download Python" ]; then
    open "https://www.python.org/downloads/macos/"
  fi
  exit 1
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

    # Ad-hoc signature so Gatekeeper accepts a locally built bundle.
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
    print("  Python : required on the target Mac (not bundled)")


if __name__ == "__main__":
    main()
