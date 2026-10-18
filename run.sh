#!/bin/bash
# Launch SSH Dashboard with the best available Python that has a modern Tk.
set -euo pipefail
cd "$(dirname "$0")"

CANDIDATES=(
  python3.14
  python3.13
  python3.12
  python3.11
  /opt/homebrew/bin/python3.14
  /opt/homebrew/bin/python3.13
  /opt/homebrew/bin/python3.12
  python3
)

for candidate in "${CANDIDATES[@]}"; do
  if command -v "$candidate" >/dev/null 2>&1; then
    if "$candidate" -c 'import tkinter, sys; assert sys.version_info >= (3, 9); assert float(tkinter.TkVersion) >= 8.6' >/dev/null 2>&1; then
      exec "$candidate" ssh_dashboard.py "$@"
    fi
  fi
done

echo "SSH Dashboard needs Python 3.9+ with Tk 8.6 or newer." >&2
echo "On macOS: brew install python-tk@3.14" >&2
exit 1
