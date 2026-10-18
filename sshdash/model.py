"""Value objects shared by the store, the parsers and the UI."""

from __future__ import annotations

import fnmatch
import re
from dataclasses import dataclass, field
from datetime import datetime
from pathlib import Path
from typing import Dict, List, Optional, Tuple

PORT_RE = re.compile(r"^\[(?P<host>.+)\]:\d+$")

#: Characters that turn an ssh_config pattern into a glob.
_GLOB_CHARS = "*?!"


def split_host_token(token: str) -> Tuple[str, Optional[int]]:
    """Split ``[example.com]:2222`` into ``("example.com", 2222)``."""
    match = PORT_RE.match(token)
    if match:
        port = token.rsplit(":", 1)[1]
        return match.group("host"), int(port) if port.isdigit() else None
    return token, None


@dataclass(frozen=True)
class KnownHostLine:
    """One physical line of ``known_hosts``."""

    lineno: int
    raw: str
    tokens: Tuple[str, ...]
    key_type: str
    key_blob: str
    comment: str
    marker: str  # "", "@cert-authority" or "@revoked"

    @property
    def hashed(self) -> bool:
        return self.tokens[0].startswith("|1|") if self.tokens else False

    @property
    def key_kind(self) -> str:
        """Human friendly key family, e.g. ``ed25519`` or ``rsa``."""
        return self.key_type.rsplit("-", 1)[-1] if "-" in self.key_type else self.key_type


@dataclass(frozen=True)
class ConfigBlock:
    """A ``Host`` or ``Match`` block from an ssh_config file."""

    source: Path
    start_line: int
    end_line: int
    patterns: Tuple[str, ...]
    options: Tuple[Tuple[str, str], ...] = ()
    kind: str = "host"  # "host" or "match"
    editable: bool = True  # False for blocks pulled in through Include

    def option(self, name: str) -> Optional[str]:
        """Value of the first occurrence of ``name`` (ssh uses first-wins)."""
        wanted = name.lower()
        for key, value in self.options:
            if key.lower() == wanted:
                return value
        return None

    @property
    def is_global(self) -> bool:
        return self.kind == "match" or any(p == "*" for p in self.patterns)

    @property
    def deletable(self) -> bool:
        """Only literal, non-editable-safe blocks may be removed from a file."""
        if not self.editable or self.kind != "host" or not self.patterns:
            return False
        for pattern in self.patterns:
            if any(ch in pattern for ch in _GLOB_CHARS) or pattern.startswith("!"):
                return False
        return True

    def matches(self, hostname: str) -> bool:
        """Mirror ssh's Host pattern matching (glob + ``!`` negation)."""
        host = hostname.lower()
        matched = False
        for raw in self.patterns:
            negated = raw.startswith("!")
            pattern = raw[1:] if negated else raw
            if fnmatch.fnmatchcase(host, pattern.lower()):
                if negated:
                    return False
                matched = True
        return matched

    @property
    def label(self) -> str:
        return ", ".join(self.patterns) if self.patterns else "Match block"


@dataclass(frozen=True)
class HostView:
    """An aggregated row: one known_hosts host plus its ssh_config settings."""

    key: str
    label: str
    address: str
    port: Optional[int]
    user: Optional[str]
    identity: Optional[str]
    alias: Optional[str]
    key_types: Tuple[str, ...]
    fingerprints: Tuple[str, ...]
    tokens: Tuple[str, ...]
    known_hosts_lines: Tuple[int, ...]
    hashed: bool
    marker: str
    block: Optional[ConfigBlock]
    in_known_hosts: bool

    @property
    def is_removable(self) -> bool:
        return bool(self.tokens) or bool(self.block and self.block.deletable)

    @property
    def source_label(self) -> str:
        parts = []
        if self.in_known_hosts:
            parts.append("known_hosts")
        if self.block is not None:
            parts.append("config")
        return " + ".join(parts) if parts else "—"

    @property
    def ssh_command(self) -> str:
        target = self.alias or self.address
        if self.user:
            target = f"{self.user}@{target}"
        if self.port and self.port != 22:
            return f"ssh -p {self.port} {target}"
        return f"ssh {target}"


@dataclass(frozen=True)
class KeyInfo:
    """A key pair found in ``~/.ssh``."""

    path: Path
    name: str
    key_type: str
    bits: str
    fingerprint: str
    comment: str
    encrypted: Optional[bool]
    has_public: bool
    permissions: str
    modified: float

    @property
    def display_type(self) -> str:
        if self.bits:
            return f"{self.key_type} {self.bits}"
        return self.key_type or "unknown"

    @property
    def status(self) -> str:
        if self.encrypted:
            return "Encrypted"
        return "No passphrase" if self.encrypted is False else "—"


@dataclass(frozen=True)
class Snapshot:
    """Everything the dashboard knows after a scan."""

    ssh_dir: Path
    known_hosts_path: Path
    config_path: Path
    hosts: Tuple[HostView, ...]
    config_blocks: Tuple[ConfigBlock, ...]
    keys: Tuple[KeyInfo, ...]
    warnings: Tuple[str, ...]
    scanned_at: datetime = field(default_factory=datetime.now)

    @property
    def known_host_count(self) -> int:
        return sum(1 for host in self.hosts if host.in_known_hosts)

    @property
    def config_count(self) -> int:
        return sum(1 for block in self.config_blocks if block.kind == "host" and not block.is_global)


@dataclass(frozen=True)
class DeleteResult:
    """Outcome of a delete operation."""

    hosts_removed: int = 0
    lines_removed: int = 0
    config_blocks_removed: int = 0
    backups: Tuple[Path, ...] = ()
    warnings: Tuple[str, ...] = ()

    @property
    def summary(self) -> str:
        bits: List[str] = []
        if self.hosts_removed:
            bits.append(f"{self.hosts_removed} host{'s' if self.hosts_removed != 1 else ''}")
        if self.config_blocks_removed:
            bits.append(
                f"{self.config_blocks_removed} config block"
                f"{'s' if self.config_blocks_removed != 1 else ''}"
            )
        return " and ".join(bits) if bits else "nothing"
