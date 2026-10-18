"""Application plumbing: settings, activity log and small path helpers."""

from __future__ import annotations

import json
import os
import sys
import time
from dataclasses import asdict, dataclass, field
from datetime import datetime
from pathlib import Path
from typing import Any, Dict, List, Optional

APP_DIR_NAME = "SSHDashboard"


def support_dir() -> Path:
    """Per-user application data directory (macOS convention first)."""
    override = os.environ.get("SSHDASH_APP_DIR")
    if override:
        return Path(override).expanduser()
    if sys.platform == "darwin":
        return Path.home() / "Library" / "Application Support" / APP_DIR_NAME
    if os.name == "nt":
        base = os.environ.get("APPDATA") or str(Path.home())
        return Path(base) / APP_DIR_NAME
    return Path(os.environ.get("XDG_CONFIG_HOME", Path.home() / ".config")) / "sshdashboard"


def human_time(timestamp: float) -> str:
    return datetime.fromtimestamp(timestamp).strftime("%d %b %Y · %H:%M")


@dataclass
class Settings:
    """User preferences, persisted as JSON."""

    theme: str = "dark"
    last_view: str = "hosts"
    window: str = ""
    confirm_before_delete: bool = True
    remove_config_default: bool = True

    @classmethod
    def load(cls) -> "Settings":
        path = support_dir() / "settings.json"
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            return cls()
        known = {f for f in cls.__dataclass_fields__}  # type: ignore[attr-defined]
        return cls(**{k: v for k, v in data.items() if k in known})

    def save(self) -> None:
        path = support_dir() / "settings.json"
        try:
            path.parent.mkdir(parents=True, exist_ok=True)
            tmp = path.with_suffix(".tmp")
            tmp.write_text(json.dumps(asdict(self), indent=2), encoding="utf-8")
            tmp.replace(path)
        except OSError:
            pass  # preferences are nice-to-have; never crash the app for them


class ActivityLog:
    """Append-only JSONL journal of destructive operations."""

    MAX_BYTES = 512 * 1024

    def __init__(self, path: Optional[Path] = None) -> None:
        self.path = path or (support_dir() / "activity.jsonl")

    def append(self, action: str, detail: str, meta: Optional[Dict[str, Any]] = None) -> None:
        record = {
            "ts": time.time(),
            "action": action,
            "detail": detail,
            "meta": meta or {},
        }
        try:
            self.path.parent.mkdir(parents=True, exist_ok=True)
            self._rotate()
            with self.path.open("a", encoding="utf-8") as handle:
                handle.write(json.dumps(record) + "\n")
        except OSError:
            pass

    def _rotate(self) -> None:
        try:
            if self.path.exists() and self.path.stat().st_size > self.MAX_BYTES:
                backup = self.path.with_suffix(".1.jsonl")
                self.path.replace(backup)
        except OSError:
            pass

    def entries(self, limit: int = 200) -> List[Dict[str, Any]]:
        records: List[Dict[str, Any]] = []
        try:
            with self.path.open("r", encoding="utf-8") as handle:
                for line in handle:
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        records.append(json.loads(line))
                    except ValueError:
                        continue
        except OSError:
            return []
        return list(reversed(records[-limit:]))
