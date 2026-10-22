# macOS distribution and App Store assessment

## Short answer

**This build is ready for direct download, not for the Mac App Store.** The app is
an Apple silicon macOS 12+ bundle signed ad-hoc. It runs immediately on the machine
that builds it, but a downloaded copy is quarantined by Gatekeeper because it is
not notarized.

Two separate things are often called "Apple store submission":

1. **Developer ID + notarization** — the normal route for a website/GitHub
   download. Keeps the current architecture and features. Needs a paid Apple
   Developer Program membership and a **Developer ID Application** certificate.
2. **Mac App Store** — a different route with different rules. Requires the App
   Sandbox, an **Apple Distribution** certificate, a Mac App Store provisioning
   profile and App Review. The current design cannot pass review unchanged
   (details below).

## What is installed on this machine

```
security find-identity -v -p codesigning
  1) 57BEEF... "Apple Development: Jean-Pierre Hermans (SRJV4E5SWD)"
```

Only an **Apple Development** certificate is present. It is for running debug
builds on registered machines; it cannot sign a notarized download and it cannot
submit to the Mac App Store. `xcrun notarytool history` also has no stored
credentials.

## Route 1 — direct distribution (recommended for this app)

This preserves `~/.ssh`, `ssh_config` aliases, ProxyJump, the SSH agent and the
system `ssh` binary.

### Prerequisites

- Apple Developer Program membership.
- **Developer ID Application** certificate in the login keychain.
- An app-specific password or App Store Connect API key for `notarytool`.
- Store notary credentials once:

```sh
xcrun notarytool store-credentials "ssh-dashboard-notary" \
  --apple-id "you@example.com" \
  --team-id "TEAMID" \
  --password "app-specific-password"
```

### Sign, notarize, staple

Run after building the Apple silicon `.app` and DMG:

```sh
scripts/notarize-macos.sh --check
scripts/notarize-macos.sh \
  "dotnet/dist/SSH Dashboard.app" \
  "dotnet/dist/SSH-Dashboard-2.1.14-macOS-arm64.dmg"
```

The script auto-detects a **Developer ID Application** certificate, signs the
app with the hardened runtime, signs the DMG, submits it with `notarytool`,
staples the ticket and runs the Gatekeeper checks. It also installs a clear
failure message if only an Apple Development certificate is available.

On the build machine right now `--check` reports only:

```
Apple Development: Jean-Pierre Hermans (SRJV4E5SWD)
notary credentials: not stored
```

That certificate cannot notarize a download, so the current release remains
ad-hoc signed. Install a Developer ID certificate and store the notary profile
before running the command above.

`spctl` must say `accepted` and `source=Notarized Developer ID`. Only then will a
downloaded DMG open with a normal double-click and no Privacy & Security detour.

## Route 2 — Mac App Store

### Hard blockers in the current design

1. **App Sandbox is mandatory.** Apple's documentation states the App Sandbox
   entitlement is "an App Store requirement for any app that you submit to the
   Mac App Store for review."
2. **`~/.ssh` is outside the sandbox container.** The app would need the user to
   pick the folder in an open panel and persist a security-scoped bookmark.
   There is no automatic `~/.ssh` lookup.
3. **`/usr/bin/ssh` as a subprocess is not viable.** A spawned process inherits
   the parent's sandbox. It cannot read the user's `config`, `known_hosts` or
   keys, and the SSH agent socket (`SSH_AUTH_SOCK`) is blocked by the sandbox
   (`EPERM`). Bundling an `ssh` binary does not solve the file-access problem.
4. **PuTTY conversion calls `puttygen`.** A sandboxed app cannot execute an
   arbitrary user-installed helper from `/opt/homebrew` or `/usr/local`.
5. **Signing materials are missing.** Submission needs an **Apple Distribution**
   certificate and a **Mac App Store** provisioning profile. Neither exists on
   this machine.

### What a Mac App Store version would require

A separate, sandbox-compatible product:

- Replace the `ssh` child process with a managed SSH library (for example
  **SSH.NET** on .NET, or Citadel/SwiftNIO on a native rewrite).
- Implement password and key authentication in-app; import keys through a file
  picker and store access as security-scoped bookmarks, or store keys in the
  Keychain.
- Re-implement the parts of `ssh_config` that matter (host, user, port,
  IdentityFile, ProxyJump) instead of delegating to OpenSSH. The agent is not
  available.
- Add entitlements: `com.apple.security.app-sandbox`,
  `com.apple.security.network.client`, and
  `com.apple.security.files.user-selected.read-write`.
- Sign with **Apple Distribution**, embed a Mac App Store provisioning profile,
  upload with Transporter or `xcrun altool`, then complete App Store Connect
  metadata, privacy answers, screenshots and App Review.

This is effectively a second SSH engine, not a packaging change. Estimate:
several weeks of work plus review risk around a terminal that executes remote
commands (guideline 2.5.2 — self-contained apps, no downloaded code).

### If you still want to pursue it

1. In Xcode → Settings → Accounts → Manage Certificates, create
   **Apple Distribution**.
2. In the developer portal create an explicit App ID
   (`io.jphsystems.sshdashboard`), then a **Mac App Store** provisioning profile.
3. Add an entitlements file and enable App Sandbox in the project.
4. Rewrite the connection layer and key access as above.
5. Archive in Xcode, validate, upload to App Store Connect, submit for review.

## iOS

An iOS version is a separate app: no `~/.ssh`, no `fork/exec`, no system `ssh`.
It needs a SwiftUI/UIKit shell, a managed SSH library and its own key import
flow. The current Avalonia desktop code does not port to iOS as-is.

## Recommendation

Ship 2.1.14 as a **notarized Developer ID download** once the Developer ID
certificate is available. Keep the App Store as a possible future product with
an in-app SSH engine, not as a repackaging of this build.
