"""The dashboard window.

Layout is built with pack/grid only — no third-party widget libraries — so the
app runs on a stock Python with Tk. Nothing here writes to ``~/.ssh`` except
through :class:`sshdash.store.SshStore`, which owns backups and atomic saves.
"""

from __future__ import annotations

import ipaddress
import os
import socket
import subprocess
import sys
import threading
import time
import tkinter as tk
from pathlib import Path
from tkinter import ttk
from typing import Callable, Dict, List, Optional, Sequence, Tuple

from .. import __app_name__, __version__
from ..config import ActivityLog, Settings, human_time, support_dir
from ..model import ConfigBlock, HostView, KeyInfo, Snapshot
from ..store import SshStore, StoreError
from .branding import load_icon
from .theme import Fonts, Palette, apply_ttk_styles, load_fonts, palette_for
from .widgets import (
    CanvasButton,
    Card,
    ConfigBlockDialog,
    ConfirmDeleteDialog,
    Divider,
    MessageDialog,
    Pill,
    SearchField,
    Toast,
    mix,
    round_rect,
)

VIEWS: Dict[str, Dict[str, str]] = {
    "hosts": {
        "title": "Known hosts",
        "placeholder": "Search hosts or users\u2026",
        "empty": "Tick entries with the first column, then remove them in one go.",
    },
    "config": {
        "title": "Configuration",
        "placeholder": "Search host blocks\u2026",
        "empty": "Every Host block in ~/.ssh/config, including global defaults.",
    },
    "keys": {
        "title": "Keys",
        "placeholder": "Search keys\u2026",
        "empty": "Key pairs found in ~/.ssh. Keys are never modified here.",
    },
    "activity": {
        "title": "Activity",
        "placeholder": "Search activity\u2026",
        "empty": "Nothing has been removed yet. Every change lands here.",
    },
}

NAV_ORDER = ("hosts", "config", "keys", "activity")
NAV_LABELS = {
    "hosts": "Known hosts",
    "config": "Configuration",
    "keys": "Keys",
    "activity": "Activity",
}
NAV_ICONS = {"hosts": "stack", "config": "sliders", "keys": "key", "activity": "pulse"}

COLUMN_SETS: Dict[str, Tuple[Tuple[str, str, int, int, bool], ...]] = {
    "hosts": (
        ("select", "\u2713", 34, 34, False),
        ("label", "Host", 170, 120, True),
        ("address", "Address", 210, 120, True),
        ("user", "User", 84, 70, True),
        ("port", "Port", 50, 48, False),
        ("keys", "Key types", 132, 100, True),
        ("source", "Source", 118, 96, False),
    ),
    "config": (
        ("select", "\u2713", 34, 34, False),
        ("label", "Alias", 118, 100, True),
        ("hostname", "HostName", 200, 120, True),
        ("user", "User", 72, 60, False),
        ("port", "Port", 54, 48, False),
        ("identity", "Identity file", 190, 120, True),
        ("lines", "Lines", 66, 54, False),
        ("state", "Type", 68, 62, False),
    ),
    "keys": (
        ("name", "Key", 130, 100, True),
        ("type", "Type", 100, 80, False),
        ("fingerprint", "Fingerprint", 186, 120, True),
        ("comment", "Comment", 124, 80, True),
        ("status", "Passphrase", 100, 86, False),
        ("permissions", "Mode", 52, 46, False),
    ),
    "activity": (
        ("time", "When", 122, 110, False),
        ("action", "Action", 110, 90, False),
        ("detail", "Detail", 420, 200, True),
    ),
}

ACTION_LABELS = {
    "delete": "Removed hosts",
    "delete-config": "Removed blocks",
    "edit-config": "Edited block",
    "restore": "Restored backup",
}


def is_local_target(host: str) -> bool:
    """True for RFC1918 / link-local / loopback addresses and mDNS names."""
    name = host.strip().lower()
    if name in ("localhost", "::1") or name.endswith(".local"):
        return True
    try:
        address = ipaddress.ip_address(name)
    except ValueError:
        return False
    return address.is_private or address.is_link_local or address.is_loopback


def key_family(key_type: str) -> str:
    text = key_type.lower()
    for family in ("ed25519", "rsa", "ecdsa", "dsa"):
        if family in text:
            return family
    return text.replace("ssh-", "") or "key"


def _plural(count: int, word: str) -> str:
    return f"{count} {word}{'' if count == 1 else 's'}"


# ---------------------------------------------------------------------------
# Navigation item
# ---------------------------------------------------------------------------

class NavItem(tk.Frame):
    """Sidebar row with a hand-drawn icon, hover state and active marker."""

    def __init__(
        self,
        master: tk.Misc,
        key: str,
        label: str,
        icon: str,
        *,
        palette: Palette,
        fonts: Fonts,
        command: Callable[[], None],
        count: str = "",
    ) -> None:
        super().__init__(master, bg=palette.sidebar, height=40, cursor="hand2")
        self.pack_propagate(False)
        self.palette = palette
        self.fonts = fonts
        self.icon_kind = icon
        self.command = command
        self.active = False
        self._hover = False

        self.indicator = tk.Frame(self, bg=palette.sidebar, width=3)
        self.indicator.pack(side="left", fill="y")
        self.icon = tk.Canvas(self, width=20, height=20, bg=palette.sidebar, highlightthickness=0)
        self.icon.pack(side="left", padx=(11, 10))
        self.label = tk.Label(
            self, text=label, bg=palette.sidebar, fg=palette.muted,
            font=fonts.body, anchor="w", cursor="hand2",
        )
        self.label.pack(side="left", fill="x", expand=True)
        self.count = tk.Label(
            self, text=count, bg=palette.sidebar, fg=palette.faint,
            font=fonts.tiny, anchor="e", cursor="hand2",
        )
        self.count.pack(side="right", padx=(4, 12))

        for widget in (self, self.label, self.count, self.icon):
            widget.bind("<Enter>", self._enter)
            widget.bind("<Leave>", self._leave)
            widget.bind("<Button-1>", self._click)
        self._paint()

    def _click(self, _event: tk.Event) -> None:
        self.command()

    def _enter(self, _event: tk.Event) -> None:
        self._hover = True
        self._paint()

    def _leave(self, _event: tk.Event) -> None:
        self._hover = False
        self._paint()

    def set_active(self, active: bool) -> None:
        self.active = active
        self._paint()

    def set_count(self, text: str) -> None:
        self.count.configure(text=text)

    def _paint(self) -> None:
        p = self.palette
        if self.active:
            bg, fg = p.accent_soft, p.accent
        elif self._hover:
            bg, fg = p.panel_alt, p.text
        else:
            bg, fg = p.sidebar, p.muted
        for widget in (self, self.label, self.count, self.icon):
            widget.configure(bg=bg)
        self.label.configure(fg=fg, font=self.fonts.body_bold if self.active else self.fonts.body)
        self.count.configure(fg=fg if self.active else p.faint)
        self.indicator.configure(bg=p.accent if self.active else bg)
        self._draw_icon(fg)

    def _draw_icon(self, color: str) -> None:
        canvas = self.icon
        canvas.delete("all")
        width = 1.5
        if self.icon_kind == "stack":
            for index, y in enumerate((3, 8, 13)):
                canvas.create_rectangle(2, y, 18, y + 4.5, outline=color, width=width)
                canvas.create_line(5, y + 2.2, 8, y + 2.2, fill=color, width=width)
        elif self.icon_kind == "sliders":
            for y, knob in ((4.5, 7), (10, 13), (15.5, 5)):
                canvas.create_line(2, y, 18, y, fill=color, width=width)
                canvas.create_oval(knob - 2.4, y - 2.4, knob + 2.4, y + 2.4, fill=color, outline=color)
        elif self.icon_kind == "key":
            canvas.create_oval(2.5, 5.5, 9.5, 12.5, outline=color, width=width)
            canvas.create_line(9, 10, 16.5, 10, fill=color, width=width)
            canvas.create_line(13, 10, 13, 13, fill=color, width=width)
            canvas.create_line(16, 10, 16, 12.5, fill=color, width=width)
        else:  # pulse
            canvas.create_line(
                2, 10, 5, 10, 7.5, 4, 10.5, 15, 13, 8, 18, 8,
                fill=color, width=width, smooth=True, splinesteps=12,
            )


# ---------------------------------------------------------------------------
# Application
# ---------------------------------------------------------------------------

class DashboardApp(tk.Tk):
    def __init__(self) -> None:
        super().__init__()
        self.settings = Settings.load()
        self.palette = palette_for(self.settings.theme)
        self.fonts = load_fonts(self)
        self._icon = load_icon(self, 512)
        self._about_icon = load_icon(self, 128)
        self.store = SshStore()
        self.log = ActivityLog()
        self.snapshot: Snapshot = self.store.snapshot()
        self.view = self.settings.last_view if self.settings.last_view in VIEWS else "hosts"
        self.query = ""
        self.selected: Dict[str, set] = {"hosts": set(), "config": set()}
        self.sort: Dict[str, Tuple[str, bool]] = {
            "hosts": ("label", False),
            "config": ("label", False),
            "keys": ("name", False),
            "activity": ("time", True),
        }
        self.rows: List[dict] = []
        self._row_meta: Dict[str, dict] = {}
        self._row_lookup: Dict[str, dict] = {}
        self.detail_key: Optional[str] = None
        self.nav_items: Dict[str, NavItem] = {}
        self._test_token = 0
        self._test_label: Optional[tk.Label] = None
        self._last_backups: Tuple[Path, ...] = ()
        self._view_var = tk.StringVar(value=self.view)
        self._theme_var = tk.StringVar(value=self.settings.theme)
        self._modifier = "Command" if sys.platform == "darwin" else "Control"

        self.title(__app_name__)
        if self._icon is not None:
            try:
                self.iconphoto(True, self._icon)
                self.iconname(__app_name__)
            except tk.TclError:
                pass
        if os.name == "nt":
            from .branding import asset

            ico = asset("SSHDashboard.ico")
            if ico.exists():
                try:
                    self.iconbitmap(default=str(ico))
                except tk.TclError:
                    pass
        self.minsize(1080, 660)
        self.configure(bg=self.palette.bg)
        self._place_window()
        apply_ttk_styles(self, self.palette, self.fonts)
        self._build_ui()
        self._bind_keys()
        self._switch_view(self.view, initial=True)
        self.protocol("WM_DELETE_WINDOW", self._on_close)

    # ------------------------------------------------------------------
    # Window
    # ------------------------------------------------------------------
    def _place_window(self) -> None:
        saved = self.settings.window.strip()
        if saved and "x" in saved:
            try:
                width, height = saved.split("+")[0].split("x")
                if int(width) >= 1000 and int(height) >= 640:
                    self.geometry(saved)
                    return
            except ValueError:
                pass
        screen_w, screen_h = self.winfo_screenwidth(), self.winfo_screenheight()
        width = max(1040, min(1500, screen_w - 80))
        height = max(660, min(900, screen_h - 120))
        x = max(0, (screen_w - width) // 2)
        y = max(0, (screen_h - height) // 2 - 20)
        self.geometry(f"{width}x{height}+{x}+{y}")

    def _on_close(self) -> None:
        try:
            self.settings.window = self.geometry()
        except tk.TclError:
            pass
        self.settings.last_view = self.view
        self.settings.save()
        self.destroy()

    # ------------------------------------------------------------------
    # Layout
    # ------------------------------------------------------------------
    def _build_ui(self) -> None:
        for child in self.winfo_children():
            child.destroy()
        self.configure(bg=self.palette.bg)
        self.nav_items = {}
        self._build_sidebar()

        main = tk.Frame(self, bg=self.palette.bg)
        main.pack(side="left", fill="both", expand=True)
        self._build_header(main)
        Divider(main, palette=self.palette).pack(fill="x")
        self.banner_host = tk.Frame(main, bg=self.palette.bg)
        self.banner_host.pack(fill="x", padx=28, pady=(12, 0))
        self._update_warnings()

        self._build_statusbar(main)
        self._build_body(main)
        self.toast = Toast(self, palette=self.palette, fonts=self.fonts)
        self._build_menubar()

    def _build_sidebar(self) -> None:
        p, f = self.palette, self.fonts
        sidebar = tk.Frame(self, bg=p.sidebar, width=238)
        sidebar.pack(side="left", fill="y")
        sidebar.pack_propagate(False)

        brand = tk.Frame(sidebar, bg=p.sidebar)
        brand.pack(fill="x", padx=20, pady=(24, 26))
        mark = tk.Canvas(brand, width=36, height=36, bg=p.sidebar, highlightthickness=0)
        mark.pack(side="left")
        round_rect(mark, 1, 1, 35, 35, 10, fill=p.accent, outline="")
        mark.create_text(18, 19, text="\u203a_", fill=p.on_accent, font=(f.mono, 13, "bold"))
        titles = tk.Frame(brand, bg=p.sidebar)
        titles.pack(side="left", padx=(10, 0))
        tk.Label(
            titles, text="SSH Dashboard", bg=p.sidebar, fg=p.text, font=f.brand, anchor="w",
        ).pack(anchor="w")
        tk.Label(
            titles, text="HOST & KEY MANAGER", bg=p.sidebar, fg=p.faint,
            font=(f.ui, 8, "bold"), anchor="w",
        ).pack(anchor="w", pady=(2, 0))

        tk.Label(
            sidebar, text="WORKSPACE", bg=p.sidebar, fg=p.faint,
            font=(f.ui, 9, "bold"), anchor="w",
        ).pack(fill="x", padx=24, pady=(0, 8))

        nav = tk.Frame(sidebar, bg=p.sidebar)
        nav.pack(fill="x", padx=12)
        counts = {
            "hosts": str(len(self.snapshot.hosts)),
            "config": str(self.snapshot.config_count),
            "keys": str(len(self.snapshot.keys)),
            "activity": "",
        }
        for key in NAV_ORDER:
            item = NavItem(
                nav, key, NAV_LABELS[key], NAV_ICONS[key],
                palette=p, fonts=f, command=lambda k=key: self._switch_view(k),
                count=counts[key],
            )
            item.pack(fill="x", pady=1)
            self.nav_items[key] = item

        footer = tk.Frame(sidebar, bg=p.sidebar)
        footer.pack(side="bottom", fill="x", padx=16, pady=(12, 16))

        dir_card = tk.Frame(
            footer, bg=p.panel, highlightbackground=p.border, highlightthickness=1,
        )
        dir_card.pack(fill="x", pady=(0, 10))
        tk.Label(
            dir_card, text="SSH DIRECTORY", bg=p.panel, fg=p.faint,
            font=(f.ui, 8, "bold"), anchor="w",
        ).pack(fill="x", padx=12, pady=(10, 0))
        tk.Label(
            dir_card, text=str(self.store.ssh_dir).replace(str(Path.home()), "~"),
            bg=p.panel, fg=p.text, font=f.mono_small, anchor="w",
        ).pack(fill="x", padx=12, pady=(1, 10))

        theme_label = "Light mode" if self.settings.theme == "dark" else "Dark mode"
        CanvasButton(
            footer, theme_label, command=self._toggle_theme,
            palette=p, fonts=f, kind="ghost", glyph="\u25d0", height=34, bg=p.sidebar,
        ).pack(fill="x")

        meta = tk.Frame(footer, bg=p.sidebar)
        meta.pack(fill="x", pady=(12, 0))
        version = tk.Label(
            meta, text=f"v{__version__}", bg=p.sidebar, fg=p.faint, font=f.tiny,
            cursor="hand2",
        )
        version.pack(side="left")
        version.bind("<Button-1>", lambda _e: self._about())
        about = tk.Label(
            meta, text="About \u00b7 Backups", bg=p.sidebar, fg=p.faint, font=f.tiny,
            cursor="hand2",
        )
        about.pack(side="right")
        about.bind("<Button-1>", lambda _e: self._about())

    def _build_header(self, parent: tk.Misc) -> None:
        p, f = self.palette, self.fonts
        header = tk.Frame(parent, bg=p.bg)
        header.pack(fill="x", padx=28, pady=(24, 16))
        title_block = tk.Frame(header, bg=p.bg)
        title_block.pack(side="left", fill="x", expand=True)
        self.title_label = tk.Label(
            title_block, text="", bg=p.bg, fg=p.text, font=f.title, anchor="w",
        )
        self.title_label.pack(anchor="w")
        self.subtitle_label = tk.Label(
            title_block, text="", bg=p.bg, fg=p.muted, font=f.small, anchor="w",
        )
        self.subtitle_label.pack(anchor="w", pady=(4, 0))

        actions = tk.Frame(header, bg=p.bg)
        actions.pack(side="right", anchor="n")
        self.search = SearchField(
            actions, palette=p, fonts=f, width=300, height=36,
            on_change=self._on_search, placeholder=VIEWS[self.view]["placeholder"],
        )
        self.search.pack(side="left", padx=(0, 8))
        self.refresh_btn = CanvasButton(
            actions, "", command=self.refresh, glyph="\u21bb", kind="ghost",
            palette=p, fonts=f, width=38, height=36, radius=9,
        )
        self.refresh_btn.pack(side="left")
        self.menu_btn = CanvasButton(
            actions, "", command=self._show_overflow, glyph="\u22ef", kind="ghost",
            palette=p, fonts=f, width=38, height=36, radius=9,
        )
        self.menu_btn.pack(side="left", padx=(8, 0))

    def _update_warnings(self) -> None:
        p, f = self.palette, self.fonts
        for child in self.banner_host.winfo_children():
            child.destroy()
        warnings = list(self.snapshot.warnings)
        # The host frame stays packed so the pack order never changes.
        self.banner_host.pack_configure(pady=(12, 0) if warnings else (0, 0))
        if not warnings:
            return
        banner = tk.Frame(
            self.banner_host,
            bg=p.warn_soft,
            highlightbackground=mix(p.warn_soft, p.warn, 0.35),
            highlightthickness=1,
        )
        banner.pack(fill="x")
        tk.Label(
            banner, text="!", bg=p.warn_soft, fg=p.warn, font=f.small_bold,
            width=2,
        ).pack(side="left", padx=(10, 0), pady=8)
        tk.Label(
            banner,
            text=f"{len(warnings)} file issue{'s' if len(warnings) != 1 else ''} "
                 "found while scanning \u2014 some entries may be hidden",
            bg=p.warn_soft, fg=p.text, font=f.small, anchor="w",
        ).pack(side="left", fill="x", expand=True, pady=8)
        CanvasButton(
            banner, "Review", command=self._show_warnings, kind="link",
            palette=p, fonts=f, bg=p.warn_soft, height=24, padx=8, font=f.small_bold,
        ).pack(side="right", padx=(0, 10))

    def _build_statusbar(self, parent: tk.Misc) -> None:
        p, f = self.palette, self.fonts
        bar = tk.Frame(parent, bg=p.bg, height=34)
        bar.pack(side="bottom", fill="x", padx=28)
        bar.pack_propagate(False)
        Divider(bar, palette=p).pack(side="top", fill="x")
        self.status_left = tk.Label(
            bar, text="", bg=p.bg, fg=p.muted, font=f.small, anchor="w",
        )
        self.status_left.pack(side="left", pady=(8, 0))
        self.status_right = tk.Label(
            bar, text="", bg=p.bg, fg=p.faint, font=f.small, anchor="e",
        )
        self.status_right.pack(side="right", pady=(8, 0))

    def _build_body(self, parent: tk.Misc) -> None:
        p = self.palette
        body = tk.Frame(parent, bg=p.bg)
        body.pack(fill="both", expand=True, padx=24, pady=(8, 10))
        body.rowconfigure(0, weight=1)
        body.columnconfigure(0, weight=1)
        body.columnconfigure(1, minsize=296)

        self.card = Card(body, palette=p)
        self.card.grid(row=0, column=0, sticky="nsew")
        self.card.grid_rowconfigure(2, weight=1)
        self.card.grid_columnconfigure(0, weight=1)

        head = tk.Frame(self.card, bg=p.panel, height=46)
        head.grid(row=0, column=0, sticky="ew")
        head.grid_propagate(False)
        self.head_title = tk.Label(
            head, text="ENTRIES", bg=p.panel, fg=p.faint,
            font=(self.fonts.ui, 9, "bold"),
        )
        self.head_title.pack(side="left", padx=(16, 8))
        self.count_pill = Pill(head, "0", palette=p, fonts=self.fonts, tone="neutral")
        self.count_pill.pack(side="left")
        self.select_all_head = CanvasButton(
            head, "Select all", command=self._toggle_all, kind="link",
            palette=p, fonts=self.fonts, bg=p.panel, height=26, padx=6,
            font=self.fonts.small_bold,
        )
        self.select_all_head.pack(side="right", padx=(0, 10))
        Divider(self.card, palette=p).grid(row=1, column=0, sticky="ew")

        self.table_holder = tk.Frame(self.card, bg=p.panel)
        self.table_holder.grid(row=2, column=0, sticky="nsew")
        self.table_holder.grid_rowconfigure(0, weight=1)
        self.table_holder.grid_columnconfigure(0, weight=1)
        Divider(self.card, palette=p).grid(row=3, column=0, sticky="ew")

        foot = tk.Frame(self.card, bg=p.panel, height=52)
        foot.grid(row=4, column=0, sticky="ew")
        foot.grid_propagate(False)
        self.foot_label = tk.Label(
            foot, text="", bg=p.panel, fg=p.muted, font=self.fonts.small, anchor="w",
        )
        self.foot_label.pack(side="left", padx=16)
        self.foot_hint = tk.Label(
            foot, text="", bg=p.panel, fg=p.faint, font=self.fonts.small, anchor="w",
        )
        self.foot_hint.pack(side="left", padx=(10, 0))

        self.delete_btn = CanvasButton(
            foot, "Remove", command=self._delete_selected, kind="danger-soft",
            palette=p, fonts=self.fonts, height=32, enabled=False, bg=p.panel,
        )
        self.delete_btn.pack(side="right", padx=(0, 14))
        self.clear_btn = CanvasButton(
            foot, "Clear", command=self._clear_selection, kind="quiet",
            palette=p, fonts=self.fonts, height=30, padx=8, bg=p.panel,
        )
        self.clear_btn.pack(side="right", padx=(0, 4))

        self.rail = Card(body, palette=p)
        self.rail.grid(row=0, column=1, sticky="nsew", padx=(16, 0))
        self.rail.grid_rowconfigure(2, weight=1)
        self.rail.grid_columnconfigure(0, weight=1)
        rail_head = tk.Frame(self.rail, bg=p.panel, height=46)
        rail_head.grid(row=0, column=0, sticky="ew")
        rail_head.grid_propagate(False)
        tk.Label(
            rail_head, text="DETAILS", bg=p.panel, fg=p.faint,
            font=(self.fonts.ui, 9, "bold"),
        ).pack(side="left", padx=16)
        Divider(self.rail, palette=p).grid(row=1, column=0, sticky="ew")
        self.rail_body = tk.Frame(self.rail, bg=p.panel, padx=18, pady=16)
        self.rail_body.grid(row=2, column=0, sticky="nsew")

    # ------------------------------------------------------------------
    # Views and tables
    # ------------------------------------------------------------------
    def _switch_view(self, view: str, initial: bool = False) -> None:
        if view not in VIEWS:
            return
        if not initial:
            self.view = view
            self.settings.last_view = view
            self.settings.save()
        for key, item in self.nav_items.items():
            item.set_active(key == self.view)
        if hasattr(self, "_view_var"):
            self._view_var.set(self.view)
        self.title_label.configure(text=VIEWS[self.view]["title"])
        self.search.set_placeholder(VIEWS[self.view]["placeholder"])
        self.detail_key = None
        self._test_token += 1
        self._build_table()
        self._render_table()
        self._show_detail(None)
        self._update_header_text()
        self._update_footer()

    def _build_table(self) -> None:
        p, f = self.palette, self.fonts
        for child in self.table_holder.winfo_children():
            child.destroy()
        columns = COLUMN_SETS[self.view]
        tree = ttk.Treeview(
            self.table_holder,
            columns=[column[0] for column in columns],
            show="headings",
            style="SSH.Treeview",
            selectmode="browse",
        )
        for key, title, width, minwidth, stretch in columns:
            tree.heading(
                key, text=title, anchor="w",
                command=(lambda c=key: self._toggle_all()) if key == "select"
                else (lambda c=key: self._sort_by(c)),
            )
            tree.column(
                key, width=width, minwidth=minwidth, stretch=stretch, anchor="w",
            )
        tree.grid(row=0, column=0, sticky="nsew", padx=(1, 0), pady=1)
        scrollbar = ttk.Scrollbar(
            self.table_holder, orient="vertical", command=tree.yview,
            style="SSH.Vertical.TScrollbar",
        )
        scrollbar.grid(row=0, column=1, sticky="ns", pady=1)
        xscrollbar = ttk.Scrollbar(
            self.table_holder, orient="horizontal", command=tree.xview,
            style="SSH.Horizontal.TScrollbar",
        )
        xscrollbar.grid(row=1, column=0, sticky="ew", padx=(1, 0))
        tree.configure(yscrollcommand=scrollbar.set, xscrollcommand=xscrollbar.set)
        self._xscrollbar = xscrollbar
        tree.bind("<Configure>", lambda _e: self._sync_xscroll(), add="+")

        tree.tag_configure("alt", background=p.row_alt)
        tree.tag_configure("checked", background=p.selection)
        tree.tag_configure("configonly", foreground=p.muted)
        tree.tag_configure("readonly", foreground=p.muted)

        tree.bind("<<TreeviewSelect>>", self._on_row_select)
        tree.bind("<Button-1>", self._on_tree_click)
        tree.bind("<Double-1>", self._on_tree_double_click)
        tree.bind("<Button-2>", self._on_context_menu)
        tree.bind("<Button-3>", self._on_context_menu)
        tree.bind("<Control-Button-1>", self._on_context_menu)
        self.tree = tree
        self._empty_overlay = None
        self._column_titles = {column[0]: column[1] for column in columns}

    def _on_search(self, query: str) -> None:
        self.query = query
        self._render_table()

    def _sort_by(self, column: str) -> None:
        if column == "select":
            return
        current, descending = self.sort[self.view]
        if current == column:
            self.sort[self.view] = (column, not descending)
        else:
            self.sort[self.view] = (column, False)
        self._render_table()

    # -- row construction ----------------------------------------------
    def _host_rows(self) -> List[dict]:
        query = self.query.lower()
        rows: List[dict] = []
        for host in self.snapshot.hosts:
            key_text = " \u00b7 ".join(key_family(kind) for kind in host.key_types) or "\u2014"
            haystack = " ".join(
                (
                    host.label, host.address, host.user or "", str(host.port or ""),
                    " ".join(host.key_types), host.source_label,
                    " ".join(host.tokens), " ".join(host.fingerprints),
                )
            ).lower()
            if query and query not in haystack:
                continue
            rows.append(
                {
                    "iid": host.key,
                    "payload": host,
                    "configonly": not host.in_known_hosts,
                    "values": (
                        "\u2713" if host.key in self.selected["hosts"] else "",
                        host.label,
                        host.address,
                        host.user or "\u2014",
                        str(host.port or ""),
                        key_text,
                        host.source_label,
                    ),
                    "sort": {
                        "label": host.label.lower(),
                        "address": host.address.lower(),
                        "user": (host.user or "").lower(),
                        "port": host.port or 0,
                        "keys": key_text.lower(),
                        "source": host.source_label.lower(),
                    },
                }
            )
        return rows

    def _config_rows(self) -> List[dict]:
        query = self.query.lower()
        rows: List[dict] = []
        for block in self.snapshot.config_blocks:
            if block.is_global:
                state = "global"
            elif not block.editable:
                state = "included"
            elif not block.deletable:
                state = "pattern"
            else:
                state = "editable"
            hostname = block.option("hostname") or "\u2014"
            user = block.option("user") or "\u2014"
            identity = block.option("identityfile") or "\u2014"
            block_id = f"block:{block.source}:{block.start_line}"
            haystack = " ".join(
                (block.label, hostname, user, identity, state, " ".join(block.patterns))
            ).lower()
            if query and query not in haystack:
                continue
            rows.append(
                {
                    "iid": block_id,
                    "payload": block,
                    "readonly": state != "editable",
                    "values": (
                        "\u2713" if block_id in self.selected["config"] else "",
                        block.label,
                        hostname,
                        user,
                        str(block.option("port") or ""),
                        identity,
                        f"{block.start_line}\u2013{block.end_line}",
                        state,
                    ),
                    "sort": {
                        "label": block.label.lower(),
                        "hostname": hostname.lower(),
                        "user": user.lower(),
                        "port": int(block.option("port") or 0) if (block.option("port") or "").isdigit() else 0,
                        "identity": identity.lower(),
                        "lines": block.start_line,
                        "state": state,
                    },
                }
            )
        return rows

    def _key_rows(self) -> List[dict]:
        query = self.query.lower()
        rows: List[dict] = []
        for key in self.snapshot.keys:
            haystack = " ".join(
                (key.name, key.key_type, key.bits, key.fingerprint, key.comment, key.status)
            ).lower()
            if query and query not in haystack:
                continue
            rows.append(
                {
                    "iid": key.name,
                    "payload": key,
                    "values": (
                        key.name,
                        key.display_type,
                        key.fingerprint or "\u2014",
                        key.comment or "\u2014",
                        key.status,
                        key.permissions,
                    ),
                    "sort": {
                        "name": key.name.lower(),
                        "type": key.key_type.lower(),
                        "fingerprint": key.fingerprint.lower(),
                        "comment": key.comment.lower(),
                        "status": key.status.lower(),
                        "permissions": key.permissions,
                    },
                }
            )
        return rows

    def _activity_rows(self) -> List[dict]:
        query = self.query.lower()
        rows: List[dict] = []
        for index, entry in enumerate(self.log.entries(300)):
            action = ACTION_LABELS.get(entry.get("action", ""), entry.get("action", ""))
            detail = entry.get("detail", "")
            when = float(entry.get("ts", 0))
            haystack = f"{action} {detail}".lower()
            if query and query not in haystack:
                continue
            rows.append(
                {
                    "iid": f"activity-{when}-{index}",
                    "payload": entry,
                    "values": (human_time(when), action, detail),
                    "sort": {"time": when, "action": action.lower(), "detail": detail.lower()},
                }
            )
        return rows

    def _sorted_rows(self, rows: List[dict]) -> List[dict]:
        column, descending = self.sort[self.view]
        if not column:
            return rows
        return sorted(rows, key=lambda row: row["sort"].get(column, ""), reverse=descending)

    def _render_table(self) -> None:
        tree = getattr(self, "tree", None)
        if tree is None:
            return
        children = tree.get_children()
        if children:
            tree.delete(*children)
        self._row_meta = {}
        self._row_lookup = {}
        builder = {
            "hosts": self._host_rows,
            "config": self._config_rows,
            "keys": self._key_rows,
            "activity": self._activity_rows,
        }[self.view]
        self.rows = self._sorted_rows(builder())
        for index, row in enumerate(self.rows):
            iid = row["iid"]
            self._row_lookup[iid] = row
            self._row_meta[iid] = {
                "alt": index % 2 == 1,
                "configonly": row.get("configonly", False),
                "readonly": row.get("readonly", False),
            }
            tree.insert("", "end", iid=iid, values=row["values"], tags=self._tags_for(iid))
        self._update_headings()
        self._update_footer()
        self._update_status()
        self._update_header_text()
        self._update_empty_state()
        self._sync_xscroll()
        self._sync_menu()
        if self.detail_key and self.detail_key in self._row_lookup:
            tree.selection_set(self.detail_key)
            tree.focus(self.detail_key)
        elif self.detail_key:
            self.detail_key = None
            self._show_detail(None)

    def _tags_for(self, iid: str) -> Tuple[str, ...]:
        meta = self._row_meta.get(iid, {})
        tags: List[str] = []
        if iid in self._selected_keys():
            tags.append("checked")
        if meta.get("alt"):
            tags.append("alt")
        if meta.get("configonly"):
            tags.append("configonly")
        if meta.get("readonly"):
            tags.append("readonly")
        return tuple(tags)

    def _selected_keys(self) -> set:
        return self.selected.get(self.view, set())

    def _sync_xscroll(self) -> None:
        """Show the horizontal scrollbar only when the columns do not fit."""
        tree = getattr(self, "tree", None)
        scrollbar = getattr(self, "_xscrollbar", None)
        if tree is None or scrollbar is None:
            return
        try:
            total = sum(int(tree.column(column, "width")) for column in tree["columns"])
            # Stretch makes the columns fill the viewport exactly; allow a hair.
            fits = total <= tree.winfo_width() + 4
            if fits:
                scrollbar.grid_remove()
            elif not scrollbar.winfo_ismapped():
                scrollbar.grid()
        except tk.TclError:
            pass

    def _update_headings(self) -> None:
        tree = getattr(self, "tree", None)
        if tree is None:
            return
        column, descending = self.sort[self.view]
        arrow = "\u2193" if descending else "\u2191"
        for key, base in self._column_titles.items():
            text = base
            if key == column and key != "select":
                text = base + "  " + arrow
            tree.heading(key, text=text)
        visible = [row["iid"] for row in self.rows]
        selected = self._selected_keys()
        all_selected = bool(visible) and all(iid in selected for iid in visible)
        if "select" in self._column_titles:
            tree.heading("select", text="\u2713" if all_selected else "\u25cb")
        self.count_pill.set(str(len(self.rows)))

    def _update_footer(self) -> None:
        can_select = self.view in ("hosts", "config")
        selected = self._selected_keys()
        count = len(selected)
        visible = len(self.rows)
        if not can_select:
            self.delete_btn.pack_forget()
            self.clear_btn.pack_forget()
            self.foot_label.configure(
                text="Keys are read-only in this dashboard" if self.view == "keys"
                else "Newest change first \u00b7 journal stored locally",
                fg=self.palette.muted,
            )
            self.foot_hint.configure(text="")
            self.select_all_head.pack_forget()
            return
        if not self.select_all_head.winfo_ismapped():
            self.select_all_head.pack(side="right", padx=(0, 10))
        if not self.delete_btn.winfo_ismapped():
            self.delete_btn.pack(side="right", padx=(0, 14))
        if not self.clear_btn.winfo_ismapped():
            self.clear_btn.pack(side="right", padx=(0, 4))

        if self.view == "hosts":
            noun = _plural(count, "host")
        else:
            noun = _plural(count, "block")
        if count:
            self.foot_label.configure(text=f"{noun} selected", fg=self.palette.accent)
            self.foot_hint.configure(
                text="A backup is written first", fg=self.palette.faint,
            )
        else:
            self.foot_label.configure(text=f"{visible} entr{'y' if visible == 1 else 'ies'}", fg=self.palette.muted)
            self.foot_hint.configure(text=VIEWS[self.view]["empty"], fg=self.palette.faint)
        self.delete_btn.set_text(f"Remove {noun}" if count else "Remove")
        self.delete_btn.set_enabled(count > 0)
        self.clear_btn.set_enabled(count > 0)
        self.clear_btn.set_text("Clear")

    def _update_header_text(self) -> None:
        snapshot = self.snapshot
        if self.view == "hosts":
            self.subtitle_label.configure(
                text=f"{snapshot.known_host_count} known_hosts entries"
                     f"  \u00b7  {_plural(snapshot.config_count, 'ssh_config block')}"
                     f"  \u00b7  {_plural(len(snapshot.keys), 'key')}"
            )
        elif self.view == "config":
            self.subtitle_label.configure(
                text=f"{_plural(snapshot.config_count, 'host block')} in ~/.ssh/config  \u00b7  "
                     f"{len(snapshot.config_blocks)} blocks parsed"
            )
        elif self.view == "keys":
            self.subtitle_label.configure(
                text=f"{_plural(len(snapshot.keys), 'key pair')} in ~/.ssh  \u00b7  fingerprints are SHA-256"
            )
        else:
            self.subtitle_label.configure(
                text="Local journal of every change made by this app"
            )

    def _update_status(self) -> None:
        snapshot = self.snapshot
        self.status_left.configure(
            text=f"~/.ssh  \u00b7  {snapshot.known_host_count} hosts  \u00b7  "
                 f"{snapshot.config_count} config blocks  \u00b7  {len(snapshot.keys)} keys"
        )
        mod = "\u2318" if self._modifier == "Command" else "Ctrl+"
        self.status_right.configure(
            text=f"Scanned {snapshot.scanned_at.strftime('%H:%M:%S')}   \u00b7   "
                 f"{mod}R rescan   \u00b7   {mod}F search"
        )

    def _update_empty_state(self) -> None:
        p, f = self.palette, self.fonts
        overlay = getattr(self, "_empty_overlay", None)
        if overlay is not None:
            try:
                if overlay.winfo_exists():
                    overlay.destroy()
            except tk.TclError:
                pass
            self._empty_overlay = None
        if self.rows:
            return
        overlay = tk.Frame(self.table_holder, bg=p.panel)
        overlay.place(relx=0.5, rely=0.5, anchor="center")
        tk.Label(
            overlay, text="\u2315", bg=p.panel, fg=p.faint, font=(f.ui, 26),
        ).pack()
        heading = "No matches" if self.query else "Nothing here yet"
        tk.Label(
            overlay, text=heading, bg=p.panel, fg=p.text, font=f.heading,
        ).pack(pady=(6, 2))
        tk.Label(
            overlay,
            text=f"No results for \u201c{self.query}\u201d" if self.query else VIEWS[self.view]["empty"],
            bg=p.panel, fg=p.muted, font=f.small, wraplength=360, justify="center",
        ).pack()
        if self.query:
            CanvasButton(
                overlay, "Clear search", command=self._clear_search, kind="link",
                palette=p, fonts=f, bg=p.panel, height=26, padx=8, font=f.small_bold,
            ).pack(pady=(8, 0))
        self._empty_overlay = overlay

    # ------------------------------------------------------------------
    # Selection and table events
    # ------------------------------------------------------------------
    def _on_tree_click(self, event: tk.Event) -> None:
        tree = self.tree
        if tree.identify_region(event.x, event.y) != "cell":
            return
        column = tree.identify_column(event.x)
        iid = tree.identify_row(event.y)
        if not iid or column != "#1":
            return
        if self.view not in ("hosts", "config"):
            return
        row = self._row_lookup.get(iid)
        if row is None:
            return
        block = row["payload"].block if self.view == "hosts" else row["payload"]
        editable = self.view == "hosts" or (block is not None and block.deletable)
        if not editable:
            self.toast.show(
                "This block is read-only: it is global, an Include or a pattern",
                tone="info",
            )
            return
        self._toggle_row(iid)

    def _toggle_row(self, iid: str) -> None:
        if self.view not in self.selected:
            return
        selection = self.selected[self.view]
        if iid in selection:
            selection.discard(iid)
        else:
            selection.add(iid)
        tree = self.tree
        values = list(tree.item(iid, "values"))
        if values:
            values[0] = "\u2713" if iid in selection else ""
            tree.item(iid, values=values, tags=self._tags_for(iid))
        self._update_headings()
        self._update_footer()
        self._sync_menu()

    def _toggle_all(self) -> None:
        if self.view not in self.selected:
            return
        visible = [row["iid"] for row in self.rows if self._selectable(row)]
        selection = self.selected[self.view]
        if visible and all(iid in selection for iid in visible):
            selection.difference_update(visible)
        else:
            selection.update(visible)
        self._render_table()

    def _selectable(self, row: dict) -> bool:
        if self.view == "hosts":
            return True
        if self.view == "config":
            return row["payload"].deletable
        return False

    def _clear_selection(self) -> None:
        if self.view in self.selected:
            self.selected[self.view].clear()
        self._render_table()

    def _clear_search(self) -> None:
        self.search.set("")
        self.query = ""
        self._render_table()

    def _on_row_select(self, _event: Optional[tk.Event] = None) -> None:
        tree = self.tree
        selection = tree.selection()
        if not selection:
            self.detail_key = None
            self._show_detail(None)
            return
        iid = selection[0]
        self.detail_key = iid
        row = self._row_lookup.get(iid)
        self._show_detail(row)

    def _on_tree_double_click(self, event: tk.Event) -> None:
        if self.view != "config":
            return
        iid = self.tree.identify_row(event.y)
        row = self._row_lookup.get(iid) if iid else None
        if row is None:
            return
        block: ConfigBlock = row["payload"]
        if self._block_editable(block):
            self._open_config_editor(block)
        else:
            self.toast.show(
                "Only literal Host blocks in ~/.ssh/config can be edited here",
                tone="info",
            )

    def _block_editable(self, block: ConfigBlock) -> bool:
        return block.editable and block.kind == "host" and not block.is_global

    def _open_config_editor(self, block: ConfigBlock) -> None:
        if not self._block_editable(block):
            self.toast.show("This block is read-only here", tone="info")
            return
        dialog = ConfigBlockDialog(
            self, palette=self.palette, fonts=self.fonts,
            block=block, on_save=self._save_config_edit,
        )
        dialog.present()

    def _save_config_edit(
        self,
        block: ConfigBlock,
        patterns: Sequence[str],
        values: Dict[str, str],
    ) -> Optional[str]:
        try:
            backup = self.store.update_config_block(block, patterns, values)
        except StoreError as exc:
            return str(exc)
        self._last_backups = (backup,)
        self.refresh()
        target = f"block:{block.source}:{block.start_line}"
        if target in self._row_lookup:
            self.detail_key = target
            self.tree.selection_set(target)
            self.tree.focus(target)
            self._show_detail(self._row_lookup[target])
        self.toast.show(
            f"Saved {', '.join(patterns)} \u00b7 backup created",
            tone="success",
            action_label="Undo",
            action=lambda: self._undo((backup,)),
            timeout_ms=12000,
        )
        return None

    def _on_context_menu(self, event: tk.Event) -> None:
        p = self.palette
        iid = self.tree.identify_row(event.y)
        if iid:
            self.tree.selection_set(iid)
            self.tree.focus(iid)
        menu = tk.Menu(
            self, tearoff=0, bg=p.panel, fg=p.text, bd=0, relief="flat",
            activebackground=p.accent_soft, activeforeground=p.accent,
            font=self.fonts.body,
        )
        row = self._row_lookup.get(iid) if iid else None
        if self.view == "config" and row is not None and self._block_editable(row["payload"]):
            menu.add_command(
                label="Edit block\u2026",
                command=lambda: self._open_config_editor(row["payload"]),
            )
        if self.view in ("hosts", "config") and self._selected_keys():
            menu.add_command(label="Remove selected\u2026", command=self._delete_selected)
        if row is not None and self.view == "hosts":
            host: HostView = row["payload"]
            menu.add_command(
                label="Copy ssh command", command=lambda: self._copy(host.ssh_command, "SSH command"),
            )
            menu.add_command(label="Test connection", command=lambda: self._test_connection(host))
        if row is not None and self.view == "keys":
            key: KeyInfo = row["payload"]
            menu.add_command(
                label="Copy fingerprint", command=lambda: self._copy(key.fingerprint, "Fingerprint"),
            )
            menu.add_command(label="Reveal in Finder", command=lambda: self._reveal(key.path))
        if menu.index("end") is not None:
            try:
                menu.tk_popup(event.x_root, event.y_root)
            finally:
                menu.grab_release()

    # ------------------------------------------------------------------
    # Detail rail
    # ------------------------------------------------------------------
    def _show_detail(self, row: Optional[dict]) -> None:
        p, f = self.palette, self.fonts
        self._test_token += 1
        for child in self.rail_body.winfo_children():
            child.destroy()
        self._test_label = None
        if row is None:
            holder = tk.Frame(self.rail_body, bg=p.panel)
            holder.pack(fill="both", expand=True)
            tk.Label(
                holder, text="\u2316", bg=p.panel, fg=p.faint, font=(f.ui, 30),
            ).pack(pady=(60, 6))
            tk.Label(
                holder, text="Nothing selected", bg=p.panel, fg=p.text, font=f.body_bold,
            ).pack()
            tk.Label(
                holder, text="Select a row to inspect its details.", bg=p.panel,
                fg=p.muted, font=f.small,
            ).pack(pady=(4, 0))
            return
        if self.view == "hosts":
            self._host_detail(row["payload"])
        elif self.view == "config":
            self._config_detail(row["payload"])
        elif self.view == "keys":
            self._key_detail(row["payload"])
        else:
            self._activity_detail(row["payload"])
        self._sync_menu()

    def _section(self, parent: tk.Misc, title: str) -> None:
        p, f = self.palette, self.fonts
        tk.Label(
            parent, text=title.upper(), bg=p.panel, fg=p.faint,
            font=(f.ui, 9, "bold"), anchor="w",
        ).pack(fill="x", pady=(16, 6))

    def _field(
        self,
        parent: tk.Misc,
        label: str,
        value: str,
        *,
        mono: bool = True,
        copy_value: str = "",
        wrap: int = 190,
    ) -> None:
        p, f = self.palette, self.fonts
        row = tk.Frame(parent, bg=p.panel)
        row.pack(fill="x", pady=2)
        tk.Label(
            row, text=label, bg=p.panel, fg=p.muted, font=f.small,
            width=10, anchor="w",
        ).pack(side="left", anchor="n")
        holder = tk.Frame(row, bg=p.panel)
        holder.pack(side="left", fill="x", expand=True)
        tk.Label(
            holder, text=value, bg=p.panel, fg=p.text,
            font=f.mono_small if mono else f.small,
            justify="left", anchor="w", wraplength=wrap,
        ).pack(side="left", anchor="w")
        if copy_value:
            CanvasButton(
                holder, "Copy", command=lambda: self._copy(copy_value),
                kind="link", palette=p, fonts=f, bg=p.panel, height=18, padx=2,
                font=f.tiny,
            ).pack(side="right", padx=(4, 0), anchor="n")

    def _host_detail(self, host: HostView) -> None:
        p, f = self.palette, self.fonts
        body = self.rail_body
        pills = tk.Frame(body, bg=p.panel)
        pills.pack(fill="x")
        Pill(
            pills, "known_hosts" if host.in_known_hosts else "ssh config",
            palette=p, fonts=f, tone="accent" if host.in_known_hosts else "neutral",
        ).pack(side="left")
        if host.block is not None:
            Pill(pills, "configuration", palette=p, fonts=f, tone="neutral").pack(
                side="left", padx=(6, 0)
            )
        if host.marker:
            Pill(pills, host.marker.lstrip("@"), palette=p, fonts=f, tone="danger").pack(
                side="left", padx=(6, 0)
            )
        tk.Label(
            body, text=host.label, bg=p.panel, fg=p.text, font=f.heading,
            justify="left", anchor="w", wraplength=250,
        ).pack(fill="x", pady=(10, 2))
        tk.Label(
            body, text=f"{host.address}:{host.port or 22}", bg=p.panel,
            fg=p.muted, font=f.mono_small, anchor="w",
        ).pack(fill="x")

        self._section(body, "Connection")
        self._field(body, "Address", host.address, copy_value=host.address)
        self._field(body, "Port", str(host.port or 22))
        self._field(body, "User", host.user or "not set")
        self._field(body, "Identity", host.identity or "default")
        if host.alias and host.alias != host.address:
            self._field(body, "Alias", host.alias)

        self._section(body, f"Keys ({len(host.key_types)})" if host.key_types else "Keys")
        if host.key_types:
            for key_type, fingerprint in zip(host.key_types, host.fingerprints):
                row = tk.Frame(body, bg=p.panel)
                row.pack(fill="x", pady=1)
                tk.Label(
                    row, text=key_family(key_type), bg=p.panel, fg=p.text,
                    font=f.mono_small, width=8, anchor="w",
                ).pack(side="left")
                short = fingerprint if len(fingerprint) <= 24 else fingerprint[:23] + "\u2026"
                tk.Label(
                    row, text=short, bg=p.panel, fg=p.muted, font=f.tiny, anchor="w",
                ).pack(side="left", fill="x", expand=True)
                CanvasButton(
                    row, "Copy", command=lambda fp=fingerprint: self._copy(fp, "Fingerprint"),
                    kind="link", palette=p, fonts=f, bg=p.panel, height=18, padx=2,
                    font=f.tiny,
                ).pack(side="right")
        else:
            tk.Label(
                body, text="No host key recorded yet \u2014 it is added on first connect.",
                bg=p.panel, fg=p.muted, font=f.small, wraplength=250, justify="left",
            ).pack(fill="x")

        self._section(body, "Location")
        if host.known_hosts_lines:
            lines = ", ".join(str(line) for line in host.known_hosts_lines)
            self._field(body, "known_hosts", f"line {lines}")
        if host.block is not None:
            self._field(
                body, "config", f"line {host.block.start_line}\u2013{host.block.end_line}",
            )

        actions = tk.Frame(body, bg=p.panel)
        actions.pack(fill="x", pady=(18, 0))
        CanvasButton(
            actions, "Copy SSH command", command=lambda: self._copy(host.ssh_command, "SSH command"),
            kind="ghost", palette=p, fonts=f, height=32, bg=p.panel,
        ).pack(fill="x")
        CanvasButton(
            actions, "Test connection", command=lambda: self._test_connection(host),
            kind="ghost", palette=p, fonts=f, height=32, bg=p.panel,
        ).pack(fill="x", pady=(6, 0))
        self._test_label = tk.Label(
            actions, text="", bg=p.panel, fg=p.muted, font=f.tiny, anchor="w",
        )
        self._test_label.pack(fill="x", pady=(4, 0))
        CanvasButton(
            actions, "Remove host\u2026", command=self._delete_selected,
            kind="danger-soft", palette=p, fonts=f, height=32, bg=p.panel,
        ).pack(fill="x", pady=(8, 0))

    def _config_detail(self, block: ConfigBlock) -> None:
        p, f = self.palette, self.fonts
        body = self.rail_body
        state = (
            "global" if block.is_global else
            "included" if not block.editable else
            "pattern" if not block.deletable else "editable"
        )
        pills = tk.Frame(body, bg=p.panel)
        pills.pack(fill="x")
        Pill(pills, state, palette=p, fonts=f, tone="warn" if state != "editable" else "success").pack(side="left")
        if block.source.name != "config":
            Pill(pills, block.source.name, palette=p, fonts=f).pack(side="left", padx=(6, 0))

        tk.Label(
            body, text=block.label, bg=p.panel, fg=p.text, font=f.heading,
            justify="left", anchor="w", wraplength=250,
        ).pack(fill="x", pady=(10, 2))
        tk.Label(
            body, text=f"line {block.start_line}\u2013{block.end_line}", bg=p.panel,
            fg=p.muted, font=f.mono_small, anchor="w",
        ).pack(fill="x")

        self._section(body, "Settings")
        self._field(body, "HostName", block.option("hostname") or "\u2014")
        self._field(body, "User", block.option("user") or "not set")
        self._field(body, "Port", block.option("port") or "22")
        self._field(body, "Identity", block.option("identityfile") or "default")
        self._field(body, "ProxyJump", block.option("proxyjump") or "\u2014")
        self._field(body, "Options", str(len(block.options)))

        if state != "editable":
            self._section(body, "Note")
            tk.Label(
                body,
                text="This block cannot be removed safely from here. Global defaults, "
                     "Include files and wildcard patterns are managed by hand.",
                bg=p.panel, fg=p.muted, font=f.small, wraplength=250, justify="left",
            ).pack(fill="x")
        actions = tk.Frame(body, bg=p.panel)
        actions.pack(fill="x", pady=(18, 0))
        CanvasButton(
            actions, "Edit block\u2026", command=lambda: self._open_config_editor(block),
            kind="ghost", palette=p, fonts=f, height=32, bg=p.panel,
            enabled=self._block_editable(block),
        ).pack(fill="x")
        CanvasButton(
            actions, "Remove block\u2026", command=self._delete_selected,
            kind="danger-soft", palette=p, fonts=f, height=32, bg=p.panel,
            enabled=block.deletable,
        ).pack(fill="x", pady=(6, 0))

    def _key_detail(self, key: KeyInfo) -> None:
        p, f = self.palette, self.fonts
        body = self.rail_body
        pills = tk.Frame(body, bg=p.panel)
        pills.pack(fill="x")
        Pill(pills, "private key", palette=p, fonts=f, tone="accent").pack(side="left")
        if key.has_public:
            Pill(pills, "public key", palette=p, fonts=f).pack(side="left", padx=(6, 0))
        tk.Label(
            body, text=key.name, bg=p.panel, fg=p.text, font=f.heading,
            anchor="w", wraplength=250,
        ).pack(fill="x", pady=(10, 2))
        tk.Label(
            body, text=key.display_type, bg=p.panel, fg=p.muted, font=f.mono_small,
            anchor="w",
        ).pack(fill="x")

        self._section(body, "Fingerprint")
        tk.Label(
            body, text=key.fingerprint or "unavailable", bg=p.panel, fg=p.text,
            font=f.mono_small, justify="left", anchor="w", wraplength=250,
        ).pack(fill="x")
        CanvasButton(
            body, "Copy fingerprint", command=lambda: self._copy(key.fingerprint, "Fingerprint"),
            kind="ghost", palette=p, fonts=f, height=30, bg=p.panel,
            enabled=bool(key.fingerprint),
        ).pack(fill="x", pady=(8, 0))

        self._section(body, "Details")
        self._field(body, "Comment", key.comment or "\u2014", mono=False)
        self._field(body, "Passphrase", key.status)
        self._field(body, "Mode", key.permissions)
        self._field(body, "Modified", human_time(key.modified))
        self._field(body, "Path", key.path.name)

        actions = tk.Frame(body, bg=p.panel)
        actions.pack(fill="x", pady=(18, 0))
        CanvasButton(
            actions, "Reveal in Finder", command=lambda: self._reveal(key.path),
            kind="ghost", palette=p, fonts=f, height=32, bg=p.panel,
        ).pack(fill="x")
        tk.Label(
            body, text="Keys are never modified by this dashboard.",
            bg=p.panel, fg=p.faint, font=f.tiny, anchor="w",
        ).pack(fill="x", pady=(10, 0))

    def _activity_detail(self, entry: dict) -> None:
        p, f = self.palette, self.fonts
        body = self.rail_body
        action = ACTION_LABELS.get(entry.get("action", ""), entry.get("action", ""))
        tk.Label(
            body, text=action, bg=p.panel, fg=p.text, font=f.heading, anchor="w",
        ).pack(fill="x", pady=(4, 2))
        tk.Label(
            body, text=human_time(float(entry.get("ts", 0))), bg=p.panel, fg=p.muted,
            font=f.mono_small, anchor="w",
        ).pack(fill="x")
        self._section(body, "What changed")
        tk.Label(
            body, text=entry.get("detail", ""), bg=p.panel, fg=p.text, font=f.small,
            wraplength=250, justify="left", anchor="w",
        ).pack(fill="x")
        backups = (entry.get("meta") or {}).get("backups") or []
        if backups:
            self._section(body, "Backups")
            for path in backups[:6]:
                tk.Label(
                    body, text=Path(path).name, bg=p.panel, fg=p.muted, font=f.tiny,
                    anchor="w", wraplength=250, justify="left",
                ).pack(fill="x")
        self._section(body, "Files")
        CanvasButton(
            body, "Open backup folder", command=lambda: self._reveal(self.store.backup_dir),
            kind="ghost", palette=p, fonts=f, height=32, bg=p.panel,
        ).pack(fill="x", pady=(4, 0))

    # ------------------------------------------------------------------
    # Actions
    # ------------------------------------------------------------------
    def refresh(self, toast: bool = False) -> None:
        try:
            snapshot = self.store.snapshot()
        except Exception as exc:  # defensive: never lose the window
            self._error("Scan failed", str(exc))
            return
        self.snapshot = snapshot
        existing_hosts = {host.key for host in snapshot.hosts}
        existing_blocks = {
            f"block:{block.source}:{block.start_line}" for block in snapshot.config_blocks
        }
        self.selected["hosts"] &= existing_hosts
        self.selected["config"] &= existing_blocks
        self.nav_items["hosts"].set_count(str(len(snapshot.hosts)))
        self.nav_items["config"].set_count(str(snapshot.config_count))
        self.nav_items["keys"].set_count(str(len(snapshot.keys)))
        self._update_warnings()
        self._render_table()
        if toast:
            self.toast.show(
                f"Scan complete \u00b7 {snapshot.known_host_count} hosts, "
                f"{len(snapshot.keys)} keys",
                tone="success",
            )

    def _on_search_change(self) -> None:
        self._render_table()

    def _delete_selected(self) -> None:
        if self.view == "hosts":
            self._delete_hosts()
        elif self.view == "config":
            self._delete_blocks()

    def _delete_hosts(self) -> None:
        ids = set(self.selected["hosts"])
        hosts = [host for host in self.snapshot.hosts if host.key in ids]
        if not hosts:
            return
        config_targets = [
            host.block for host in hosts
            if host.block is not None and host.block.deletable and host.in_known_hosts
        ]
        config_only = [host for host in hosts if not host.in_known_hosts]
        count_label = _plural(len(hosts), "host")
        checkbox_label = ""
        if config_targets:
            checkbox_label = (
                f"Also remove {_plural(len(config_targets), 'matching ssh_config host block')}"
            )
        note = ""
        if config_only and len(config_only) == len(hosts):
            note = ("These entries live only in ssh_config; removing them touches only "
                    "~/.ssh/config.")
        if self.settings.confirm_before_delete:
            dialog = ConfirmDeleteDialog(
                self, palette=self.palette, fonts=self.fonts,
                hosts=[host.label for host in hosts],
                target_label=count_label,
                checkbox_label=checkbox_label,
                checkbox_default=bool(config_targets) and self.settings.remove_config_default,
                extra_note=note,
                confirm_label=f"Remove {count_label}",
            )
            if not dialog.ask():
                return
            remove_config = dialog.checked if checkbox_label else False
        else:
            remove_config = bool(config_targets) and self.settings.remove_config_default
        try:
            result = self.store.delete_hosts(hosts, remove_config=remove_config)
        except StoreError as exc:
            self._error("Could not remove hosts", str(exc))
            return
        self.selected["hosts"] -= ids
        self.detail_key = None
        if result.backups:
            self._last_backups = tuple(result.backups)
        self.refresh()
        self._show_detail(None)
        self._sync_menu()
        if result.hosts_removed or result.config_blocks_removed:
            self.toast.show(
                f"Removed {result.summary} \u00b7 backup saved",
                tone="success",
                action_label="Undo" if result.backups else "",
                action=(lambda: self._undo(result.backups)) if result.backups else None,
                timeout_ms=12000,
            )
        else:
            self.toast.show("Nothing was removed", tone="info")
        if result.warnings:
            self._warn("Some entries were skipped", "\n".join(result.warnings))

    def _delete_blocks(self) -> None:
        ids = set(self.selected["config"])
        blocks = [
            block for block in self.snapshot.config_blocks
            if f"block:{block.source}:{block.start_line}" in ids and block.deletable
        ]
        if not blocks:
            self.toast.show("Only editable Host blocks can be removed", tone="info")
            return
        count_label = _plural(len(blocks), "block")
        if self.settings.confirm_before_delete:
            dialog = ConfirmDeleteDialog(
                self, palette=self.palette, fonts=self.fonts,
                hosts=[block.label for block in blocks],
                target_label=count_label,
                checkbox_label="",
                checkbox_default=False,
                extra_note="The block and all of its options are removed from ~/.ssh/config.",
                confirm_label=f"Remove {count_label}",
            )
            if not dialog.ask():
                return
        try:
            result = self.store.delete_config_blocks(
                [(block.source, block.start_line, block.patterns) for block in blocks]
            )
        except StoreError as exc:
            self._error("Could not remove blocks", str(exc))
            return
        self.selected["config"] -= ids
        self.detail_key = None
        if result.backups:
            self._last_backups = tuple(result.backups)
        self.refresh()
        self._show_detail(None)
        self._sync_menu()
        if result.config_blocks_removed:
            self.toast.show(
                f"Removed {result.summary} \u00b7 backup saved",
                tone="success",
                action_label="Undo" if result.backups else "",
                action=(lambda: self._undo(result.backups)) if result.backups else None,
                timeout_ms=12000,
            )
        else:
            self.toast.show("Nothing was removed", tone="info")
        if result.warnings:
            self._warn("Some blocks were skipped", "\n".join(result.warnings))

    def _undo(self, backups: Sequence[Path]) -> None:
        errors: List[str] = []
        for backup in reversed(list(backups)):
            try:
                self.store.restore_backup(backup)
            except StoreError as exc:
                errors.append(str(exc))
        self._last_backups = ()
        self.refresh()
        if errors:
            self._error("Restore incomplete", "\n".join(errors))
        else:
            self.toast.show("Restored from backup", tone="success")

    def _test_connection(self, host: HostView) -> None:
        self._test_token += 1
        token = self._test_token
        if self._test_label is not None:
            self._test_label.configure(text="Testing connection\u2026", fg=self.palette.muted)
        address, port = host.address, host.port or 22

        def worker() -> None:
            error = ""
            try:
                with socket.create_connection((address, port), timeout=2.5):
                    reachable = True
            except OSError as exc:
                reachable = False
                error = str(exc)

            def finish() -> None:
                if token != self._test_token:
                    return
                label = self._test_label
                if label is None:
                    return
                try:
                    if reachable:
                        label.configure(text=f"Port {port} reachable", fg=self.palette.success)
                    else:
                        local = is_local_target(address)
                        if local:
                            label.configure(
                                text="Unreachable \u00b7 allow Local Network in Settings",
                                fg=self.palette.danger,
                            )
                        else:
                            label.configure(
                                text=f"Unreachable \u00b7 {error[:60]}", fg=self.palette.danger
                            )
                        if local and sys.platform == "darwin":
                            self.toast.show(
                                "macOS blocked local network access for this app "
                                "\u2014 allow Python under Privacy & Security \u2192 Local Network",
                                tone="danger",
                                action_label="Open Settings",
                                action=self._open_local_network_settings,
                                timeout_ms=15000,
                            )
                except tk.TclError:
                    pass

            try:
                self.after(0, finish)
            except tk.TclError:
                pass

        threading.Thread(target=worker, daemon=True).start()

    def _copy(self, value: str, label: str = "Text") -> None:
        if not value:
            return
        self.clipboard_clear()
        self.clipboard_append(value)
        self.update_idletasks()
        self.toast.show(f"{label} copied to clipboard", tone="info", timeout_ms=3200)

    def _reveal(self, path: Path) -> None:
        target = path if path.exists() else path.parent
        try:
            if sys.platform == "darwin":
                command = ["open", "-R", str(target)] if path.is_file() else ["open", str(target)]
                subprocess.Popen(command)
            elif os.name == "nt":
                if target.is_file():
                    subprocess.Popen(["explorer", "/select," + str(target)])
                else:
                    os.startfile(str(target))  # type: ignore[attr-defined]
            else:
                subprocess.Popen(["xdg-open", str(target)])
        except OSError as exc:
            self._error("Could not open the folder", str(exc))

    def _show_warnings(self) -> None:
        self._warn("Scan warnings", "\n".join(self.snapshot.warnings))

    def _error(self, heading: str, message: str) -> None:
        MessageDialog(
            self, palette=self.palette, fonts=self.fonts, title=heading,
            heading=heading, message=message, tone="danger",
        ).present()

    def _warn(self, heading: str, message: str) -> None:
        MessageDialog(
            self, palette=self.palette, fonts=self.fonts, title=heading,
            heading=heading, message=message, tone="warning",
        ).present()

    def _show_overflow(self) -> None:
        p = self.palette
        menu = tk.Menu(
            self, tearoff=0, bg=p.panel, fg=p.text, bd=0, relief="flat",
            activebackground=p.accent_soft, activeforeground=p.accent,
            font=self.fonts.body,
        )
        menu.add_command(label="Rescan SSH files", command=self.refresh)
        menu.add_command(label="Open ~/.ssh", command=lambda: self._reveal(self.store.ssh_dir))
        menu.add_command(
            label="Open backup folder", command=lambda: self._reveal(self.store.backup_dir),
        )
        menu.add_separator()
        menu.add_command(label="About SSH Dashboard", command=self._about)
        x = self.menu_btn.winfo_rootx()
        y = self.menu_btn.winfo_rooty() + self.menu_btn.winfo_height() + 4
        try:
            menu.tk_popup(x, y)
        finally:
            menu.grab_release()

    def _about(self) -> None:
        backup_count = len(self.store.backups())
        details = (
            f"Version      {__version__}\n"
            f"SSH folder   {self.store.ssh_dir}\n"
            f"Backups      {self.store.backup_dir} ({backup_count})\n"
            f"Settings     {support_dir()}\n"
            f"Runtime      Python {sys.version_info.major}.{sys.version_info.minor} "
            f"\u00b7 Tk {self.tk.call('info', 'patchlevel')}"
        )
        MessageDialog(
            self, palette=self.palette, fonts=self.fonts, title="About",
            heading="SSH Dashboard", message=(
                "A local host and key manager for OpenSSH.\n\n"
                "Removing an entry rewrites known_hosts or ssh_config atomically and "
                "keeps a timestamped backup you can restore at any time."
            ),
            details=details, tone="info",
            secondary_label="Backups", secondary=lambda: self._reveal(self.store.backup_dir),
            image=self._about_icon,
        ).present()

    def _toggle_theme(self) -> None:
        self._apply_theme("light" if self.settings.theme == "dark" else "dark")

    def _apply_theme(self, theme: str) -> None:
        if theme not in ("dark", "light"):
            return
        self.settings.theme = theme
        self.settings.save()
        self._theme_var.set(theme)
        self.palette = palette_for(theme)
        self.fonts = load_fonts(self)
        apply_ttk_styles(self, self.palette, self.fonts)
        query = self.query
        detail_key = self.detail_key
        self._build_ui()
        self.search.set(query)
        self._switch_view(self.view)
        self._theme_var.set(theme)
        if detail_key and detail_key in self._row_lookup:
            self.detail_key = detail_key
            self.tree.selection_set(detail_key)
            self.tree.focus(detail_key)
            self._show_detail(self._row_lookup[detail_key])

    # ------------------------------------------------------------------
    # Menu bar
    # ------------------------------------------------------------------
    def _accel(self, key: str) -> str:
        """Accelerator label for the current platform."""
        if self._modifier == "Command":
            return f"Command-{key}"
        return f"Ctrl+{key}"

    def _set_menu_state(self, menu: Optional[tk.Menu], label: str, enabled: bool) -> None:
        if menu is None:
            return
        try:
            menu.entryconfigure(label, state="normal" if enabled else "disabled")
        except tk.TclError:
            pass

    def _build_menubar(self) -> None:
        bar = tk.Menu(self)
        self.menubar = bar
        self.file_menu: Optional[tk.Menu] = None
        self.edit_menu: Optional[tk.Menu] = None
        self.view_menu: Optional[tk.Menu] = None
        self.tools_menu: Optional[tk.Menu] = None
        mac = sys.platform == "darwin"

        if mac:
            app_menu = tk.Menu(bar, name="apple", tearoff=0)
            bar.add_cascade(menu=app_menu, label=__app_name__)
            app_menu.add_command(label=f"About {__app_name__}", command=self._about)
            app_menu.add_separator()
            app_menu.add_command(
                label="Settings\u2026", accelerator=self._accel(","),
                command=self._open_settings,
            )
            app_menu.add_separator()
            app_menu.add_command(
                label=f"Hide {__app_name__}", accelerator=self._accel("h"),
                command=lambda: self.tk.call("tk::mac::Hide", ""),
            )
            app_menu.add_command(
                label="Hide Others", accelerator=self._accel("Option-h"),
                command=lambda: self.tk.call("tk::mac::HideOthers", ""),
            )
            app_menu.add_command(
                label="Show All", command=lambda: self.tk.call("tk::mac::ShowAll", ""),
            )
            app_menu.add_separator()
            app_menu.add_command(
                label=f"Quit {__app_name__}", accelerator=self._accel("q"),
                command=self._on_close,
            )

        # -- File ---------------------------------------------------------
        file_menu = tk.Menu(bar, tearoff=0)
        bar.add_cascade(menu=file_menu, label="File")
        self.file_menu = file_menu
        file_menu.add_command(
            label="Rescan SSH Files", accelerator=self._accel("r"),
            command=lambda: self.refresh(toast=True),
        )
        file_menu.add_command(
            label="Export Visible Rows\u2026", accelerator=self._accel("e"),
            command=self._export_rows,
        )
        file_menu.add_separator()
        file_menu.add_command(
            label="Open SSH Folder", command=lambda: self._reveal(self.store.ssh_dir),
        )
        file_menu.add_command(
            label="Open Backups Folder", command=lambda: self._reveal(self.store.backup_dir),
        )
        file_menu.add_separator()
        if mac:
            file_menu.add_command(
                label="Close Window", accelerator=self._accel("w"), command=self._on_close,
            )
        else:
            file_menu.add_command(
                label="Settings\u2026", accelerator=self._accel(","),
                command=self._open_settings,
            )
            file_menu.add_separator()
            file_menu.add_command(
                label="Exit", accelerator=self._accel("q"), command=self._on_close,
            )

        # -- Edit ---------------------------------------------------------
        edit_menu = tk.Menu(bar, tearoff=0)
        bar.add_cascade(menu=edit_menu, label="Edit")
        self.edit_menu = edit_menu
        edit_menu.add_command(
            label="Undo Removal", accelerator=self._accel("z"), command=self._menu_undo,
        )
        edit_menu.add_separator()
        edit_menu.add_command(
            label="Copy", accelerator=self._accel("c"),
            command=lambda: self._menu_clipboard("<<Copy>>"),
        )
        edit_menu.add_command(
            label="Paste", accelerator=self._accel("v"),
            command=lambda: self._menu_clipboard("<<Paste>>"),
        )
        edit_menu.add_separator()
        edit_menu.add_command(
            label="Select All Visible", accelerator=self._accel("a"), command=self._toggle_all,
        )
        edit_menu.add_command(
            label="Deselect All", accelerator=self._accel("Shift-A"),
            command=self._clear_selection,
        )
        edit_menu.add_separator()
        edit_menu.add_command(
            label="Find\u2026", accelerator=self._accel("f"),
            command=lambda: self.search.focus(),
        )
        edit_menu.add_command(
            label="Clear Search", accelerator=self._accel("Shift-F"), command=self._clear_search,
        )

        # -- View ---------------------------------------------------------
        view_menu = tk.Menu(bar, tearoff=0)
        bar.add_cascade(menu=view_menu, label="View")
        self.view_menu = view_menu
        for index, key in enumerate(NAV_ORDER, start=1):
            view_menu.add_radiobutton(
                label=NAV_LABELS[key], value=key, variable=self._view_var,
                accelerator=self._accel(str(index)),
                command=lambda k=key: self._switch_view(k),
            )
        view_menu.add_separator()
        appearance = tk.Menu(view_menu, tearoff=0)
        view_menu.add_cascade(menu=appearance, label="Appearance")
        appearance.add_radiobutton(
            label="Dark", value="dark", variable=self._theme_var,
            command=lambda: self._apply_theme("dark"),
        )
        appearance.add_radiobutton(
            label="Light", value="light", variable=self._theme_var,
            command=lambda: self._apply_theme("light"),
        )

        # -- Tools --------------------------------------------------------
        tools_menu = tk.Menu(bar, tearoff=0)
        bar.add_cascade(menu=tools_menu, label="Tools")
        self.tools_menu = tools_menu
        tools_menu.add_command(
            label="Edit Selected Block\u2026", accelerator=self._accel("Shift-E"),
            command=self._edit_selected_block,
        )
        tools_menu.add_command(
            label="Test Connection", command=self._test_selected_host,
        )
        tools_menu.add_command(
            label="Copy SSH Command", accelerator=self._accel("Shift-C"),
            command=self._copy_selected_command,
        )
        tools_menu.add_command(
            label="Reveal Selected Key", command=self._reveal_selected_key,
        )
        tools_menu.add_separator()
        tools_menu.add_command(label="Check Environment\u2026", command=self._check_environment)

        if mac:
            window_menu = tk.Menu(bar, name="window", tearoff=0)
            bar.add_cascade(menu=window_menu, label="Window")
            window_menu.add_command(
                label="Minimize", accelerator=self._accel("m"), command=self.iconify,
            )
            # Single-window app: Minimize is the only window action worth exposing.

        # -- Help ---------------------------------------------------------
        help_menu = tk.Menu(bar, tearoff=0, name="help") if mac else tk.Menu(bar, tearoff=0)
        bar.add_cascade(menu=help_menu, label="Help")
        help_menu.add_command(label="SSH Dashboard Help", command=self._show_help)
        help_menu.add_command(
            label="Keyboard Shortcuts", accelerator=self._accel("/"),
            command=self._show_shortcuts,
        )
        help_menu.add_command(label="Backups & Safety", command=self._show_safety)
        if mac:
            help_menu.add_command(
                label="Local Network Access\u2026", command=self._show_local_network,
            )
        if not mac:
            help_menu.add_separator()
            help_menu.add_command(label=f"About {__app_name__}", command=self._about)

        self.config(menu=bar)
        self._sync_menu()

    def _sync_menu(self) -> None:
        if not hasattr(self, "menubar"):
            return
        host = self._selected_host()
        block = self._selected_block()
        key = self._selected_key()
        self._set_menu_state(self.edit_menu, "Undo Removal", bool(self._last_backups))
        self._set_menu_state(self.file_menu, "Export Visible Rows\u2026", bool(self.rows))
        self._set_menu_state(self.tools_menu, "Edit Selected Block\u2026", block is not None)
        self._set_menu_state(self.tools_menu, "Test Connection", host is not None)
        self._set_menu_state(self.tools_menu, "Copy SSH Command", host is not None)
        self._set_menu_state(self.tools_menu, "Reveal Selected Key", key is not None)

    # -- selection helpers ----------------------------------------------
    def _selected_row(self) -> Optional[dict]:
        if not self.detail_key:
            return None
        return self._row_lookup.get(self.detail_key)

    def _selected_host(self) -> Optional[HostView]:
        row = self._selected_row()
        if row is not None and self.view == "hosts":
            return row["payload"]
        return None

    def _selected_block(self) -> Optional[ConfigBlock]:
        row = self._selected_row()
        if row is not None and self.view == "config":
            block = row["payload"]
            return block if self._block_editable(block) else None
        return None

    def _selected_key(self) -> Optional[KeyInfo]:
        row = self._selected_row()
        if row is not None and self.view == "keys":
            return row["payload"]
        return None

    # -- menu commands ---------------------------------------------------
    def _menu_undo(self) -> None:
        if isinstance(self.focus_get(), tk.Entry):
            return
        if self._last_backups:
            self._undo(self._last_backups)

    def _menu_clipboard(self, event_name: str) -> None:
        widget = self.focus_get()
        if widget is not None and widget.winfo_class() in ("Entry", "TEntry"):
            widget.event_generate(event_name)
            return
        if event_name == "<<Copy>>":
            host = self._selected_host()
            key = self._selected_key()
            if host is not None:
                self._copy(host.ssh_command, "SSH command")
            elif key is not None:
                self._copy(key.fingerprint, "Fingerprint")

    def _edit_selected_block(self) -> None:
        block = self._selected_block()
        if block is not None:
            self._open_config_editor(block)
        else:
            self.toast.show("Select an editable Host block first", tone="info")

    def _test_selected_host(self) -> None:
        host = self._selected_host()
        if host is not None:
            self._test_connection(host)

    def _copy_selected_command(self) -> None:
        host = self._selected_host()
        if host is not None:
            self._copy(host.ssh_command, "SSH command")

    def _reveal_selected_key(self) -> None:
        key = self._selected_key()
        if key is not None:
            self._reveal(key.path)

    # -- export ----------------------------------------------------------
    def _export_rows(self) -> None:
        if not self.rows:
            return
        from tkinter import filedialog
        import csv

        columns = COLUMN_SETS[self.view]
        keep = [index for index, column in enumerate(columns) if column[0] != "select"]
        default_name = f"ssh-{self.view}-{time.strftime('%Y%m%d')}.csv"
        path = filedialog.asksaveasfilename(
            parent=self, title="Export visible rows", defaultextension=".csv",
            initialfile=default_name,
            filetypes=[("CSV file", "*.csv"), ("All files", "*.*")],
        )
        if not path:
            return
        try:
            with open(path, "w", newline="", encoding="utf-8") as handle:
                writer = csv.writer(handle)
                writer.writerow([columns[index][1] for index in keep])
                for row in self.rows:
                    values = list(row["values"])
                    writer.writerow([values[index] for index in keep])
        except OSError as exc:
            self._error("Export failed", str(exc))
            return
        self.toast.show(f"Exported {len(self.rows)} rows", tone="success")

    # -- settings --------------------------------------------------------
    def _open_settings(self) -> None:
        from .widgets import SettingsDialog

        dialog = SettingsDialog(
            self, palette=self.palette, fonts=self.fonts,
            theme=self.settings.theme,
            confirm_before_delete=self.settings.confirm_before_delete,
            remove_config_default=self.settings.remove_config_default,
            ssh_dir=str(self.store.ssh_dir),
            backup_dir=str(self.store.backup_dir),
            backup_count=len(self.store.backups()),
            on_reveal_backups=lambda: self._reveal(self.store.backup_dir),
        )
        dialog.present()
        self.wait_window(dialog)
        if not dialog.result:
            return
        theme = str(dialog.result.get("theme", self.settings.theme))
        self.settings.confirm_before_delete = bool(
            dialog.result.get("confirm_before_delete", True)
        )
        self.settings.remove_config_default = bool(
            dialog.result.get("remove_config_default", True)
        )
        self.settings.save()
        if theme != self.palette.key:
            self._apply_theme(theme)
        else:
            self._theme_var.set(theme)
            self.toast.show("Settings saved", tone="success")

    # -- help ------------------------------------------------------------
    def _show_help(self) -> None:
        MessageDialog(
            self, palette=self.palette, fonts=self.fonts, title="Help",
            heading="What this dashboard does",
            message=(
                "SSH Dashboard reads your local OpenSSH files: known_hosts, ssh_config "
                "and key pairs. Tick entries in the first column to remove them, or "
                "edit a Host block in the Configuration view.\n\n"
                "Every write is atomic and is preceded by a timestamped backup. Use "
                "Undo in the toast, or Edit \u2192 Undo Removal, to revert the last change."
            ),
            details=(
                f"SSH folder     {self.store.ssh_dir}\n"
                f"Backups        {self.store.backup_dir}\n"
                f"Settings       {support_dir()}"
            ),
            tone="info", image=self._about_icon,
        ).present()

    def _show_shortcuts(self) -> None:
        mod = "\u2318" if self._modifier == "Command" else "Ctrl+"
        rows = (
            f"{mod}R            Rescan SSH files\n"
            f"{mod}F            Focus search\n"
            f"{mod}A            Select / deselect all visible\n"
            f"{mod}E            Export visible rows\n"
            f"{mod}Z            Undo the last removal or edit\n"
            f"{mod}1 \u2026 {mod}4        Switch view\n"
            f"{mod},            Settings\n"
            f"{mod}/            This shortcut list\n"
            f"{mod}Q            Quit\n\n"
            "Space           Tick the focused row\n"
            "Delete          Remove the selected rows\n"
            "Esc             Clear search, then selection\n"
            "Double-click    Edit a Host block (Configuration)"
        )
        MessageDialog(
            self, palette=self.palette, fonts=self.fonts, title="Keyboard shortcuts",
            heading="Keyboard shortcuts", message="", details=rows, tone="info",
        ).present()

    def _open_local_network_settings(self) -> None:
        if sys.platform != "darwin":
            return
        url = "x-apple.systempreferences:com.apple.preference.security?Privacy_LocalNetwork"
        try:
            subprocess.Popen(["open", url])
        except OSError as exc:
            self._error("Could not open System Settings", str(exc))

    def _show_local_network(self) -> None:
        MessageDialog(
            self, palette=self.palette, fonts=self.fonts, title="Local network access",
            heading="Testing hosts on your local network",
            message=(
                "macOS asks for Local Network access the first time an app connects "
                "to a private address (10.x, 172.16-31.x, 192.168.x, link-local or "
                "*.local). External hosts keep working even when this is blocked.\n\n"
                "The small macOS build runs on your installed Python, so macOS lists "
                "the permission under \u201cPython\u201d. The self-contained build is "
                "listed as \u201cSSH Dashboard\u201d."
            ),
            details=(
                "System Settings \u2192 Privacy & Security \u2192 Local Network\n"
                "Enable Python (small build) or SSH Dashboard (fat build), then "
                "quit and reopen the app."
            ),
            tone="info",
            secondary_label="Open Settings",
            secondary=self._open_local_network_settings,
        ).present()

    def _show_safety(self) -> None:
        MessageDialog(
            self, palette=self.palette, fonts=self.fonts, title="Backups & safety",
            heading="Backups & safety",
            message=(
                "Before any change, both known_hosts and ssh_config are copied to the "
                "backup folder with a timestamp. Files are rewritten atomically and keep "
                "their original permissions. Hashed known_hosts entries are supported, "
                "and entries are removed by exact token, never by substring."
            ),
            details=(
                f"Backup folder\n{self.store.backup_dir}\n\n"
                f"Backups kept   {len(self.store.backups())}\n"
                "Included ssh_config files and global Host blocks are read-only."
            ),
            tone="info",
            secondary_label="Open folder",
            secondary=lambda: self._reveal(self.store.backup_dir),
        ).present()

    def _check_environment(self) -> None:
        import platform

        frozen = bool(getattr(sys, "frozen", False))
        details = (
            f"Python       {platform.python_version()}\n"
            f"Tk           {self.tk.call('info', 'patchlevel')}\n"
            f"Platform     {platform.platform()}\n"
            f"Frozen       {'yes' if frozen else 'no'}\n"
            f"SSH folder   {self.store.ssh_dir}\n"
            f"Backups      {self.store.backup_dir} ({len(self.store.backups())})\n"
            f"Settings     {support_dir()}"
        )
        MessageDialog(
            self, palette=self.palette, fonts=self.fonts, title="Environment",
            heading="Runtime environment",
            message="Useful when reporting a problem or checking a packaged build.",
            details=details, tone="info",
        ).present()

    # ------------------------------------------------------------------
    # Keyboard
    # ------------------------------------------------------------------
    def _bind_keys(self) -> None:
        mod = self._modifier
        self.bind(f"<{mod}-e>", lambda _e: self._export_rows())
        self.bind(f"<{mod}-comma>", lambda _e: self._open_settings())
        self.bind(f"<{mod}-z>", lambda _e: self._menu_undo())
        self.bind(f"<{mod}-w>", lambda _e: self._on_close())
        self.bind(f"<{mod}-q>", lambda _e: self._on_close())
        self.bind(f"<{mod}-slash>", lambda _e: self._show_shortcuts())
        self.bind(f"<{mod}-Shift-a>", lambda _e: self._clear_selection())
        self.bind(f"<{mod}-Shift-f>", lambda _e: self._clear_search())
        self.bind(f"<{mod}-Shift-c>", lambda _e: self._copy_selected_command())
        self.bind(f"<{mod}-Shift-e>", lambda _e: self._edit_selected_block())
        self.bind(f"<{mod}-m>", lambda _e: self.iconify())
        for index, view_key in enumerate(NAV_ORDER, start=1):
            self.bind(f"<{mod}-Key-{index}>", lambda _e, v=view_key: self._switch_view(v))
        self.bind("<Command-r>", lambda _e: self.refresh(toast=True))
        self.bind("<Control-r>", lambda _e: self.refresh(toast=True))
        self.bind("<Command-f>", lambda _e: self.search.focus())
        self.bind("<Control-f>", lambda _e: self.search.focus())
        self.bind("<Command-a>", self._key_select_all)
        self.bind("<Control-a>", self._key_select_all)
        self.bind("<Delete>", self._key_delete)
        self.bind("<BackSpace>", self._key_delete)
        self.bind("<space>", self._key_toggle)
        self.bind("<Escape>", self._key_escape)

    def _entry_has_focus(self) -> bool:
        return isinstance(self.focus_get(), tk.Entry)

    def _key_select_all(self, _event: Optional[tk.Event] = None) -> str:
        if self._entry_has_focus():
            return ""
        if self.view in ("hosts", "config"):
            self._toggle_all()
            return "break"
        return ""

    def _key_delete(self, _event: Optional[tk.Event] = None) -> str:
        if self._entry_has_focus():
            return ""
        if self.view in ("hosts", "config") and self._selected_keys():
            self._delete_selected()
            return "break"
        return ""

    def _key_toggle(self, _event: Optional[tk.Event] = None) -> str:
        if self._entry_has_focus() or self.view not in ("hosts", "config"):
            return ""
        iid = self.tree.focus()
        if iid and iid in self._row_lookup and self._selectable(self._row_lookup[iid]):
            self._toggle_row(iid)
            return "break"
        return ""

    def _key_escape(self, _event: Optional[tk.Event] = None) -> str:
        if self.query:
            self._clear_search()
            return "break"
        if self._selected_keys():
            self._clear_selection()
            return "break"
        if self._entry_has_focus():
            self.focus_set()
            return "break"
        return ""


def _log_unhandled(exc_type, exc, tb) -> None:
    """Persist unexpected Tk callback errors instead of losing them to stderr."""
    import traceback

    text = "".join(traceback.format_exception(exc_type, exc, tb))
    try:
        path = support_dir() / "error.log"
        path.parent.mkdir(parents=True, exist_ok=True)
        with path.open("a", encoding="utf-8") as handle:
            handle.write(f"\n[{time.strftime('%Y-%m-%d %H:%M:%S')}]\n{text}")
    except OSError:
        pass
    sys.stderr.write(text)


def main() -> None:
    try:
        app = DashboardApp()
    except Exception:
        import traceback

        sys.stderr.write(traceback.format_exc())
        try:
            path = support_dir() / "error.log"
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(traceback.format_exc(), encoding="utf-8")
        except OSError:
            pass
        raise
    app.report_callback_exception = _log_unhandled
    autoclose = os.environ.get("SSHDASH_AUTOCLOSE_MS", "")
    if autoclose.isdigit():
        app.after(int(autoclose), app._on_close)
    app.mainloop()
