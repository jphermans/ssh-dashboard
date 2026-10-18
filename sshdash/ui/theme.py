"""Colour palette, typography and ttk styling.

Two palettes ship with the app. Both avoid pure black/white surfaces: the dark
theme is a neutral slate, the light theme a soft grey-blue.
"""

from __future__ import annotations

import tkinter as tk
from dataclasses import dataclass
from tkinter import font as tkfont
from tkinter import ttk
from typing import Iterable, Optional


@dataclass(frozen=True)
class Palette:
    key: str
    name: str
    bg: str
    sidebar: str
    panel: str
    panel_alt: str
    border: str
    border_strong: str
    text: str
    muted: str
    faint: str
    accent: str
    accent_hover: str
    accent_pressed: str
    accent_soft: str
    on_accent: str
    danger: str
    danger_hover: str
    danger_soft: str
    danger_text: str
    success: str
    success_soft: str
    warn: str
    warn_soft: str
    selection: str
    row_alt: str


DARK = Palette(
    key="dark",
    name="Dark",
    bg="#11171F",
    sidebar="#0C1219",
    panel="#161E28",
    panel_alt="#1B2531",
    border="#25313F",
    border_strong="#33455A",
    text="#E9EFF7",
    muted="#8C9DB2",
    faint="#5E7187",
    accent="#4C8DFF",
    accent_hover="#6BA0FF",
    accent_pressed="#3B7AE6",
    accent_soft="#1B2B45",
    on_accent="#FFFFFF",
    danger="#F0596B",
    danger_hover="#F57483",
    danger_soft="#3A1E26",
    danger_text="#FF9AA6",
    success="#3FCF8E",
    success_soft="#16342A",
    warn="#E6B450",
    warn_soft="#362C1A",
    selection="#20324C",
    row_alt="#18212C",
)

LIGHT = Palette(
    key="light",
    name="Light",
    bg="#EBF0F7",
    sidebar="#F5F8FC",
    panel="#FFFFFF",
    panel_alt="#F1F6FB",
    border="#D5E0EC",
    border_strong="#B9C9DA",
    text="#111B27",
    muted="#5D7288",
    faint="#8496A8",
    accent="#2563EB",
    accent_hover="#3B78F0",
    accent_pressed="#1D4FD7",
    accent_soft="#DCE9FB",
    on_accent="#FFFFFF",
    danger="#D93A4B",
    danger_hover="#C22F3F",
    danger_soft="#FBE3E6",
    danger_text="#B02A39",
    success="#0F9D63",
    success_soft="#DDF3E9",
    warn="#A8730A",
    warn_soft="#FBF0D9",
    selection="#DCE9FB",
    row_alt="#F7FAFD",
)

PALETTES = {"dark": DARK, "light": LIGHT}


def palette_for(name: str) -> Palette:
    return PALETTES.get(name, DARK)


@dataclass(frozen=True)
class Fonts:
    ui: str
    mono: str
    body: tuple
    body_bold: tuple
    small: tuple
    small_bold: tuple
    tiny: tuple
    title: tuple
    heading: tuple
    brand: tuple
    mono_body: tuple
    mono_small: tuple
    table: tuple


def _pick(root: tk.Misc, candidates: Iterable[str], fallback: str) -> str:
    available = set(tkfont.families(root))
    for candidate in candidates:
        if candidate in available:
            return candidate
    return fallback


def load_fonts(root: tk.Misc) -> Fonts:
    ui = _pick(
        root,
        ("SF Pro Text", "SF Pro Display", "Helvetica Neue", "Segoe UI", "Inter", "Arial"),
        "Helvetica",
    )
    mono = _pick(
        root,
        ("SF Mono", "Menlo", "JetBrains Mono", "Cascadia Mono", "Consolas", "DejaVu Sans Mono"),
        "Courier",
    )
    return Fonts(
        ui=ui,
        mono=mono,
        body=(ui, 13),
        body_bold=(ui, 13, "bold"),
        small=(ui, 11),
        small_bold=(ui, 11, "bold"),
        tiny=(ui, 10),
        title=(ui, 22, "bold"),
        heading=(ui, 15, "bold"),
        brand=(ui, 14, "bold"),
        mono_body=(mono, 12),
        mono_small=(mono, 11),
        table=(ui, 12),
    )


def apply_ttk_styles(root: tk.Misc, palette: Palette, fonts: Fonts) -> None:
    """Restyle ttk's table and scrollbar to match the palette."""
    style = ttk.Style(root)
    try:
        style.theme_use("clam")
    except tk.TclError:
        pass

    style.configure(
        "SSH.Treeview",
        background=palette.panel,
        fieldbackground=palette.panel,
        foreground=palette.text,
        rowheight=34,
        borderwidth=0,
        relief="flat",
        font=fonts.table,
    )
    style.map(
        "SSH.Treeview",
        background=[("selected", palette.selection)],
        foreground=[("selected", palette.text)],
    )
    style.configure(
        "SSH.Treeview.Heading",
        background=palette.panel_alt,
        foreground=palette.muted,
        relief="flat",
        borderwidth=0,
        padding=(10, 9),
        font=fonts.small_bold,
    )
    style.map(
        "SSH.Treeview.Heading",
        background=[("active", palette.panel_alt), ("pressed", palette.panel_alt)],
        foreground=[("active", palette.text)],
    )
    style.layout("SSH.Treeview", [("SSH.Treeview.treearea", {"sticky": "nswe"})])

    style.configure(
        "SSH.Vertical.TScrollbar",
        background=palette.border,
        troughcolor=palette.panel,
        bordercolor=palette.panel,
        darkcolor=palette.border,
        lightcolor=palette.border,
        arrowcolor=palette.muted,
        arrowsize=10,
        width=11,
        relief="flat",
    )
    style.map(
        "SSH.Vertical.TScrollbar",
        background=[("active", palette.border_strong), ("pressed", palette.accent)],
    )
    style.layout(
        "SSH.Vertical.TScrollbar",
        [("Vertical.Scrollbar.trough", {"sticky": "ns"}), ("Vertical.Scrollbar.thumb", {"expand": "1", "sticky": "nswe"})],
    )
    style.configure(
        "SSH.Horizontal.TScrollbar",
        background=palette.border,
        troughcolor=palette.panel,
        bordercolor=palette.panel,
        darkcolor=palette.border,
        lightcolor=palette.border,
        arrowcolor=palette.muted,
        arrowsize=10,
        width=11,
        relief="flat",
    )
    style.map(
        "SSH.Horizontal.TScrollbar",
        background=[("active", palette.border_strong), ("pressed", palette.accent)],
    )
    style.layout(
        "SSH.Horizontal.TScrollbar",
        [("Horizontal.Scrollbar.trough", {"sticky": "ew"}), ("Horizontal.Scrollbar.thumb", {"expand": "1", "sticky": "nswe"})],
    )
