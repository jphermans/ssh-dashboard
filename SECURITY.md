# Security policy

## Supported versions

Only the latest release of SSH Dashboard receives fixes. Older builds are not
patched.

## Reporting a vulnerability

Please report issues privately, not in a public GitHub issue:

- Email **jphermans@fastmail.com** with a description, the affected version and
  the steps to reproduce.
- You can also use GitHub's [private vulnerability
  reporting](https://github.com/jphermans/ssh-dashboard/security/advisories/new).

Expect an initial response within **7 days**. Please give me a chance to ship a
fix before publishing details.

## What SSH Dashboard touches

The application reads and writes files under `~/.ssh`: `known_hosts`,
`ssh_config`, private and public keys, and `~/.ssh/backups`. It also starts
`ssh`, `scp` and `rsync` processes for the hosts you configure.

- Private keys, passphrases and passwords are never sent anywhere. There is no
  telemetry, no analytics and no network traffic to any server owned by this
  project.
- A password typed into a transfer prompt is held in memory for that operation
  only and passed to OpenSSH through a private temporary file that is deleted
  immediately afterwards. It is never written to settings, logs or backups.
- Backups are written next to the originals, with the same permissions as the
  file they came from. Treat `~/.ssh` as sensitive as a whole.
- Downloads are self-contained builds. Verify them against the checksums on the
  [latest release](https://github.com/jphermans/ssh-dashboard/releases/latest)
  (`SHA256SUMS.txt`).

## Out of scope

- Reports that require an attacker who already has read access to the user's
  home directory.
- Vulnerabilities in OpenSSH itself or in .NET/Avalonia; report those upstream.
- Missing hardening headers on the static documentation site, unless they lead
  to a concrete attack.