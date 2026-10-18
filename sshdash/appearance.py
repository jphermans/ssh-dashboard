"""Resolve the effective light/dark appearance.

Preferences store one of ``system``, ``dark`` or ``light``. ``system`` follows
the OS setting and is re-evaluated while the app runs.
"""

from __future__ import annotations

import os
import subprocess
import sys
from typing import Literal

Theme = Literal["dark", "light"]
Preference = Literal["system", "dark", "light"]
PREFERENCES = ("system", "dark", "light")


def system_theme() -> Theme:
    """Return the OS appearance without opening a window or prompting."""
    if sys.platform == "darwin":
        try:
            result = subprocess.run(
                ["defaults", "read", "-g", "AppleInterfaceStyle"],
                capture_output=True, text=True, timeout=3,
            )
            return "dark" if result.stdout.strip().lower() == "dark" else "light"
        except (OSError, subprocess.SubprocessError):
            return "light"

    if os.name == "nt":
        try:
            import winreg

            with winreg.OpenKey(
                winreg.HKEY_CURRENT_USER,
                r"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            ) as key:
                value, _ = winreg.QueryValueEx(key, "AppsUseLightTheme")
                return "light" if value else "dark"
        except (OSError, ImportError):
            return "light"

    try:
        result = subprocess.run(
            ["gsettings", "get", "org.gnome.desktop.interface", "color-scheme"],
            capture_output=True, text=True, timeout=3,
        )
        if "dark" in result.stdout.lower():
            return "dark"
    except (OSError, subprocess.SubprocessError):
        pass
    if "dark" in os.environ.get("GTK_THEME", "").lower():
        return "dark"
    return "light"


def resolve_theme(preference: str) -> Theme:
    """Map a stored preference to a concrete palette key."""
    if preference == "dark":
        return "dark"
    if preference == "light":
        return "light"
    return system_theme()
