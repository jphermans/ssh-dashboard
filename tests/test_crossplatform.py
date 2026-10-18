"""Cross-platform integrity tests: CRLF files must round-trip unchanged."""

from __future__ import annotations

import os
import shutil
import stat
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from sshdash.store import SshStore, build_snapshot  # noqa: E402

KEY = "AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl"


class _NullLog:
    def append(self, *args, **kwargs) -> None:
        pass


class CrlfTests(unittest.TestCase):
    def setUp(self) -> None:
        self.root = Path(tempfile.mkdtemp(prefix="sshdash-crlf-"))
        self.ssh = self.root / ".ssh"
        self.ssh.mkdir()
        self.config = self.ssh / "config"
        self.config.write_bytes(
            b"# windows style\r\n"
            b"Host alpha\r\n"
            b"    HostName alpha.example.com\r\n"
            b"    Port 2200\r\n"
            b"\r\n"
            b"Host beta\r\n"
            b"    HostName beta.example.com\r\n"
        )
        self.known = self.ssh / "known_hosts"
        self.known.write_bytes(f"alpha ssh-ed25519 {KEY}\r\nbeta ssh-ed25519 {KEY}\r\n".encode())
        self.store = SshStore(self.ssh, activity=_NullLog())

    def tearDown(self) -> None:
        shutil.rmtree(self.root, ignore_errors=True)

    def test_config_edit_keeps_crlf(self) -> None:
        block = next(
            block for block in self.store.snapshot().config_blocks
            if "alpha" in block.patterns
        )
        self.store.update_config_block(block, ["alpha"], {"user": "root"})
        raw = self.config.read_bytes()
        self.assertIn(b"User root\r\n", raw)
        self.assertNotIn(b"User root\n", raw.replace(b"User root\r\n", b""))
        self.assertTrue(raw.startswith(b"# windows style\r\n"))
        self.assertIn(b"Host beta\r\n", raw)

    def test_known_hosts_delete_keeps_crlf(self) -> None:
        snapshot = build_snapshot(self.ssh)
        alpha = next(host for host in snapshot.hosts if host.label == "alpha")
        self.store.delete_hosts([alpha], remove_config=False)
        raw = self.known.read_bytes()
        self.assertNotIn(b"alpha", raw)
        self.assertIn(b"beta ssh-ed25519", raw)
        self.assertTrue(raw.endswith(b"\r\n"))
        self.assertNotIn(b"\n\n", raw.replace(b"\r\n", b""))

    def test_permissions_preserved(self) -> None:
        self.config.chmod(0o640)
        block = next(
            block for block in self.store.snapshot().config_blocks
            if "beta" in block.patterns
        )
        self.store.update_config_block(block, ["beta"], {"port": "2222"})
        if os.name != "nt":
            self.assertEqual(stat.S_IMODE(self.config.stat().st_mode), 0o640)
        else:
            self.assertTrue(self.config.exists())


if __name__ == "__main__":
    unittest.main(verbosity=2)
