#!/bin/bash
# Cross-build the small Windows launcher and package it from macOS (mingw-w64).
# The result needs Python 3.9+/Tk 8.6 on the Windows machine; no runtime is bundled.
set -euo pipefail
cd "$(dirname "$0")/.."

CC=${CC:-x86_64-w64-mingw32-gcc}
RC=${RC:-x86_64-w64-mingw32-windres}
OUT=dist/windows
ZIP=dist/SSH-Dashboard-1.0.0-windows-x64.zip

if ! command -v "$CC" >/dev/null 2>&1; then
  echo "mingw-w64 not found. Install it with: brew install mingw-w64" >&2
  exit 1
fi

rm -rf "$OUT" "$ZIP"
mkdir -p "$OUT" build

"$RC" launcher/windows/launcher.rc -O coff -o build/launcher.res
"$CC" -O2 -s -municode -mwindows \
  launcher/windows/launcher.c build/launcher.res \
  -o "$OUT/SSH Dashboard.exe" \
  -luser32 -lshell32 -ladvapi32

cp ssh_dashboard.py README.md "$OUT/"
cp launcher/windows/install-python.ps1 "$OUT/"
cp -R sshdash "$OUT/sshdash"
find "$OUT" -name '__pycache__' -type d -prune -exec rm -rf {} +

(cd dist && zip -qr "$(basename "$ZIP")" windows)

echo
echo "Launcher: $(stat -f%z "$OUT/SSH Dashboard.exe") bytes"
echo "Package:  $ZIP ($(du -h "$ZIP" | cut -f1))"
echo "Note:     Python 3.9+ with Tk 8.6 is required on the target PC."
