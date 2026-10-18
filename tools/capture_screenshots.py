"""Capture real screenshots of the app with fictional demo data.

Runs the actual Tk window, then grabs its own NSWindow through CoreGraphics —
no Screen Recording permission is needed because a process may capture its own
windows.

    .venv-build/bin/python tools/capture_screenshots.py

Writes PNGs (Retina resolution) to docs/screenshots/.
"""

from __future__ import annotations

import base64
import os
import shutil
import subprocess
import sys
import tempfile
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "docs" / "screenshots"
OUT.mkdir(parents=True, exist_ok=True)

# Preserve the developer's real appearance preference across the capture.
SETTINGS = Path.home() / "Library" / "Application Support" / "SSHDashboard" / "settings.json"
_ORIGINAL_SETTINGS = SETTINGS.read_bytes() if SETTINGS.exists() else None

DEMO = Path(tempfile.mkdtemp(prefix="sshdash-demo-"))
SSH = DEMO / ".ssh"
SSH.mkdir(mode=0o700)


def key_blob() -> str:
    return base64.b64encode(os.urandom(48)).decode()


def make_key(name: str, comment: str, passphrase: str = "", kind: str = "ed25519") -> None:
    path = SSH / name
    subprocess.run(
        ["ssh-keygen", "-q", "-t", kind, "-N", passphrase, "-C", comment, "-f", str(path)],
        check=True,
        capture_output=True,
        timeout=30,
    )
    os.chmod(path, 0o600)


# ---------------------------------------------------------------- demo data
print("preparing demo ssh folder…")
hosts = [
    ("prod-eu", "eu-west.example.com", "deploy", 2222),
    ("staging", "staging.example.com", "deploy", 22),
    ("gitea", "git.example.com", "git", 22),
    ("nas", "192.168.1.20", "admin", 22),
    ("pi-lab", "192.168.1.42", "pi", 22),
    ("db-primary", "db1.internal.example.com", "dba", 22),
]
extra = [
    ("github.com", "github.com", None, 22),
    ("gitlab.com", "gitlab.com", None, 22),
    ("mirror.example.net", "mirror.example.net", None, 22),
    ("vpn.example.org", "vpn.example.org", None, 22),
    ("192.168.1.10", "192.168.1.10", None, 22),
    ("192.168.1.11", "192.168.1.11", None, 22),
    ("192.168.1.31", "192.168.1.31", None, 22),
]

config_lines = [
    "# Global defaults",
    "Host *",
    "    ServerAliveInterval 60",
    "    AddKeysToAgent yes",
    "    UseKeychain yes",
    "",
]
known_lines = []
for alias, hostname, user, port in hosts:
    config_lines += [f"Host {alias}", f"    HostName {hostname}"]
    if user:
        config_lines.append(f"    User {user}")
    if port != 22:
        config_lines.append(f"    Port {port}")
    if alias in ("prod-eu", "staging", "gitea"):
        config_lines.append(f"    IdentityFile ~/.ssh/{'deploy_ed25519' if alias != 'gitea' else 'gitea_key'}")
    if alias in ("prod-eu", "gitea"):
        config_lines.append("    IdentitiesOnly yes")
    config_lines.append("")
    for key_type in ("ssh-ed25519", "ecdsa-sha2-nistp256"):
        known_lines.append(f"{hostname} {key_type} {key_blob()}")

for alias, hostname, _user, _port in extra:
    known_lines.append(f"{hostname} ssh-ed25519 {key_blob()}")
    known_lines.append(f"{hostname} ssh-rsa {key_blob()}")

# a shared, multi-token line and a revoked marker for realism
known_lines.append(f"mirror.example.net,cdn.example.net ssh-ed25519 {key_blob()}")
known_lines.append(f"@revoked old.example.com ssh-rsa {key_blob()}")

(SSH / "config").write_text("\n".join(config_lines).rstrip() + "\n")
(SSH / "known_hosts").write_text("\n".join(known_lines) + "\n")
os.chmod(SSH / "config", 0o600)
os.chmod(SSH / "known_hosts", 0o600)

make_key("id_ed25519", "jp@studio")
make_key("deploy_ed25519", "deploy@ci")
make_key("gitea_key", "git@example")
make_key("id_rsa", "jp@studio (legacy)", kind="rsa")
make_key("backup_ed25519", "backup@vault", passphrase="demo")

settings_dir = DEMO / "Library" / "Application Support" / "SSHDashboard"
settings_dir.mkdir(parents=True)
(settings_dir / "settings.json").write_text(
    '{"theme": "dark", "last_view": "hosts", "window": "1400x900+80+60", '
    '"confirm_before_delete": true, "remove_config_default": true}'
)

os.environ["HOME"] = str(DEMO)
sys.path.insert(0, str(ROOT))

import Quartz  # noqa: E402
from CoreFoundation import CFURLCreateWithFileSystemPath, kCFURLPOSIXPathStyle  # noqa: E402

from sshdash.ui.app import DashboardApp  # noqa: E402


def windows_for_pid(pid: int) -> list:
    infos = Quartz.CGWindowListCopyWindowInfo(
        Quartz.kCGWindowListOptionOnScreenOnly, Quartz.kCGNullWindowID
    )
    return [
        info for info in infos
        if info.get("kCGWindowOwnerPID") == pid
        and info.get("kCGWindowLayer") == 0
        and info.get("kCGWindowBounds", {}).get("Width", 0) > 200
    ]


def capture(window: dict, filename: str) -> None:
    image = Quartz.CGWindowListCreateImage(
        Quartz.CGRectNull,
        Quartz.kCGWindowListOptionIncludingWindow,
        window["kCGWindowNumber"],
        Quartz.kCGWindowImageBoundsIgnoreFraming | Quartz.kCGWindowImageBestResolution,
    )
    if image is None:
        raise RuntimeError(f"capture failed for {window.get('kCGWindowName')!r}")
    path = OUT / filename
    url = CFURLCreateWithFileSystemPath(None, str(path), kCFURLPOSIXPathStyle, False)
    destination = Quartz.CGImageDestinationCreateWithURL(url, "public.png", 1, None)
    Quartz.CGImageDestinationAddImage(destination, image, None)
    if not Quartz.CGImageDestinationFinalize(destination):
        raise RuntimeError(f"could not write {path}")
    size = Quartz.CGImageGetWidth(image), Quartz.CGImageGetHeight(image)
    print(f"  {filename}  {size[0]}x{size[1]}  {path.stat().st_size // 1024} KB")


app = DashboardApp()
app.update()
app.geometry("1500x900+60+60")
app.update()
time.sleep(0.8)


def settle(seconds: float = 0.5) -> None:
    app.update()
    time.sleep(seconds)
    app.update()


def main_window() -> dict:
    candidates = [w for w in windows_for_pid(os.getpid()) if w["kCGWindowBounds"]["Width"] > 900]
    return max(candidates, key=lambda w: w["kCGWindowBounds"]["Width"] * w["kCGWindowBounds"]["Height"])


def dialog_window() -> dict:
    candidates = [w for w in windows_for_pid(os.getpid()) if w["kCGWindowBounds"]["Width"] < 900]
    if not candidates:
        raise RuntimeError("dialog window not found")
    return candidates[-1]


print("capturing…")

# 1 — dark known hosts with a few rows ticked and details showing
app._set_theme("dark")
app._switch_view("hosts")
ticks = [row["iid"] for row in app.rows if row["values"][1] in ("prod-eu", "gitea")]
app.selected["hosts"].update(ticks)
app._render_table()
target = next(row["iid"] for row in app.rows if row["values"][1] == "prod-eu")
app.tree.selection_set(target)
app.tree.focus(target)
settle()
capture(main_window(), "hosts-dark.png")

# 2 — configuration view with an editable block selected
app._switch_view("config")
block_row = next(row for row in app.rows if row["values"][1] == "prod-eu")
app.tree.selection_set(block_row["iid"])
app.tree.focus(block_row["iid"])
settle()
capture(main_window(), "configuration-dark.png")

# 3 — the block editor
app._open_config_editor(block_row["payload"])
settle(0.6)
capture(dialog_window(), "editor-dark.png")
for child in app.winfo_children():
    if child.winfo_class() == "Toplevel":
        child.cancel()
app.update()

# 4 — keys in the light theme
app._set_theme("light")
app._switch_view("keys")
if app.rows:
    app.tree.selection_set(app.rows[0]["iid"])
    app.tree.focus(app.rows[0]["iid"])
settle(0.7)
capture(main_window(), "keys-light.png")

# 5 — settings, captured then closed
def shoot_settings() -> None:
    try:
        capture(dialog_window(), "settings-light.png")
    except Exception as exc:  # pragma: no cover - capture environment
        print("settings capture failed:", exc)
    for child in app.winfo_children():
        if child.winfo_class() == "Toplevel":
            child.cancel()

app.after(700, shoot_settings)
app._open_settings()
app.update()

# 6 — activity journal in the light theme
app._switch_view("activity")
settle()
capture(main_window(), "activity-light.png")

app._on_close()
if _ORIGINAL_SETTINGS is not None:
    SETTINGS.write_bytes(_ORIGINAL_SETTINGS)
elif SETTINGS.exists():
    SETTINGS.unlink()
shutil.rmtree(DEMO, ignore_errors=True)
print("done")
