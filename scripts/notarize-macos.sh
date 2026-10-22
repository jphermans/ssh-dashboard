#!/usr/bin/env bash
# Sign, notarize and staple the macOS app and DMG.
#
# Required:
#   DEVELOPER_ID="Developer ID Application: Name (TEAMID)"
#   NOTARY_PROFILE="ssh-dashboard-notary"   # from notarytool store-credentials
#
# Usage:
#   scripts/notarize-macos.sh "dotnet/dist/SSH Dashboard.app" \
#     "dotnet/dist/SSH-Dashboard-2.1.12-macOS-arm64.dmg"
set -euo pipefail

APP="${1:?path to .app}"
DMG="${2:?path to .dmg}"
: "${DEVELOPER_ID:?set DEVELOPER_ID}"
: "${NOTARY_PROFILE:?set NOTARY_PROFILE}"

echo "Signing $APP"
codesign --force --deep --options runtime --timestamp --sign "$DEVELOPER_ID" "$APP"
codesign --verify --deep --strict --verbose=2 "$APP"

echo "Notarizing $DMG (this can take a few minutes)"
xcrun notarytool submit "$DMG" --keychain-profile "$NOTARY_PROFILE" --wait

echo "Stapling"
xcrun stapler staple "$DMG"
xcrun stapler validate "$DMG"

echo "Gatekeeper check"
spctl -a -vvv -t install "$APP"
echo "Done."
