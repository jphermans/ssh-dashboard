#!/bin/bash
# Build "SSH Dashboard.app" with the product icon and bundled assets.
set -euo pipefail
cd "$(dirname "$0")/.."

PYTHON=${PYTHON:-python3}
"$PYTHON" -m pip install --upgrade pyinstaller
"$PYTHON" -m PyInstaller --noconfirm --clean packaging/SSHDashboard.spec
# Remove the intermediate COLLECT folder; the .app is the deliverable.
rm -rf "dist/SSH Dashboard"

echo
echo "Built: dist/SSH Dashboard.app"
echo "Icon:  sshdash/ui/assets/SSHDashboard.icns"
