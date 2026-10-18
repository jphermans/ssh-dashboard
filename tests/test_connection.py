"""Connection test: must work on localhost without any privacy grant."""

from __future__ import annotations

import socket
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from sshdash.ui.app import probe_port  # noqa: E402


class ProbePortTests(unittest.TestCase):
    def test_open_local_port(self) -> None:
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as server:
            server.bind(("127.0.0.1", 0))
            server.listen(1)
            port = server.getsockname()[1]
            ok, error = probe_port("127.0.0.1", port, timeout=2)
        self.assertTrue(ok, error)
        self.assertEqual(error, "")

    def test_closed_local_port(self) -> None:
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as probe:
            probe.bind(("127.0.0.1", 0))
            port = probe.getsockname()[1]
        ok, error = probe_port("127.0.0.1", port, timeout=1)
        self.assertFalse(ok)
        self.assertTrue(error)


if __name__ == "__main__":
    unittest.main(verbosity=2)
