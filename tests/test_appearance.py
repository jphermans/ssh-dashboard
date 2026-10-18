"""Tests for appearance preferences and their persistence."""

from __future__ import annotations

import os
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from sshdash.appearance import PREFERENCES, resolve_theme, system_theme  # noqa: E402
from sshdash.config import Settings  # noqa: E402


class AppearanceTests(unittest.TestCase):
    def test_system_theme_is_dark_or_light(self) -> None:
        self.assertIn(system_theme(), ("dark", "light"))

    def test_preferences_map_to_palettes(self) -> None:
        self.assertEqual(resolve_theme("dark"), "dark")
        self.assertEqual(resolve_theme("light"), "light")
        self.assertEqual(resolve_theme("system"), system_theme())
        self.assertEqual(set(PREFERENCES), {"system", "dark", "light"})

    def test_settings_default_is_system(self) -> None:
        self.assertEqual(Settings().theme, "system")

    def test_system_preference_survives_restart(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            with mock.patch.dict(os.environ, {"SSHDASH_APP_DIR": directory}):
                settings = Settings(theme="system")
                settings.save()
                reloaded = Settings.load()
                self.assertEqual(reloaded.theme, "system")
                self.assertTrue((Path(directory) / "settings.json").exists())

    def test_explicit_preference_survives_restart(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            with mock.patch.dict(os.environ, {"SSHDASH_APP_DIR": directory}):
                Settings(theme="light").save()
                self.assertEqual(Settings.load().theme, "light")


if __name__ == "__main__":
    unittest.main(verbosity=2)
