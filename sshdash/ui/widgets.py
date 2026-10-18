"""Reusable widgets: rounded buttons, search field, pills, toasts and dialogs.

Everything is drawn with plain Tk so the app has one consistent visual language
instead of a mix of native macOS controls and ttk defaults.
"""

from __future__ import annotations

import tkinter as tk
from tkinter import font as tkfont
from typing import Callable, Dict, Iterable, Optional, Sequence

from .theme import Fonts, Palette


# ---------------------------------------------------------------------------
# Drawing helpers
# ---------------------------------------------------------------------------

def mix(color_a: str, color_b: str, ratio: float) -> str:
    """Blend two ``#rrggbb`` colours (ratio 0 → a, 1 → b)."""
    ratio = max(0.0, min(1.0, ratio))
    a = tuple(int(color_a.lstrip("#")[i:i + 2], 16) for i in (0, 2, 4))
    b = tuple(int(color_b.lstrip("#")[i:i + 2], 16) for i in (0, 2, 4))
    blended = tuple(round(x + (y - x) * ratio) for x, y in zip(a, b))
    return "#%02x%02x%02x" % blended


def round_rect(canvas: tk.Canvas, x1: int, y1: int, x2: int, y2: int, radius: int, **kwargs) -> int:
    """Draw a rounded rectangle as a smoothed polygon (no images needed)."""
    radius = max(0, min(radius, (x2 - x1) // 2, (y2 - y1) // 2))
    points = [
        x1 + radius, y1, x2 - radius, y1, x2, y1,
        x2, y1 + radius, x2, y2 - radius, x2, y2,
        x2 - radius, y2, x1 + radius, y2, x1, y2,
        x1, y2 - radius, x1, y1 + radius, x1, y1,
    ]
    return canvas.create_polygon(points, smooth=True, splinesteps=12, **kwargs)


# ---------------------------------------------------------------------------
# Buttons
# ---------------------------------------------------------------------------

class CanvasButton(tk.Canvas):
    """A rounded, theme-aware button with hover/press/disabled states."""

    def __init__(
        self,
        master: tk.Misc,
        text: str = "",
        command: Optional[Callable[[], None]] = None,
        *,
        palette: Palette,
        fonts: Fonts,
        kind: str = "primary",
        glyph: str = "",
        height: int = 34,
        width: Optional[int] = None,
        padx: int = 14,
        radius: int = 8,
        font: Optional[tuple] = None,
        bg: Optional[str] = None,
        enabled: bool = True,
    ) -> None:
        self.palette = palette
        self._bg = bg or palette.bg
        self._kind = kind
        self._glyph = glyph
        self._text = text
        self._command = command
        self._height = height
        self._padx = padx
        self._radius = radius
        self._font = font or fonts.body
        self._enabled = enabled
        self._hover = False
        self._pressed = False
        self._measure_font = tkfont.Font(font=self._font)

        computed = width if width is not None else self._natural_width()
        super().__init__(
            master,
            height=height,
            width=computed,
            bg=self._bg,
            bd=0,
            highlightthickness=0,
            cursor="hand2" if enabled else "arrow",
        )
        self.bind("<Configure>", lambda _event: self._draw())
        self.bind("<Enter>", self._on_enter)
        self.bind("<Leave>", self._on_leave)
        self.bind("<Button-1>", self._on_press)
        self.bind("<ButtonRelease-1>", self._on_release)
        self._draw()

    # -- public ----------------------------------------------------------
    def set_enabled(self, enabled: bool) -> None:
        if enabled == self._enabled:
            return
        self._enabled = enabled
        self.configure(cursor="hand2" if enabled else "arrow")
        self._draw()

    def set_kind(self, kind: str) -> None:
        self._kind = kind
        self._draw()

    def set_text(self, text: str) -> None:
        self._text = text
        self.configure(width=self._natural_width())
        self._draw()

    def set_bg(self, color: str) -> None:
        self._bg = color
        self.configure(bg=color)
        self._draw()

    def invoke(self) -> None:
        if self._enabled and self._command:
            self._command()

    # -- internals -------------------------------------------------------
    def _label(self) -> str:
        if self._glyph and self._text:
            return f"{self._glyph}  {self._text}"
        return self._glyph or self._text

    def _natural_width(self) -> int:
        return self._measure_font.measure(self._label()) + self._padx * 2

    def _colors(self) -> tuple:
        p = self.palette
        if not self._enabled:
            return p.panel_alt, p.faint, p.border
        base = {
            "primary": (p.accent, p.on_accent, ""),
            "danger": (p.danger, "#FFFFFF", ""),
            "danger-soft": (p.danger_soft, p.danger_text, mix(p.danger_soft, p.danger, 0.35)),
            "ghost": (p.panel, p.text, p.border),
            "quiet": (self._bg, p.muted, ""),
            "link": (self._bg, p.accent, ""),
        }.get(self._kind, (p.accent, p.on_accent, ""))
        fill, fg, border = base
        if self._kind == "primary":
            fill = p.accent_pressed if self._pressed else (p.accent_hover if self._hover else p.accent)
        elif self._kind == "danger":
            fill = mix(p.danger, "#000000", 0.14) if self._pressed else (p.danger_hover if self._hover else p.danger)
        elif self._kind == "danger-soft":
            fill = mix(p.danger_soft, p.danger, 0.26 if self._pressed else (0.16 if self._hover else 0.06))
        elif self._kind == "ghost":
            fill = p.panel_alt if (self._hover or self._pressed) else p.panel
            border = p.border_strong if self._hover else p.border
        elif self._kind == "quiet":
            fill = p.panel_alt if (self._hover or self._pressed) else self._bg
            fg = p.text if (self._hover or self._pressed) else p.muted
        elif self._kind == "link":
            fg = p.accent_hover if (self._hover or self._pressed) else p.accent
        return fill, fg, border

    def _draw(self) -> None:
        self.delete("all")
        width = self.winfo_width() or int(self["width"])
        height = self.winfo_height() or self._height
        if width <= 2 or height <= 2:
            return
        fill, fg, border = self._colors()
        round_rect(
            self, 1, 1, width - 1, height - 1, self._radius,
            fill=fill, outline=border or fill, width=1,
        )
        self.create_text(
            width / 2, height / 2 + 1, text=self._label(), fill=fg, font=self._font,
        )

    def _on_enter(self, _event: tk.Event) -> None:
        if not self._enabled:
            return
        self._hover = True
        self._draw()

    def _on_leave(self, _event: tk.Event) -> None:
        self._hover = False
        self._pressed = False
        self._draw()

    def _on_press(self, _event: tk.Event) -> None:
        if not self._enabled:
            return
        self._pressed = True
        self._draw()

    def _on_release(self, event: tk.Event) -> None:
        if not self._enabled:
            return
        inside = 0 <= event.x <= self.winfo_width() and 0 <= event.y <= self.winfo_height()
        self._pressed = False
        self._draw()
        if inside:
            self.invoke()


class CheckRow(tk.Frame):
    """A theme-drawn checkbox with a label."""

    def __init__(
        self,
        master: tk.Misc,
        text: str,
        *,
        palette: Palette,
        fonts: Fonts,
        variable: tk.BooleanVar,
        bg: Optional[str] = None,
        wraplength: int = 0,
    ) -> None:
        self.palette = palette
        self._var = variable
        self._on_bg = bg or palette.panel
        super().__init__(master, bg=self._on_bg, cursor="hand2")
        self._box = tk.Label(
            self, text="", font=fonts.small, bg=self._on_bg, cursor="hand2", width=1, anchor="w"
        )
        self._label = tk.Label(
            self,
            text=text,
            font=fonts.body,
            bg=self._on_bg,
            fg=palette.text,
            cursor="hand2",
            justify="left",
            anchor="w",
            wraplength=wraplength,
        )
        self._box.pack(side="left", padx=(0, 8))
        self._label.pack(side="left", fill="x", expand=True)
        for widget in (self, self._box, self._label):
            widget.bind("<Button-1>", self._toggle)
        self._var.trace_add("write", lambda *_: self._refresh())
        self._refresh()

    def _toggle(self, _event: Optional[tk.Event] = None) -> None:
        self._var.set(not self._var.get())

    def _refresh(self) -> None:
        if self._var.get():
            self._box.configure(text="\u2713", fg=self.palette.accent)
            self._label.configure(fg=self.palette.text)
        else:
            self._box.configure(text="\u00b7", fg=self.palette.faint)
            self._label.configure(fg=self.palette.muted)


# ---------------------------------------------------------------------------
# Search field
# ---------------------------------------------------------------------------

class SearchField(tk.Canvas):
    """Rounded search box with placeholder, leading glyph and clear button."""

    def __init__(
        self,
        master: tk.Misc,
        *,
        palette: Palette,
        fonts: Fonts,
        placeholder: str = "Search…",
        width: int = 300,
        height: int = 36,
        on_change: Optional[Callable[[str], None]] = None,
    ) -> None:
        self.palette = palette
        self.fonts = fonts
        self._bg = palette.bg
        self._height = height
        super().__init__(
            master, width=width, height=height, bg=self._bg,
            bd=0, highlightthickness=0, cursor="xterm",
        )
        self._placeholder = placeholder
        self._on_change = on_change
        self._focused = False
        self._has_placeholder = False

        self.entry = tk.Entry(
            self,
            bd=0,
            relief="flat",
            highlightthickness=0,
            bg=palette.panel_alt,
            fg=palette.text,
            insertbackground=palette.text,
            font=fonts.body,
            disabledbackground=palette.panel_alt,
        )
        self._window = self.create_window(38, height // 2, anchor="w", window=self.entry)
        self.entry.bind("<FocusIn>", self._on_focus_in)
        self.entry.bind("<FocusOut>", self._on_focus_out)
        self.entry.bind("<Escape>", lambda _e: self.clear())
        self.entry.bind("<KeyRelease>", self._on_key)
        self.bind("<Configure>", self._on_resize)
        self.bind("<Button-1>", lambda _e: self.entry.focus_set())
        self._show_placeholder()
        self._draw()

    # -- API -------------------------------------------------------------
    def get(self) -> str:
        return "" if self._has_placeholder else self.entry.get().strip()

    def clear(self) -> None:
        self.entry.delete(0, "end")
        self._has_placeholder = False
        if not self._focused:
            self._show_placeholder()
        self._draw()
        self._notify()

    def set_placeholder(self, text: str) -> None:
        self._placeholder = text
        if self._has_placeholder:
            self._show_placeholder()
            self._draw()

    def set(self, value: str) -> None:
        self.entry.delete(0, "end")
        self._has_placeholder = False
        if value:
            self.entry.insert(0, value)
            self.entry.configure(fg=self.palette.text)
        elif not self._focused:
            self._show_placeholder()
        self._draw()

    def focus(self) -> None:
        self.entry.focus_set()

    # -- internals -------------------------------------------------------
    def _show_placeholder(self) -> None:
        self.entry.configure(fg=self.palette.faint)
        self.entry.delete(0, "end")
        self.entry.insert(0, self._placeholder)
        self._has_placeholder = True

    def _clear_placeholder(self) -> None:
        if self._has_placeholder:
            self.entry.delete(0, "end")
            self.entry.configure(fg=self.palette.text)
            self._has_placeholder = False

    def _on_focus_in(self, _event: tk.Event) -> None:
        self._focused = True
        self._clear_placeholder()
        self._draw()

    def _on_focus_out(self, _event: tk.Event) -> None:
        self._focused = False
        if not self.entry.get().strip():
            self._show_placeholder()
        self._draw()

    def _on_key(self, _event: tk.Event) -> None:
        self._draw()
        self._notify()

    def _notify(self) -> None:
        if self._on_change:
            self._on_change(self.get())

    def _on_resize(self, _event: tk.Event) -> None:
        width = self.winfo_width()
        self.coords(self._window, 38, self.winfo_height() // 2)
        self.itemconfigure(self._window, width=max(80, width - 38 - 34))
        self._draw()

    def _draw(self) -> None:
        self.delete("deco")
        width = self.winfo_width() or int(self["width"])
        height = self.winfo_height() or self._height
        if width <= 4:
            return
        focused = bool(self.focus_get() is self.entry)
        border = self.palette.accent if focused else self.palette.border
        round_rect(
            self, 1, 1, width - 1, height - 1, 9,
            fill=self.palette.panel_alt, outline=border, width=1, tags="bg",
        )
        self.tag_lower("bg")
        self.create_text(
            20, height // 2 + 1, text="\u2315", fill=self.palette.muted,
            font=(self.fonts.ui, 14), tags="deco",
        )
        if self.entry.get() and not self._has_placeholder:
            self.create_text(
                width - 18, height // 2, text="\u2715", fill=self.palette.faint,
                font=(self.fonts.ui, 10), tags=("deco", "clear"),
            )
            self.tag_bind("clear", "<Button-1>", lambda _e: self.clear())


# ---------------------------------------------------------------------------
# Small pieces
# ---------------------------------------------------------------------------

class TextField(tk.Canvas):
    """Rounded single-line text input used by the editors."""

    def __init__(
        self,
        master: tk.Misc,
        *,
        palette: Palette,
        fonts: Fonts,
        placeholder: str = "",
        width: int = 300,
        height: int = 36,
        mono: bool = False,
        textvariable: Optional[tk.StringVar] = None,
    ) -> None:
        self.palette = palette
        self.fonts = fonts
        self._bg = palette.bg
        self._height = height
        super().__init__(
            master, width=width, height=height, bg=self._bg,
            bd=0, highlightthickness=0, cursor="xterm",
        )
        self._placeholder = placeholder
        self._has_placeholder = False
        self.entry = tk.Entry(
            self,
            bd=0,
            relief="flat",
            highlightthickness=0,
            bg=palette.panel_alt,
            fg=palette.text,
            insertbackground=palette.text,
            font=fonts.mono_body if mono else fonts.body,
            textvariable=textvariable,
        )
        self._window = self.create_window(12, height // 2, anchor="w", window=self.entry)
        self.entry.bind("<FocusIn>", self._focus_in)
        self.entry.bind("<FocusOut>", self._focus_out)
        self.entry.bind("<KeyRelease>", lambda _e: self._draw())
        self.bind("<Configure>", self._resize)
        self.bind("<Button-1>", lambda _e: self.entry.focus_set())
        if not self.entry.get():
            self._show_placeholder()
        self._draw()

    def get(self) -> str:
        return "" if self._has_placeholder else self.entry.get()

    def set(self, value: str) -> None:
        self.entry.delete(0, "end")
        self._has_placeholder = False
        self.entry.configure(fg=self.palette.text)
        if value:
            self.entry.insert(0, value)
        elif not (self.focus_get() is self.entry):
            self._show_placeholder()
        self._draw()

    def focus(self) -> None:
        self.entry.focus_set()

    def _show_placeholder(self) -> None:
        self.entry.configure(fg=self.palette.faint)
        self.entry.delete(0, "end")
        self.entry.insert(0, self._placeholder)
        self._has_placeholder = True

    def _focus_in(self, _event: tk.Event) -> None:
        if self._has_placeholder:
            self.entry.delete(0, "end")
            self.entry.configure(fg=self.palette.text)
            self._has_placeholder = False
        self._draw()

    def _focus_out(self, _event: tk.Event) -> None:
        if not self.entry.get():
            self._show_placeholder()
        self._draw()

    def _resize(self, _event: tk.Event) -> None:
        self.coords(self._window, 12, self.winfo_height() // 2)
        self.itemconfigure(self._window, width=max(60, self.winfo_width() - 24))
        self._draw()

    def _draw(self) -> None:
        self.delete("bg")
        width = self.winfo_width() or int(self["width"])
        height = self.winfo_height() or self._height
        if width <= 4:
            return
        focused = bool(self.focus_get() is self.entry)
        border = self.palette.accent if focused else self.palette.border
        round_rect(
            self, 1, 1, width - 1, height - 1, 9,
            fill=self.palette.panel_alt, outline=border, width=1, tags="bg",
        )
        self.tag_lower("bg")


class Pill(tk.Label):
    def __init__(
        self,
        master: tk.Misc,
        text: str,
        *,
        palette: Palette,
        fonts: Fonts,
        tone: str = "neutral",
    ) -> None:
        self.palette = palette
        self._tone = tone
        bg, fg = self._tone_colors(tone)
        super().__init__(
            master, text=text, font=fonts.small_bold, bg=bg, fg=fg,
            padx=9, pady=2, bd=0, highlightthickness=0,
        )

    def _tone_colors(self, tone: str) -> tuple:
        p = self.palette
        return {
            "accent": (p.accent_soft, p.accent),
            "success": (p.success_soft, p.success),
            "danger": (p.danger_soft, p.danger_text),
            "warn": (p.warn_soft, p.warn),
            "neutral": (p.panel_alt, p.muted),
        }.get(tone, (p.panel_alt, p.muted))

    def set(self, text: str, tone: Optional[str] = None) -> None:
        if tone and tone != self._tone:
            self._tone = tone
            bg, fg = self._tone_colors(tone)
            self.configure(bg=bg, fg=fg)
        self.configure(text=text)


class Card(tk.Frame):
    """A bordered surface used for the table and the detail rail."""

    def __init__(self, master: tk.Misc, *, palette: Palette, bg: Optional[str] = None, **kwargs) -> None:
        super().__init__(
            master,
            bg=bg or palette.panel,
            highlightbackground=palette.border,
            highlightcolor=palette.border,
            highlightthickness=1,
            bd=0,
            **kwargs,
        )


class Divider(tk.Frame):
    def __init__(self, master: tk.Misc, *, palette: Palette) -> None:
        super().__init__(master, bg=palette.border, height=1)


# ---------------------------------------------------------------------------
# Toast notifications
# ---------------------------------------------------------------------------

class Toast:
    """A single, self-dismissing notification anchored to the bottom right."""

    def __init__(self, master: tk.Misc, *, palette: Palette, fonts: Fonts) -> None:
        self.master = master
        self.palette = palette
        self.fonts = fonts
        self._frame: Optional[tk.Frame] = None
        self._after_id: Optional[str] = None

    def show(
        self,
        message: str,
        *,
        tone: str = "success",
        action_label: str = "",
        action: Optional[Callable[[], None]] = None,
        timeout_ms: int = 7000,
    ) -> None:
        self.hide()
        p = self.palette
        accent = {"success": p.success, "danger": p.danger, "info": p.accent}.get(tone, p.accent)
        frame = tk.Frame(
            self.master,
            bg=p.panel,
            highlightbackground=p.border_strong,
            highlightcolor=p.border_strong,
            highlightthickness=1,
            bd=0,
        )
        bar = tk.Frame(frame, bg=accent, width=4)
        bar.pack(side="left", fill="y")
        inner = tk.Frame(frame, bg=p.panel)
        inner.pack(side="left", fill="both", expand=True, padx=(14, 12), pady=11)
        label = tk.Label(
            inner,
            text=message,
            bg=p.panel,
            fg=p.text,
            font=self.fonts.body,
            justify="left",
            anchor="w",
            wraplength=360,
        )
        label.pack(side="left", fill="x", expand=True)
        if action and action_label:
            button = CanvasButton(
                inner,
                action_label,
                command=lambda: (self.hide(), action()),
                palette=p,
                fonts=self.fonts,
                kind="link",
                padx=6,
                height=24,
                font=self.fonts.small_bold,
                bg=p.panel,
            )
            button.pack(side="left", padx=(10, 0))
        self._frame = frame
        frame.place(relx=1.0, rely=1.0, x=-26, y=-58, anchor="se")
        frame.lift()
        if timeout_ms > 0:
            self._after_id = self.master.after(timeout_ms, self.hide)

    def hide(self) -> None:
        if self._after_id is not None:
            try:
                self.master.after_cancel(self._after_id)
            except tk.TclError:
                pass
            self._after_id = None
        if self._frame is not None:
            self._frame.destroy()
            self._frame = None


# ---------------------------------------------------------------------------
# Dialogs
# ---------------------------------------------------------------------------

class Dialog(tk.Toplevel):
    """Base class: themed, modal, centred, Escape closes."""

    def __init__(self, master: tk.Misc, *, palette: Palette, fonts: Fonts, title: str) -> None:
        super().__init__(master, bg=palette.bg)
        self.palette = palette
        self.fonts = fonts
        self.withdraw()
        self.title(title)
        self.configure(bg=palette.bg)
        self.resizable(False, False)
        self.transient(master)
        self.protocol("WM_DELETE_WINDOW", self.cancel)
        self.bind("<Escape>", lambda _e: self.cancel())
        self._shell = tk.Frame(self, bg=palette.bg, padx=26, pady=22)
        self._shell.pack(fill="both", expand=True)

    def _centre(self) -> None:
        self.update_idletasks()
        master = self.master
        try:
            mx, my = master.winfo_rootx(), master.winfo_rooty()
            mw, mh = master.winfo_width(), master.winfo_height()
        except tk.TclError:
            mx = my = 0
            mw, mh = self.winfo_screenwidth(), self.winfo_screenheight()
        width, height = self.winfo_reqwidth(), self.winfo_reqheight()
        x = mx + max(0, (mw - width) // 2)
        y = my + max(0, (mh - height) // 3)
        self.geometry(f"+{x}+{y}")

    def present(self) -> None:
        self._centre()
        self.deiconify()
        self.lift()
        self.focus_force()
        self.grab_set()

    def cancel(self) -> None:
        self.grab_release()
        self.destroy()


class MessageDialog(Dialog):
    """Informational dialog (about, errors, warnings)."""

    def __init__(
        self,
        master: tk.Misc,
        *,
        palette: Palette,
        fonts: Fonts,
        title: str,
        heading: str,
        message: str,
        details: str = "",
        tone: str = "info",
        primary_label: str = "Close",
        secondary_label: str = "",
        secondary: Optional[Callable[[], None]] = None,
        image: Optional[tk.PhotoImage] = None,
    ) -> None:
        super().__init__(master, palette=palette, fonts=fonts, title=title)
        p, f = palette, fonts
        accent = {"info": p.accent, "warning": p.warn, "danger": p.danger, "success": p.success}.get(tone, p.accent)

        header = tk.Frame(self._shell, bg=p.bg)
        header.pack(fill="x")
        if image is not None:
            art = tk.Label(header, image=image, bg=p.bg, bd=0)
            art.image = image  # keep a reference alive for Tk
            art.pack(side="left", padx=(0, 14))
        else:
            badge = tk.Canvas(header, width=34, height=34, bg=p.bg, highlightthickness=0)
            badge.pack(side="left", padx=(0, 12))
            round_rect(badge, 2, 2, 32, 32, 9, fill=p.accent_soft if tone == "info" else p.warn_soft, outline="")
            badge.create_text(
                17, 17, text={"info": "i", "warning": "!", "danger": "!", "success": "\u2713"}.get(tone, "i"),
                fill=accent, font=(f.ui, 15, "bold"),
            )
        tk.Label(
            header, text=heading, bg=p.bg, fg=p.text, font=f.heading, anchor="w", justify="left",
        ).pack(side="left", fill="x", expand=True)

        tk.Label(
            self._shell, text=message, bg=p.bg, fg=p.muted, font=f.body,
            justify="left", anchor="w", wraplength=440,
        ).pack(fill="x", pady=(14, 0))

        if details:
            box = tk.Frame(self._shell, bg=p.panel, highlightbackground=p.border, highlightthickness=1)
            box.pack(fill="x", pady=(14, 0))
            tk.Label(
                box, text=details, bg=p.panel, fg=p.muted, font=f.mono_small,
                justify="left", anchor="w", wraplength=420, padx=12, pady=10,
            ).pack(fill="x")

        footer = tk.Frame(self._shell, bg=p.bg)
        footer.pack(fill="x", pady=(20, 0))
        CanvasButton(
            footer, primary_label, command=self.cancel, palette=p, fonts=f, kind="primary",
        ).pack(side="right")
        if secondary_label and secondary:
            CanvasButton(
                footer, secondary_label,
                command=lambda: (self.cancel(), secondary()),
                palette=p, fonts=f, kind="ghost",
            ).pack(side="right", padx=(0, 8))


class ConfirmDeleteDialog(Dialog):
    """Final confirmation before touching ``~/.ssh``."""

    def __init__(
        self,
        master: tk.Misc,
        *,
        palette: Palette,
        fonts: Fonts,
        hosts: Sequence[str],
        target_label: str,
        checkbox_label: str = "",
        checkbox_default: bool = True,
        extra_note: str = "",
        confirm_label: str = "",
    ) -> None:
        super().__init__(master, palette=palette, fonts=fonts, title="Confirm removal")
        p, f = palette, fonts
        self.result = False
        self.checked = checkbox_default
        self._var = tk.BooleanVar(value=checkbox_default)

        header = tk.Frame(self._shell, bg=p.bg)
        header.pack(fill="x")
        badge = tk.Canvas(header, width=34, height=34, bg=p.bg, highlightthickness=0)
        badge.pack(side="left", padx=(0, 12))
        round_rect(badge, 2, 2, 32, 32, 9, fill=p.danger_soft, outline="")
        badge.create_text(17, 17, text="!", fill=p.danger, font=(f.ui, 15, "bold"))
        tk.Label(
            header, text=f"Remove {target_label}?", bg=p.bg, fg=p.text,
            font=f.heading, anchor="w",
        ).pack(side="left")

        tk.Label(
            self._shell,
            text="These entries will be deleted from your SSH files. A timestamped "
                 "backup is written first, so the change can be undone.",
            bg=p.bg, fg=p.muted, font=f.body, justify="left", anchor="w", wraplength=460,
        ).pack(fill="x", pady=(14, 0))

        box = tk.Frame(self._shell, bg=p.panel, highlightbackground=p.border, highlightthickness=1)
        box.pack(fill="x", pady=(14, 0))
        visible = list(hosts)[:6]
        listing = "\n".join(f"\u2022  {name}" for name in visible)
        if len(hosts) > len(visible):
            listing += f"\n   and {len(hosts) - len(visible)} more"
        tk.Label(
            box, text=listing, bg=p.panel, fg=p.text, font=f.mono_small,
            justify="left", anchor="w", padx=14, pady=11,
        ).pack(fill="x")

        if checkbox_label:
            check = CheckRow(
                self._shell, checkbox_label, palette=p, fonts=f,
                variable=self._var, bg=p.bg, wraplength=440,
            )
            check.pack(fill="x", pady=(14, 0))

        if extra_note:
            tk.Label(
                self._shell, text=extra_note, bg=p.bg, fg=p.warn, font=f.small,
                justify="left", anchor="w", wraplength=460,
            ).pack(fill="x", pady=(10, 0))

        footer = tk.Frame(self._shell, bg=p.bg)
        footer.pack(fill="x", pady=(22, 0))
        CanvasButton(
            footer, confirm_label or f"Remove {target_label}",
            command=self._confirm, palette=p, fonts=f, kind="danger",
        ).pack(side="right")
        CanvasButton(
            footer, "Cancel", command=self.cancel, palette=p, fonts=f, kind="ghost",
        ).pack(side="right", padx=(0, 8))

        self.bind("<Return>", lambda _e: self._confirm())

    def _confirm(self) -> None:
        self.result = True
        self.checked = bool(self._var.get())
        self.grab_release()
        self.destroy()

    def ask(self) -> bool:
        """Show modally; returns True when the user confirmed."""
        self.present()
        self.master.wait_window(self)
        return bool(self.result)


class ConfigBlockDialog(Dialog):
    """Editor for a single ssh_config Host block.

    ``on_save(block, patterns, values)`` performs the write and returns an error
    message, or ``None`` on success. The dialog stays open on errors so the user
    never loses their input.
    """

    FIELDS = (
        ("host", "Host", "alias, alias2", False),
        ("hostname", "HostName", "server.example.com", True),
        ("user", "User", "root", False),
        ("port", "Port", "22", True),
        ("identityfile", "IdentityFile", "~/.ssh/id_ed25519", True),
        ("proxyjump", "ProxyJump", "bastion.example.com", True),
    )

    def __init__(
        self,
        master: tk.Misc,
        *,
        palette: Palette,
        fonts: Fonts,
        block,
        on_save: Callable[[object, Sequence[str], Dict[str, str]], Optional[str]],
    ) -> None:
        super().__init__(master, palette=palette, fonts=fonts, title="Edit Host block")
        p, f = palette, fonts
        self.block = block
        self._on_save = on_save
        self._fields: Dict[str, TextField] = {}

        header = tk.Frame(self._shell, bg=p.bg)
        header.pack(fill="x")
        badge = tk.Canvas(header, width=34, height=34, bg=p.bg, highlightthickness=0)
        badge.pack(side="left", padx=(0, 12))
        round_rect(badge, 2, 2, 32, 32, 9, fill=p.accent_soft, outline="")
        badge.create_text(17, 17, text="\u270e", fill=p.accent, font=(f.ui, 15))
        title_block = tk.Frame(header, bg=p.bg)
        title_block.pack(side="left", fill="x", expand=True)
        tk.Label(
            title_block, text=f"Edit {block.label}", bg=p.bg, fg=p.text,
            font=f.heading, anchor="w",
        ).pack(anchor="w")
        tk.Label(
            title_block,
            text=f"{block.source.name} \u00b7 lines {block.start_line}\u2013{block.end_line}",
            bg=p.bg, fg=p.muted, font=f.mono_small, anchor="w",
        ).pack(anchor="w")

        form = tk.Frame(self._shell, bg=p.bg)
        form.pack(fill="x", pady=(18, 0))
        form.columnconfigure(1, weight=1)
        initial = {
            "host": " ".join(block.patterns),
            "hostname": block.option("hostname") or "",
            "user": block.option("user") or "",
            "port": block.option("port") or "",
            "identityfile": block.option("identityfile") or "",
            "proxyjump": block.option("proxyjump") or "",
        }
        self._initial = initial
        for row, (key, label, placeholder, mono) in enumerate(self.FIELDS):
            tk.Label(
                form, text=label, bg=p.bg, fg=p.muted, font=f.small,
                anchor="w", width=12,
            ).grid(row=row, column=0, sticky="w", pady=4)
            field = TextField(
                form, palette=p, fonts=f, placeholder=placeholder,
                mono=mono, height=34, width=380,
            )
            field.set(initial[key])
            field.grid(row=row, column=1, sticky="ew", pady=4)
            self._fields[key] = field

        self._error = tk.Label(
            self._shell, text="", bg=p.bg, fg=p.danger, font=f.small,
            anchor="w", justify="left", wraplength=460,
        )
        self._error.pack(fill="x", pady=(10, 0))

        others = [
            key for key, _value in block.options
            if key.lower() not in {"hostname", "user", "port", "identityfile", "proxyjump"}
        ]
        if others:
            unique = list(dict.fromkeys(name for name in others))
            tk.Label(
                self._shell,
                text=f"Also preserved: {', '.join(unique[:4])}"
                     + (" and more" if len(unique) > 4 else "") + ".",
                bg=p.bg, fg=p.faint, font=f.tiny, anchor="w", wraplength=460,
            ).pack(fill="x", pady=(2, 0))

        footer = tk.Frame(self._shell, bg=p.bg)
        footer.pack(fill="x", pady=(20, 0))
        CanvasButton(
            footer, "Save changes", command=self._save, palette=p, fonts=f, kind="primary",
        ).pack(side="right")
        CanvasButton(
            footer, "Cancel", command=self.cancel, palette=p, fonts=f, kind="ghost",
        ).pack(side="right", padx=(0, 8))
        self.bind("<Return>", lambda _e: self._save())

    def present(self) -> None:
        super().present()
        self._fields["host"].focus()

    # -- validation ------------------------------------------------------
    def _collect(self) -> tuple:
        patterns = [
            token for token in self._fields["host"].get().replace(",", " ").split()
            if token
        ]
        values = {
            key: self._fields[key].get().strip()
            for key, _label, _placeholder, _mono in self.FIELDS
            if key != "host"
        }
        return patterns, values

    def _validate(self, patterns: Sequence[str], values: Dict[str, str]) -> str:
        if not patterns:
            return "Add at least one Host pattern."
        for pattern in patterns:
            if any(ch in pattern for ch in "*?!"):
                return "Wildcards and negations are not allowed in an edited block."
            if any(ch.isspace() for ch in pattern):
                return "Host patterns cannot contain spaces."
        if values["port"]:
            if not values["port"].isdigit() or not 1 <= int(values["port"]) <= 65535:
                return "Port must be a number between 1 and 65535."
        labels = {
            "hostname": "HostName", "user": "User",
            "identityfile": "IdentityFile", "proxyjump": "ProxyJump",
        }
        for key, label in labels.items():
            if any(ch.isspace() for ch in values[key]):
                return f"{label} cannot contain spaces."
        return ""

    def _save(self) -> None:
        patterns, values = self._collect()
        error = self._validate(patterns, values)
        if not error and patterns == self._initial["host"].split() and values == {
            key: self._initial[key] for key in values
        }:
            self.cancel()
            return
        if not error:
            error = self._on_save(self.block, patterns, values) or ""
        if error:
            self._error.configure(text=error)
            return
        self.grab_release()
        self.destroy()


class SettingsDialog(Dialog):
    """Preferences: appearance and removal behaviour."""

    def __init__(
        self,
        master: tk.Misc,
        *,
        palette: Palette,
        fonts: Fonts,
        theme: str,
        confirm_before_delete: bool,
        remove_config_default: bool,
        ssh_dir: str,
        backup_dir: str,
        backup_count: int,
        on_reveal_backups: Optional[Callable[[], None]] = None,
    ) -> None:
        super().__init__(master, palette=palette, fonts=fonts, title="Settings")
        p, f = palette, fonts
        self.result: Optional[Dict[str, object]] = None
        self._theme = theme
        self._confirm_var = tk.BooleanVar(value=confirm_before_delete)
        self._config_var = tk.BooleanVar(value=remove_config_default)
        self._theme_buttons: Dict[str, CanvasButton] = {}

        tk.Label(
            self._shell, text="Settings", bg=p.bg, fg=p.text, font=f.title, anchor="w",
        ).pack(fill="x")
        tk.Label(
            self._shell, text="Preferences for this Mac. Files in ~/.ssh are never "
                              "changed by these options.",
            bg=p.bg, fg=p.muted, font=f.small, anchor="w", wraplength=460,
        ).pack(fill="x", pady=(4, 0))

        # -- appearance ---------------------------------------------------
        self._section("Appearance")
        row = tk.Frame(self._shell, bg=p.bg)
        row.pack(fill="x")
        for key, label in (("system", "System"), ("dark", "Dark"), ("light", "Light")):
            button = CanvasButton(
                row, label, command=lambda k=key: self._select_theme(k),
                palette=p, fonts=f, kind="primary" if key == self._theme else "ghost",
                height=34, width=98, bg=p.bg,
            )
            button.pack(side="left", padx=(0, 8))
            self._theme_buttons[key] = button
        tk.Label(
            row, text="System follows the OS appearance.", bg=p.bg, fg=p.faint,
            font=f.tiny,
        ).pack(side="left", padx=(6, 0))

        # -- removal ------------------------------------------------------
        self._section("Removal")
        CheckRow(
            self._shell, "Ask for confirmation before removing entries",
            palette=p, fonts=f, variable=self._confirm_var, bg=p.bg, wraplength=440,
        ).pack(fill="x", pady=(0, 8))
        CheckRow(
            self._shell,
            "Also remove matching ssh_config Host blocks by default",
            palette=p, fonts=f, variable=self._config_var, bg=p.bg, wraplength=440,
        ).pack(fill="x")
        tk.Label(
            self._shell,
            text="A timestamped backup is written before every change, regardless "
                 "of these options.",
            bg=p.bg, fg=p.faint, font=f.tiny, anchor="w", wraplength=440,
        ).pack(fill="x", pady=(6, 0))

        # -- storage ------------------------------------------------------
        self._section("Storage")
        self._path_row("SSH folder", ssh_dir, None)
        self._path_row(
            f"Backups ({backup_count})", backup_dir, on_reveal_backups, "Reveal",
        )

        footer = tk.Frame(self._shell, bg=p.bg)
        footer.pack(fill="x", pady=(22, 0))
        CanvasButton(
            footer, "Save changes", command=self._save, palette=p, fonts=f, kind="primary",
        ).pack(side="right")
        CanvasButton(
            footer, "Cancel", command=self.cancel, palette=p, fonts=f, kind="ghost",
        ).pack(side="right", padx=(0, 8))
        self.bind("<Return>", lambda _e: self._save())

    # ------------------------------------------------------------------
    def _section(self, title: str) -> None:
        p, f = self.palette, self.fonts
        tk.Label(
            self._shell, text=title.upper(), bg=p.bg, fg=p.faint,
            font=(f.ui, 9, "bold"), anchor="w",
        ).pack(fill="x", pady=(18, 6))

    def _path_row(
        self,
        label: str,
        value: str,
        action: Optional[Callable[[], None]],
        action_label: str = "",
    ) -> None:
        p, f = self.palette, self.fonts
        row = tk.Frame(self._shell, bg=p.bg)
        row.pack(fill="x", pady=2)
        tk.Label(
            row, text=label, bg=p.bg, fg=p.muted, font=f.small, width=14, anchor="w",
        ).pack(side="left", anchor="n")
        tk.Label(
            row, text=value, bg=p.bg, fg=p.text, font=f.tiny,
            anchor="w", justify="left", wraplength=300,
        ).pack(side="left", fill="x", expand=True)
        if action and action_label:
            CanvasButton(
                row, action_label, command=action, kind="link", palette=p, fonts=f,
                bg=p.bg, height=20, padx=4, font=f.small_bold,
            ).pack(side="right", anchor="n")

    def _select_theme(self, theme: str) -> None:
        self._theme = theme
        for key, button in self._theme_buttons.items():
            button.set_kind("primary" if key == theme else "ghost")

    def _save(self) -> None:
        self.result = {
            "theme": self._theme,
            "confirm_before_delete": bool(self._confirm_var.get()),
            "remove_config_default": bool(self._config_var.get()),
        }
        self.grab_release()
        self.destroy()
