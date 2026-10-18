"""Unit tests for the parsers and the store (stdlib unittest, no deps).

Run with:  python3 -m unittest discover -s tests -v
"""

from __future__ import annotations

import base64
import hashlib
import hmac
import os
import shutil
import stat
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from sshdash import parsers  # noqa: E402
from sshdash.model import ConfigBlock  # noqa: E402
from sshdash.store import SshStore, StoreError, build_snapshot  # noqa: E402

ED25519_KEY = (
    "AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl"
)
RSA_KEY = (
    "AAAAB3NzaC1yc2EAAAADAQABAAABgQC7K/1hS3Hqjv0n5Kk0S9m2B0Zx1jCq2Yh2"
    "nO1cE5f5H9d3Q2a+1i4lP+8oUu5x1bM3bW7k6Y5T4b3Y2r1Q0pO9n8M7l6K5J4H3G2"
)


def hashed(hostname: str) -> str:
    salt = b"0123456789abcdefghij"
    digest = hmac.new(salt, hostname.encode(), hashlib.sha1).digest()
    return "|1|{}|{}".format(
        base64.b64encode(salt).decode(), base64.b64encode(digest).decode()
    )


class TempSsh(unittest.TestCase):
    def setUp(self) -> None:
        self.root = Path(tempfile.mkdtemp(prefix="sshdash-test-"))
        self.ssh = self.root / ".ssh"
        self.ssh.mkdir()
        self.app = self.root / "appdata"

    def tearDown(self) -> None:
        shutil.rmtree(self.root, ignore_errors=True)

    def write(self, name: str, content: str, mode: int = 0o600) -> Path:
        path = self.ssh / name
        path.write_text(content, encoding="utf-8")
        os.chmod(path, mode)
        return path


class KnownHostsParserTests(TempSsh):
    def test_parses_plain_markers_comments_and_ports(self) -> None:
        self.write(
            "known_hosts",
            "# comment\n"
            "\n"
            "github.com ssh-ed25519 AAAAC3Nz\n"
            "github.com,140.82.121.4 ssh-rsa AAAAB3Nz\n"
            "@revoked bad.example.com ssh-rsa AAAAB3Nz\n"
            "[example.com]:2222 ssh-ed25519 AAAAC3Nz user@host\n"
            "broken line\n",
        )
        lines, warnings = parsers.parse_known_hosts(self.ssh / "known_hosts")
        self.assertEqual(len(lines), 4)
        self.assertEqual(lines[0].tokens, ("github.com",))
        self.assertEqual(lines[1].tokens, ("github.com", "140.82.121.4"))
        self.assertEqual(lines[2].marker, "@revoked")
        self.assertEqual(lines[3].comment, "user@host")
        self.assertTrue(any("Line 7" in w for w in warnings))
        self.assertEqual(parsers.split_host_token("[example.com]:2222"), ("example.com", 2222))

    def test_hashed_token_roundtrip(self) -> None:
        token = hashed("secret.example.com")
        self.assertTrue(parsers.hashed_token_matches(token, "secret.example.com"))
        self.assertFalse(parsers.hashed_token_matches(token, "other.example.com"))
        lines, _ = parsers.parse_known_hosts(
            self.write("known_hosts", f"{token} ssh-ed25519 {ED25519_KEY}\n")
        )
        self.assertTrue(lines[0].hashed)

    def test_fingerprint_matches_ssh_format(self) -> None:
        fp = parsers.key_fingerprint(ED25519_KEY)
        self.assertTrue(fp.startswith("SHA256:"))
        raw = base64.b64decode(ED25519_KEY + "===")
        expected = base64.b64encode(hashlib.sha256(raw).digest()).decode().rstrip("=")
        self.assertEqual(fp, "SHA256:" + expected)


class ConfigParserTests(TempSsh):
    def test_blocks_options_and_global(self) -> None:
        self.write(
            "config",
            "# global\n"
            "Host *\n"
            "    ServerAliveInterval 60\n"
            "\n"
            "Host pi5 nas-lab\n"
            "    HostName 192.0.2.1.10\n"
            "    User root\n"
            "    Port 2222\n"
            "    IdentityFile ~/.ssh/id_ed25519\n"
            "\n"
            "Host db-*\n"
            "    HostName %h.internal\n",
        )
        blocks, warnings = parsers.parse_ssh_config(self.ssh / "config")
        self.assertEqual(warnings, [])
        self.assertEqual(len(blocks), 3)
        self.assertTrue(blocks[0].is_global)
        pi5 = blocks[1]
        self.assertEqual(pi5.patterns, ("pi5", "nas-lab"))
        self.assertEqual(pi5.option("hostname"), "192.0.2.1.10")
        self.assertEqual(pi5.option("user"), "root")
        self.assertEqual(pi5.option("port"), "2222")
        self.assertTrue(pi5.deletable)
        self.assertEqual(pi5.end_line, 9)
        self.assertFalse(blocks[2].deletable)  # glob pattern
        self.assertTrue(pi5.matches("nas-lab"))
        self.assertFalse(pi5.matches("other"))

    def test_includes_are_read_only(self) -> None:
        self.write(
            "extra.conf",
            "Host included-host\n    HostName 10.0.0.9\n",
        )
        self.write("config", f"Include {self.ssh / 'extra.conf'}\nHost local\n    HostName 10.0.0.1\n")
        blocks, _ = parsers.parse_ssh_config(self.ssh / "config")
        included = [b for b in blocks if b.patterns == ("included-host",)]
        self.assertEqual(len(included), 1)
        self.assertFalse(included[0].editable)

    def test_negated_patterns(self) -> None:
        block = ConfigBlock(
            source=Path("/tmp/config"),
            start_line=1,
            end_line=2,
            patterns=("*.example.com", "!secret.example.com"),
        )
        self.assertTrue(block.matches("api.example.com"))
        self.assertFalse(block.matches("secret.example.com"))
        self.assertFalse(block.deletable)


class SnapshotTests(TempSsh):
    def setUp(self) -> None:
        super().setUp()
        self.write(
            "known_hosts",
            "github.com ssh-ed25519 " + ED25519_KEY + "\n"
            "github.com ssh-rsa " + RSA_KEY + "\n"
            "192.0.2.84 ssh-ed25519 " + ED25519_KEY + "\n",
        )
        self.write(
            "config",
            "Host *\n    ServerAliveInterval 60\n\n"
            "Host nas-lab\n    HostName 192.0.2.84\n    User root\n\n"
            "Host never-used\n    HostName 10.9.9.9\n    User pi\n",
        )

    def test_groups_key_types_and_links_config(self) -> None:
        snap = build_snapshot(self.ssh)
        self.assertEqual(len(snap.hosts), 3)
        github = next(h for h in snap.hosts if h.label == "github.com")
        self.assertEqual(len(github.tokens), 1)
        self.assertEqual(len(github.key_types), 2)
        self.assertEqual(len(github.known_hosts_lines), 2)
        nas_lab = next(h for h in snap.hosts if h.alias == "nas-lab")
        self.assertEqual(nas_lab.user, "root")
        self.assertEqual(nas_lab.address, "192.0.2.84")
        self.assertEqual(nas_lab.port, 22)
        config_only = next(h for h in snap.hosts if h.label == "never-used")
        self.assertFalse(config_only.in_known_hosts)
        self.assertTrue(config_only.is_removable)

    def test_wildcards_never_become_rows(self) -> None:
        snap = build_snapshot(self.ssh)
        self.assertNotIn("*", [h.label for h in snap.hosts])


class StoreDeleteTests(TempSsh):
    def setUp(self) -> None:
        super().setUp()
        self.known = self.write(
            "known_hosts",
            "# keep me\n"
            "github.com ssh-ed25519 " + ED25519_KEY + "\n"
            "github.com ssh-rsa " + RSA_KEY + "\n"
            "alpha,beta ssh-ed25519 " + ED25519_KEY + "\n"
            f"{hashed('secret.example.com')} ssh-ed25519 {ED25519_KEY}\n",
        )
        self.config = self.write(
            "config",
            "# header\n"
            "Host *\n    ServerAliveInterval 60\n\n"
            "Host alpha\n    HostName alpha.example.com\n\n"
            "Host beta\n    HostName beta.example.com\n",
        )
        self.store = SshStore(self.ssh, activity=_NullLog())

    def test_deletes_selected_host_and_backs_up(self) -> None:
        snap = self.store.snapshot()
        github = next(h for h in snap.hosts if h.label == "github.com")
        result = self.store.delete_hosts([github], remove_config=True)
        self.assertEqual(result.hosts_removed, 1)
        self.assertEqual(result.lines_removed, 2)
        remaining = self.known.read_text()
        self.assertNotIn("github.com", remaining)
        self.assertIn("# keep me", remaining)
        if os.name != "nt":
            self.assertEqual(stat.S_IMODE(self.known.stat().st_mode), 0o600)
        self.assertTrue(result.backups and result.backups[0].exists())
        self.assertIn("github.com", result.backups[0].read_text())

    def test_shared_line_is_one_entry_and_removed_whole(self) -> None:
        snap = self.store.snapshot()
        alpha = next(h for h in snap.hosts if "alpha" in h.label)
        self.assertEqual(alpha.tokens, ("alpha", "beta"))
        self.store.delete_hosts([alpha], remove_config=False)
        remaining = self.known.read_text()
        self.assertNotIn("alpha", remaining)
        self.assertIn("github.com", remaining)

    def test_deletes_hashed_entry(self) -> None:
        snap = self.store.snapshot()
        hashed_host = next(h for h in snap.hosts if h.hashed)
        self.store.delete_hosts([hashed_host], remove_config=False)
        self.assertNotIn("|1|", self.known.read_text())

    def test_config_block_removed_with_neighbours_intact(self) -> None:
        snap = self.store.snapshot()
        alpha = next(h for h in snap.hosts if h.label == "alpha")
        result = self.store.delete_hosts([alpha], remove_config=True)
        self.assertEqual(result.config_blocks_removed, 1)
        text = self.config.read_text()
        self.assertNotIn("Host alpha", text)
        self.assertIn("Host beta", text)
        self.assertIn("ServerAliveInterval", text)
        self.assertIn("# header", text)

    def test_restore_backup(self) -> None:
        snap = self.store.snapshot()
        github = next(h for h in snap.hosts if h.label == "github.com")
        result = self.store.delete_hosts([github], remove_config=False)
        backup = result.backups[0]
        self.store.restore_backup(backup)
        self.assertIn("github.com", self.known.read_text())

    def test_stale_block_is_not_removed(self) -> None:
        snap = self.store.snapshot()
        alpha = next(h for h in snap.hosts if h.label == "alpha")
        # Someone edits the file after the scan.
        self.config.write_text("Host gamma\n    HostName 1.2.3.4\n")
        result = self.store.delete_hosts([alpha], remove_config=True)
        self.assertEqual(result.config_blocks_removed, 0)
        self.assertTrue(result.warnings)
        self.assertIn("Host gamma", self.config.read_text())

    def test_missing_file_raises_store_error(self) -> None:
        snap = self.store.snapshot()          # taken while the file exists
        alpha = next(h for h in snap.hosts if h.label == "alpha")
        self.known.unlink()                   # deleted by someone else
        with self.assertRaises(StoreError):
            self.store.delete_hosts([alpha], remove_config=False)

    def test_deleting_config_only_host_without_config_flag_is_a_noop(self) -> None:
        snapshot = build_snapshot(self.ssh)
        config_only = next(h for h in snapshot.hosts if not h.in_known_hosts)
        self.assertEqual(config_only.tokens, ())
        result = self.store.delete_hosts([config_only], remove_config=False)
        self.assertEqual((result.hosts_removed, result.config_blocks_removed), (0, 0))


class ConfigEditTests(TempSsh):
    def setUp(self) -> None:
        super().setUp()
        self.config = self.write(
            "config",
            "# global defaults\n"
            "Host *\n    ServerAliveInterval 60\n\n"
            "Host alpha alpha-alt\n"
            "    HostName alpha.example.com\n"
            "    User olduser\n"
            "    Port 2200\n"
            "    # keep this comment\n"
            "    ForwardAgent yes\n\n"
            "Host beta\n"
            "    HostName beta.example.com\n"
            "    IdentityFile ~/.ssh/id_beta\n"
            "    IdentityFile ~/.ssh/id_beta_old\n",
        )
        self.store = SshStore(self.ssh, activity=_NullLog())
        self.block = next(
            block for block in self.store.snapshot().config_blocks
            if "alpha" in block.patterns
        )

    def test_edits_options_and_preserves_everything_else(self) -> None:
        backup = self.store.update_config_block(
            self.block,
            ["alpha", "alpha-alt", "alpha2"],
            {
                "hostname": "new.example.com",
                "user": "root",
                "port": "",                       # remove the directive
                "identityfile": "~/.ssh/id_alpha",
                "proxyjump": "jump.example.com",
            },
        )
        text = self.config.read_text()
        self.assertIn("Host alpha alpha-alt alpha2", text)
        self.assertIn("HostName new.example.com", text)
        self.assertIn("User root", text)
        self.assertNotIn("Port 2200", text)
        self.assertIn("IdentityFile ~/.ssh/id_alpha", text)
        self.assertIn("ProxyJump jump.example.com", text)
        self.assertIn("# keep this comment", text)
        self.assertIn("ForwardAgent yes", text)
        self.assertIn("Host beta", text)
        self.assertIn("IdentityFile ~/.ssh/id_beta_old", text)  # untouched other block
        self.assertIn("ServerAliveInterval 60", text)
        if os.name != "nt":
            self.assertEqual(stat.S_IMODE(self.config.stat().st_mode), 0o600)
        self.assertTrue(backup.exists())
        self.assertIn("Port 2200", backup.read_text())

    def test_blank_value_removes_only_that_directive(self) -> None:
        self.store.update_config_block(self.block, ["alpha", "alpha-alt"], {"user": ""})
        text = self.config.read_text()
        self.assertNotIn("User olduser", text)
        self.assertIn("HostName alpha.example.com", text)
        self.assertIn("ForwardAgent yes", text)

    def test_stale_block_is_rejected(self) -> None:
        self.config.write_text("Host gamma\n    HostName 10.0.0.9\n")
        with self.assertRaises(StoreError):
            self.store.update_config_block(self.block, ["alpha"], {"user": "root"})
        self.assertIn("Host gamma", self.config.read_text())

    def test_globals_and_patterns_are_not_editable(self) -> None:
        global_block = next(
            block for block in self.store.snapshot().config_blocks if block.is_global
        )
        with self.assertRaises(StoreError):
            self.store.update_config_block(global_block, ["*"], {"user": "root"})


class _NullLog:
    def append(self, *args, **kwargs) -> None:  # pragma: no cover - test double
        pass


if __name__ == "__main__":
    unittest.main(verbosity=2)
