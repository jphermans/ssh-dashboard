#!/bin/bash
# Double-click launcher for SSH Dashboard (macOS opens .command files in Terminal).
cd "$(dirname "$0")" || exit 1
exec ./run.sh
