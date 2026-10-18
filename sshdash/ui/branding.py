"""App artwork helpers.

Icons live inside the package so PyInstaller can bundle them: the same relative
path resolves in development and inside a frozen app (``sys._MEIPASS``).
"""

from __future__ import annotations

from pathlib import Path
from typing import Optional

import tkinter as tk

ASSETS = Path(__file__).resolve().parent / "assets"


def asset(name: str) -> Path:
    return ASSETS / name


def icon_path(size: int = 256) -> Path:
    return asset(f"icon_{size}.png")


def load_icon(master: Optional[tk.Misc] = None, size: int = 256) -> Optional[tk.PhotoImage]:
    """Load a PNG icon; returns ``None`` when the asset is missing or Tk can't read it."""
    path = icon_path(size)
    if not path.exists():
        return None
    try:
        return tk.PhotoImage(master=master, file=str(path))
    except tk.TclError:
        return None
