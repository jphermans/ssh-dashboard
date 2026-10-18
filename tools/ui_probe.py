"""Drive the real app against a throwaway HOME, including a full delete flow.

Usage: python3.14 tools/ui_probe.py
"""
from __future__ import annotations

import os
import shutil
import sys
import tempfile
import traceback
from pathlib import Path

REAL_SSH = Path.home() / ".ssh"
root = Path(tempfile.mkdtemp(prefix="sshdash-probe-"))
ssh = root / ".ssh"
ssh.mkdir(mode=0o700)
shutil.copy2(REAL_SSH / "known_hosts", ssh / "known_hosts")
shutil.copy2(REAL_SSH / "config", ssh / "config")
if (REAL_SSH / "id_ed25519.pub").exists():
    shutil.copy2(REAL_SSH / "id_ed25519.pub", ssh / "id_ed25519.pub")
os.chmod(ssh / "known_hosts", 0o600)
os.chmod(ssh / "config", 0o600)
os.environ["HOME"] = str(root)

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from sshdash.ui.app import DashboardApp  # noqa: E402
from sshdash.store import SshStore  # noqa: E402

app = DashboardApp()
app.update()
print("window:", app.winfo_geometry())
print("rows:", len(app.rows), "| title:", app.title_label.cget("text"))

# 1. nav through every view via the actual NavItem handler
for view in ("config", "keys", "activity", "hosts"):
    app.nav_items[view]._click(None)
    app.update()
    print(f"view {view}: rows={len(app.rows)} table_w={app.tree.winfo_width()}")

# 2. click the checkbox cell of the first row with a synthetic mouse event
iid = app.rows[0]["iid"]
bbox = app.tree.bbox(iid, "select")
print("bbox:", bbox)
if bbox:
    x, y, w, h = bbox
    app.tree.event_generate("<Button-1>", x=x + w // 2, y=y + h // 2)
    app.update()
    print("after click selected:", app.selected["hosts"], "| delete enabled:", app.delete_btn._enabled)

# 3. change sort by clicking the heading, and search
app._sort_by("address")
app.update()
app.search.set("192.168")
app.update()
print("search rows:", len(app.rows))
app._clear_search()
app.update()

# 4. full delete with the modal dialog driven from the event loop
target = app.rows[0]
label = target["payload"].label if hasattr(target["payload"], "label") else "?"
print("deleting:", label)
original_lines = (ssh / "known_hosts").read_text().count("\n")

def confirm() -> None:
    for child in app.winfo_children():
        if child.winfo_class() == "Toplevel":
            try:
                child._confirm()
                print("dialog confirmed")
            except Exception:
                traceback.print_exc()

app.after(400, confirm)
app._delete_selected()
app.update()
print("after delete selected:", app.selected["hosts"], "| rows:", len(app.rows))
new_text = (ssh / "known_hosts").read_text()
print("known_hosts lines:", original_lines, "->", new_text.count("\n"))
print("backups:", [p.name for p in SshStore(ssh).backups()])

# 5. undo through the store, then theme round-trip
backups = SshStore(ssh).backups()
if backups:
    SshStore(ssh).restore_backup(backups[0])
    app.refresh()
    app.update()
    print("after undo rows:", len(app.rows))
app._toggle_theme()
app.update()
print("theme:", app.palette.key)
app._toggle_theme()
app.update()
print("theme:", app.palette.key)

# 6. dialog construction (does not touch files)
app._about()
app.update()
for child in app.winfo_children():
    if child.winfo_class() == "Toplevel":
        child.cancel()
app.update()
print("all probe steps finished")
app.destroy()
shutil.rmtree(root, ignore_errors=True)
