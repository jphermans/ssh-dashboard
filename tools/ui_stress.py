"""Stress the UI paths a user actually hits, against a throwaway HOME."""
from __future__ import annotations

import os
import shutil
import sys
import tempfile
import threading
from pathlib import Path

real = Path.home() / ".ssh"
root = Path(tempfile.mkdtemp(prefix="sshdash-stress-"))
ssh = root / ".ssh"
ssh.mkdir()
for name in ("known_hosts", "config", "id_ed25519.pub"):
    if (real / name).exists():
        shutil.copy2(real / name, ssh / name)
os.environ["HOME"] = str(root)

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import tkinter as tk  # noqa: E402
from sshdash.ui.app import DashboardApp  # noqa: E402

watchdog = threading.Timer(45, lambda: (print("WATCHDOG: hung", flush=True), os._exit(3)))
watchdog.daemon = True
watchdog.start()

app = DashboardApp()
app.update()
print("started", app.winfo_geometry(), flush=True)

# native menus, posted and unposted without a tracking loop
from sshdash.ui.widgets import CanvasButton  # noqa: E402

p = app.palette
menu = tk.Menu(app, tearoff=0, bg=p.panel, fg=p.text, activebackground=p.accent_soft,
               activeforeground=p.accent, font=app.fonts.body)
menu.add_command(label="Rescan SSH files")
menu.add_command(label="Open ~/.ssh")
menu.add_separator()
menu.add_command(label="About SSH Dashboard")
menu.post(app.winfo_rootx() + 60, app.winfo_rooty() + 60)
app.update()
menu.unpost()
app.update()
print("menu post/unpost ok", flush=True)

# dialogs opened and cancelled repeatedly
for index in range(3):
    app._about()
    app.update()
    for child in app.winfo_children():
        if isinstance(child, tk.Toplevel):
            child.cancel()
    app.update()
print("about dialogs ok", flush=True)

# rapid view + theme + sort + resize churn
for cycle in range(4):
    for view in ("config", "keys", "activity", "hosts"):
        app._switch_view(view)
        app.update()
    app._toggle_theme()
    app.update()
    app.geometry(f"{1200 + cycle * 40}x{760 + cycle * 20}")
    app.update()
    app._sort_by("label")
    app.update()
print("churn ok; theme:", app.palette.key, flush=True)

# search typing simulation and selection of every row
app._switch_view("hosts")
app.search.focus()
app.update()
for char in "192.168":
    app.search.entry.insert("end", char)
    app.search._on_key(None)
    app.update()
print("search rows:", len(app.rows), flush=True)
app._clear_search()
app._toggle_all()
app.update()
print("selected:", len(app.selected["hosts"]), flush=True)
app._clear_selection()
app.update()

# keyboard bindings fire without a focused entry
app.tree.focus(app.rows[0]["iid"])
app._key_toggle()
app.update()
print("space toggled:", len(app.selected["hosts"]), flush=True)
app._key_escape()
app.update()
app._copy("ssh test-host", "Command")
app.update()
print("clipboard:", app.clipboard_get(), flush=True)

print("stress finished", flush=True)
watchdog.cancel()
app.destroy()
shutil.rmtree(root, ignore_errors=True)
