#!/usr/bin/env bash
# Sign, notarize and staple the macOS app and DMG.
#
# Prerequisites (one-time):
#   1. A "Developer ID Application" certificate in the login keychain.
#      An "Apple Development" certificate cannot notarize a download.
#   2. Store notary credentials:
#        xcrun notarytool store-credentials "ssh-dashboard-notary" \
#          --apple-id "you@example.com" --team-id "TEAMID" \
#          --password "app-specific-password"
#
# Usage:
#   scripts/notarize-macos.sh \
#     "dotnet/dist/SSH Dashboard.app" \
#     "dotnet/dist/SSH-Dashboard-2.1.12-macOS-arm64.dmg"
#
# Environment:
#   DEVELOPER_ID     optional; auto-detected from the keychain
#   NOTARY_PROFILE   default: ssh-dashboard-notary
set -euo pipefail

NOTARY_PROFILE="${NOTARY_PROFILE:-ssh-dashboard-notary}"

if [[ "${1:-}" == "--check" ]]; then
  echo "Code-signing identities:"
  security find-identity -v -p codesigning
  echo
  echo "Notary credentials for '$NOTARY_PROFILE':"
  if xcrun notarytool history --keychain-profile "$NOTARY_PROFILE" >/dev/null 2>&1; then
    echo "available"
  else
    echo "not stored (run notarytool store-credentials)"
  fi
  exit 0
fi

APP="${1:?path to .app}"
DMG="${2:?path to .dmg}"

if [[ -z "${DEVELOPER_ID:-}" ]]; then
  DEVELOPER_ID="$(security find-identity -v -p codesigning 2>/dev/null |
    sed -n 's/.*"\(Developer ID Application:.*\)"/\1/p' | head -1)"
fi
if [[ -z "${DEVELOPER_ID:-}" ]]; then
  cat >&2 <<'EOF'
No "Developer ID Application" certificate found.

Install one from the Apple Developer portal, then run this script again.
An "Apple Development" certificate (the kind used for local debugging) cannot
notarize a downloadable build.
EOF
  exit 2
fi

command -v xcrun >/dev/null || { echo "xcrun is required" >&2; exit 2; }
[[ -d "$APP" ]] || { echo "App bundle not found: $APP" >&2; exit 2; }
[[ -f "$DMG" ]] || { echo "DMG not found: $DMG" >&2; exit 2; }

echo "Signing app with: $DEVELOPER_ID"
codesign --force --deep --options runtime --timestamp --sign "$DEVELOPER_ID" "$APP"
codesign --verify --deep --strict --verbose=2 "$APP"

echo "Signing DMG with: $DEVELOPER_ID"
codesign --force --timestamp --sign "$DEVELOPER_ID" "$DMG"
codesign --verify --verbose=2 "$DMG"

echo "Notarizing $DMG (this can take a few minutes)"
xcrun notarytool submit "$DMG" --keychain-profile "$NOTARY_PROFILE" --wait

echo "Stapling ticket"
xcrun stapler staple "$DMG"
xcrun stapler validate "$DMG"

echo "Gatekeeper check"
spctl -a -vvv -t install "$APP"
spctl -a -vvv -t open --context context:primary-signature "$DMG"

echo "Done: $DMG is signed, notarized and stapled."
