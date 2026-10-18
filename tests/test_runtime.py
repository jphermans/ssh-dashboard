"""Tests for the runtime guards (no window is created here)."""

from __future__ import annotations

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from sshdash import runtime  # noqa: E402


class RuntimeTests(unittest.TestCase):
    def test_tk_version_is_detected(self) -> None:
        version = runtime.tk_version()
        self.assertIsInstance(version, tuple)
        self.assertEqual(len(version), 2)
        self.assertGreaterEqual(version[0], 8)

    def test_minimum_is_86(self) -> None:
        self.assertEqual(runtime.MINIMUM_TK, (8, 6))

    def test_probe_rejects_missing_binary(self) -> None:
        self.assertIsNone(runtime._probe("/nonexistent/python-xyz"))

    def test_probe_accepts_current_interpreter(self) -> None:
        version = runtime._probe(sys.executable)
        if runtime.tk_version() >= runtime.MINIMUM_TK:
            self.assertIsNotNone(version)
            self.assertGreaterEqual(version, runtime.MINIMUM_TK)

    def test_configure_platform_is_safe(self) -> None:
        runtime.configure_platform()  # must never raise on any platform

    def test_ensure_supported_tk_is_a_noop_when_supported(self) -> None:
        if runtime.tk_version() >= runtime.MINIMUM_TK:
            runtime.ensure_supported_tk(Path("ssh_dashboard.py"), [])  # returns


if __name__ == "__main__":
    unittest.main(verbosity=2)
