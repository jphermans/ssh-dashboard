"""End-to-end probe for editing a Host block against a throwaway HOME."""
from __future__ import annotations

import os
import shutil
import sys
import tempfile
from pathlib import Path

real = Path.home() / ".ssh"
root = Path(tempfile.mkdtemp(prefix="sshdash-edit-"))
ssh = root / ".ssh"
ssh.mkdir()
shutil.copy2(real / "config", ssh / "config")
shutil.copy2(real / "known_hosts", ssh / "known_hosts")
os.environ["HOME"] = str(root)

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import tkinter as tk  # noqa: E402
from sshdash.config import ActivityLog  # noqa: E402
from sshdash.ui.app import DashboardApp  # noqa: E402
from sshdash.ui.widgets import ConfigBlockDialog  # noqa: E402

app = DashboardApp()
app.update()
app._switch_view("config")
app.update()

editable = next(row for row in app.rows if row["payload"].deletable)
block = editable["payload"]
print("editing:", block.label, "lines", block.start_line, "-", block.end_line)
before = (ssh / "config").read_text()

# open the editor exactly like a double-click would
app._open_config_editor(block)
app.update()
dialogs = [child for child in app.winfo_children() if isinstance(child, tk.Toplevel)]
assert dialogs, "editor dialog did not open"
dialog = dialogs[-1]
assert isinstance(dialog, ConfigBlockDialog)

print("prefilled host:", dialog._fields["host"].get())
dialog._fields["host"].set("edited-alias")
dialog._fields["hostname"].set("edited.example.com")
dialog._fields["user"].set("deploy")
dialog._fields["port"].set("not-a-port")
dialog._save()
app.update()
print("validation error:", dialog._error.cget("text"))
assert "Port" in dialog._error.cget("text")

dialog._fields["port"].set("2222")
dialog._fields["identityfile"].set("~/.ssh/id_ed25519")
dialog._save()
app.update()
after = (ssh / "config").read_text()
assert "Host edited-alias" in after, after
assert "HostName edited.example.com" in after
assert "User deploy" in after
assert "Port 2222" in after
assert "Host edited-alias" not in before
assert app.refresh  # sanity
print("file rewritten, block count:", len([b for b in app.snapshot.config_blocks if b.editable]))
print("row exists:", any("edited-alias" in row["values"][1] for row in app.rows))

# undo restores the original
backups = app.store.backups()
app._undo(backups[:1])
app.update()
restored = (ssh / "config").read_text()
print("undo restored:", restored == before)
print("activity tail:", ActivityLog().entries(3)[:2])

app._on_close()
shutil.rmtree(root, ignore_errors=True)
print("edit probe finished")
