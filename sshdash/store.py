"""The only module allowed to modify ``~/.ssh``.

Design rules:

* every write is preceded by a timestamped backup in
  ``~/.ssh/.sshdashboard/backups``;
* writes are atomic (temp file + ``os.replace``) and keep the original mode;
* deletion re-parses the file at write time and removes only entries whose
  tokens still match what the user selected;
* ``Include``d configuration files are never edited.
"""

from __future__ import annotations

import os
import shutil
import stat
import time
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path
from typing import Dict, Iterable, List, Optional, Sequence, Set, Tuple

from . import parsers
from .config import ActivityLog
from .model import (
    ConfigBlock,
    DeleteResult,
    HostView,
    KeyInfo,
    KnownHostLine,
    Snapshot,
    split_host_token,
)


class StoreError(RuntimeError):
    """A file operation failed; the message is safe to show to the user."""


BACKUP_DIR_NAME = ".sshdashboard"
BACKUP_STAMP = "%Y%m%d-%H%M%S"


# ---------------------------------------------------------------------------
# Filesystem helpers
# ---------------------------------------------------------------------------

def _read(path: Path) -> str:
    # newline="" preserves CRLF so Windows config files are rewritten byte-faithfully
    with path.open("r", encoding="utf-8", errors="surrogateescape", newline="") as handle:
        return handle.read()


def _atomic_write(path: Path, content: str) -> None:
    """Replace ``path`` atomically, preserving its permission bits."""
    directory = path.parent
    try:
        mode = stat.S_IMODE(path.stat().st_mode)
    except FileNotFoundError:
        mode = 0o600
    tmp = directory / f".{path.name}.sshdash.tmp"
    try:
        with tmp.open("w", encoding="utf-8", errors="surrogateescape", newline="") as handle:
            handle.write(content)
            handle.flush()
            os.fsync(handle.fileno())
        os.chmod(tmp, mode)
        os.replace(tmp, path)
    except OSError as exc:
        try:
            tmp.unlink()
        except OSError:
            pass
        raise StoreError(f"Could not write {path.name}: {exc}") from exc


# ---------------------------------------------------------------------------
# Snapshot assembly
# ---------------------------------------------------------------------------

def _block_hostname(block: ConfigBlock) -> str:
    hostname = block.option("hostname")
    if hostname:
        return hostname
    for pattern in block.patterns:
        if not any(ch in pattern for ch in "*?!"):
            return pattern
    return ""


def _int_or_none(value: Optional[str]) -> Optional[int]:
    if not value:
        return None
    digits = value.strip()
    return int(digits) if digits.isdigit() else None


def _short_hashed(token: str) -> str:
    parts = token.split("|")
    digest = parts[-1] if parts else token
    return f"hashed · {digest[:10]}…"


def _link_block(tokens: Sequence[str], blocks: Sequence[ConfigBlock]) -> Optional[ConfigBlock]:
    """Find the ssh_config block that governs this host.

    Matches on the concrete hostname first (that is what ssh stores in
    ``known_hosts``) and falls back to the ``Host`` alias itself.
    """
    plain = [split_host_token(token)[0].lower() for token in tokens if not token.startswith("|1|")]
    best: Optional[ConfigBlock] = None
    for block in blocks:
        if block.kind != "host":
            continue
        hostname = _block_hostname(block).lower()
        if hostname and hostname in plain:
            # Prefer an exact HostName match over a glob.
            if best is None or block.option("hostname"):
                best = block
                if block.option("hostname"):
                    break
    if best is not None:
        return best
    for block in blocks:
        if block.kind != "host" or block.is_global:
            continue
        if any(block.matches(token) for token in plain):
            return block
    return None


def build_snapshot(ssh_dir: Optional[Path] = None) -> Snapshot:
    """Read every artefact and aggregate it into the dashboard's view model."""
    ssh_dir = (ssh_dir or (Path.home() / ".ssh")).expanduser()
    known_path = ssh_dir / "known_hosts"
    config_path = ssh_dir / "config"

    warnings: List[str] = []
    known_lines: List[KnownHostLine] = []
    blocks: List[ConfigBlock] = []
    keys: List[KeyInfo] = []

    try:
        known_lines, known_warnings = parsers.parse_known_hosts(known_path)
        warnings.extend(known_warnings)
    except parsers.ParseError as exc:
        warnings.append(str(exc))

    try:
        blocks, config_warnings = parsers.parse_ssh_config(config_path)
        warnings.extend(config_warnings)
    except parsers.ParseError as exc:
        warnings.append(str(exc))

    try:
        keys = parsers.scan_keys(ssh_dir)
    except Exception as exc:  # never let key scanning break the dashboard
        warnings.append(f"Key scan failed: {exc}")

    groups: Dict[Tuple[str, ...], dict] = {}
    for line in known_lines:
        key = tuple(sorted(line.tokens))
        group = groups.setdefault(
            key,
            {"lines": [], "key_types": [], "fingerprints": [], "markers": []},
        )
        group["lines"].append(line.lineno)
        if line.key_type not in group["key_types"]:
            group["key_types"].append(line.key_type)
        fingerprint = parsers.key_fingerprint(line.key_blob)
        if fingerprint and fingerprint not in group["fingerprints"]:
            group["fingerprints"].append(fingerprint)
        if line.marker and line.marker not in group["markers"]:
            group["markers"].append(line.marker)

    hosts: List[HostView] = []
    linked_blocks: Set[Tuple[str, int]] = set()

    for tokens, group in groups.items():
        block = _link_block(tokens, blocks)
        if block is not None:
            linked_blocks.add((str(block.source), block.start_line))
        hashed = all(token.startswith("|1|") for token in tokens)
        plain_names = [split_host_token(t)[0] for t in tokens if not t.startswith("|1|")]
        token_ports = [split_host_token(t)[1] for t in tokens if split_host_token(t)[1]]
        if block is not None:
            alias = next((p for p in block.patterns if not p.startswith("!")), None)
            hostname = (block.option("hostname") or (plain_names[0] if plain_names else ""))
            port = _int_or_none(block.option("port")) or (token_ports[0] if token_ports else 22)
            label = alias if alias and alias.lower() != hostname.lower() else (hostname or alias or "")
        else:
            alias = None
            hostname = plain_names[0] if plain_names else ""
            port = token_ports[0] if token_ports else 22
            label = hostname
        if hashed:
            label = _short_hashed(tokens[0])
            hostname = _short_hashed(tokens[0])
        if not label:
            label = "unknown entry"
        hosts.append(
            HostView(
                key="|".join(tokens),
                label=label,
                address=hostname or label,
                port=port,
                user=block.option("user") if block else None,
                identity=block.option("identityfile") if block else None,
                alias=alias,
                key_types=tuple(group["key_types"]),
                fingerprints=tuple(group["fingerprints"]),
                tokens=tokens,
                known_hosts_lines=tuple(group["lines"]),
                hashed=hashed,
                marker=", ".join(group["markers"]),
                block=block,
                in_known_hosts=True,
            )
        )

    # Aliases declared in ssh_config but never contacted yet.
    known_plain = {
        split_host_token(token)[0].lower()
        for group in groups
        for token in group
        if not token.startswith("|1|")
    }
    for block in blocks:
        if block.kind != "host" or block.is_global:
            continue
        identity = (str(block.source), block.start_line)
        if identity in linked_blocks:
            continue
        hostname = _block_hostname(block)
        if hostname and hostname.lower() in known_plain:
            continue
        alias = next((p for p in block.patterns if not p.startswith("!")), block.label)
        hosts.append(
            HostView(
                key=f"config:{block.source}:{block.start_line}",
                label=alias,
                address=hostname or alias,
                port=_int_or_none(block.option("port")) or 22,
                user=block.option("user"),
                identity=block.option("identityfile"),
                alias=alias,
                key_types=(),
                fingerprints=(),
                tokens=(),
                known_hosts_lines=(),
                hashed=False,
                marker="",
                block=block,
                in_known_hosts=False,
            )
        )

    hosts.sort(key=lambda host: (not host.in_known_hosts, host.label.lower()))
    return Snapshot(
        ssh_dir=ssh_dir,
        known_hosts_path=known_path,
        config_path=config_path,
        hosts=tuple(hosts),
        config_blocks=tuple(blocks),
        keys=tuple(keys),
        warnings=tuple(warnings),
    )


# ---------------------------------------------------------------------------
# Store
# ---------------------------------------------------------------------------

class SshStore:
    """Backed by an ``~/.ssh`` directory; all mutations go through here."""

    def __init__(
        self,
        ssh_dir: Optional[Path] = None,
        activity: Optional[ActivityLog] = None,
    ) -> None:
        self.ssh_dir = (ssh_dir or (Path.home() / ".ssh")).expanduser()
        self.activity = activity or ActivityLog()

    # -- paths ------------------------------------------------------------
    @property
    def known_hosts_path(self) -> Path:
        return self.ssh_dir / "known_hosts"

    @property
    def config_path(self) -> Path:
        return self.ssh_dir / "config"

    @property
    def backup_dir(self) -> Path:
        return self.ssh_dir / BACKUP_DIR_NAME / "backups"

    # -- scanning ---------------------------------------------------------
    def snapshot(self) -> Snapshot:
        return build_snapshot(self.ssh_dir)

    # -- backups ----------------------------------------------------------
    def _backup(self, path: Path) -> Path:
        if not path.exists():
            raise StoreError(f"{path.name} does not exist")
        directory = self.backup_dir
        try:
            directory.mkdir(parents=True, exist_ok=True)
            os.chmod(self.ssh_dir / BACKUP_DIR_NAME, 0o700)
            os.chmod(directory, 0o700)
            stamp = datetime.now().strftime(BACKUP_STAMP)
            target = directory / f"{stamp}__{path.name}"
            counter = 1
            while target.exists():
                target = directory / f"{stamp}-{counter}__{path.name}"
                counter += 1
            shutil.copy2(path, target)
            os.chmod(target, 0o600)
        except OSError as exc:
            raise StoreError(f"Could not create a backup: {exc}") from exc
        return target

    def restore_backup(self, backup: Path) -> Path:
        """Copy a backup back over its original file."""
        name = backup.name
        if "__" not in name:
            raise StoreError("Unrecognised backup file name")
        original = name.split("__", 1)[1]
        target = self.ssh_dir / original
        # Keep a safety copy of the current state before restoring.
        if target.exists():
            self._backup(target)
        try:
            content = _read(backup)
            _atomic_write(target, content)
            os.chmod(target, 0o600)
        except OSError as exc:
            raise StoreError(f"Restore failed: {exc}") from exc
        self.activity.append("restore", f"Restored {original} from backup")
        return target

    # -- deletion ---------------------------------------------------------
    def delete_hosts(
        self,
        hosts: Iterable[HostView],
        remove_config: bool = True,
    ) -> DeleteResult:
        """Remove the selected hosts from ``known_hosts`` and optionally config."""
        hosts = list(hosts)
        tokens: Set[str] = set()
        blocks: List[ConfigBlock] = []
        for host in hosts:
            if host.in_known_hosts and host.tokens:
                tokens.update(host.tokens)
            if remove_config and host.block is not None and host.block.deletable:
                blocks.append(host.block)

        backups: List[Path] = []
        warnings: List[str] = []
        removed_hosts = 0
        removed_lines = 0
        removed_blocks = 0

        if tokens:
            removed_hosts, removed_lines = self._remove_known_host_tokens(tokens, backups)

        if blocks:
            removed_blocks, block_warnings = self._remove_config_blocks(blocks, backups)
            warnings.extend(block_warnings)

        if removed_hosts or removed_blocks:
            label = ", ".join(sorted(host.label for host in hosts))
            self.activity.append(
                "delete",
                f"Removed {removed_hosts} host(s) and {removed_blocks} config block(s): {label}",
                {"backups": [str(path) for path in backups]},
            )
        return DeleteResult(
            hosts_removed=removed_hosts,
            lines_removed=removed_lines,
            config_blocks_removed=removed_blocks,
            backups=tuple(backups),
            warnings=tuple(warnings),
        )

    def _remove_known_host_tokens(
        self, tokens: Set[str], backups: List[Path]
    ) -> Tuple[int, int]:
        path = self.known_hosts_path
        if not path.exists():
            raise StoreError("known_hosts does not exist")
        try:
            text = _read(path)
        except OSError as exc:
            raise StoreError(f"Could not read known_hosts: {exc}") from exc

        lowered = {token.lower() for token in tokens}
        kept: List[str] = []
        removed_tokens: Set[str] = set()
        removed_lines = 0

        # keepends preserves the file's original newline structure.
        for raw in text.splitlines(keepends=True):
            body = raw.rstrip("\r\n")
            ending = raw[len(body):]
            stripped = body.strip()
            if not stripped or stripped.startswith("#"):
                kept.append(raw)
                continue
            parts = stripped.split()
            marker = ""
            if parts and parts[0].lower() in parsers.KNOWN_HOSTS_MARKERS:
                marker = parts.pop(0)
            if len(parts) < 3:
                kept.append(raw)
                continue
            line_tokens = [token for token in parts[0].split(",") if token]
            hits = {token for token in line_tokens if token.lower() in lowered}
            if not hits:
                kept.append(raw)
                continue
            removed_tokens.update(hits)
            removed_lines += 1
            remaining = [token for token in line_tokens if token.lower() not in lowered]
            if not remaining:
                continue
            prefix = f"{marker} " if marker else ""
            kept.append(
                f"{prefix}{','.join(remaining)} {' '.join(parts[1:])}".rstrip() + ending
            )

        if not removed_lines:
            return 0, 0
        backup = self._backup(path)
        backups.append(backup)
        _atomic_write(path, "".join(kept))
        return len(removed_tokens), removed_lines

    def _remove_config_blocks(
        self, blocks: Sequence[ConfigBlock], backups: List[Path]
    ) -> Tuple[int, List[str]]:
        warnings: List[str] = []
        by_source: Dict[Path, List[ConfigBlock]] = {}
        for block in blocks:
            if not block.editable:
                warnings.append(f"Skipped {block.source.name}: included files are read-only")
                continue
            by_source.setdefault(block.source, []).append(block)

        total = 0
        for source, wanted in by_source.items():
            if not source.exists():
                warnings.append(f"{source.name} no longer exists")
                continue
            # Re-parse so we delete the *current* line ranges, not stale ones.
            try:
                current, _ = parsers.parse_ssh_config(source)
            except parsers.ParseError as exc:
                warnings.append(str(exc))
                continue
            current_by_start = {block.start_line: block for block in current if block.editable}
            ranges: List[Tuple[int, int]] = []
            for block in wanted:
                live = current_by_start.get(block.start_line)
                if live is None or live.patterns != block.patterns:
                    warnings.append(
                        f"{source.name} changed on disk; '{block.label}' was not removed"
                    )
                    continue
                ranges.append((live.start_line, live.end_line))
            if not ranges:
                continue
            try:
                text = _read(source)
            except OSError as exc:
                warnings.append(f"Could not read {source.name}: {exc}")
                continue
            lines = text.splitlines(keepends=True)
            for start, end in sorted(ranges, reverse=True):
                del lines[start - 1:end]
                # Collapse a doubled blank line left behind by the removal.
                index = start - 1
                if 0 <= index < len(lines) and not lines[index].strip():
                    if index == 0 or not lines[index - 1].strip():
                        del lines[index]
            backup = self._backup(source)
            backups.append(backup)
            _atomic_write(source, "".join(lines))
            total += len(ranges)
        return total, warnings

    # -- editing ----------------------------------------------------------
    def update_config_block(
        self,
        block: ConfigBlock,
        patterns: Sequence[str],
        values: Dict[str, str],
    ) -> Path:
        """Rewrite the edited options of one ``Host`` block.

        ``values`` keys are lower-case option names (hostname, user, port,
        identityfile, proxyjump). An empty value removes that directive. Other
        options, comments and the surrounding blocks are left untouched.
        """
        if not block.editable:
            raise StoreError(f"{block.source.name} is included and read-only here")
        if block.kind != "host" or block.is_global:
            raise StoreError("Global and Match blocks cannot be edited here")
        source = block.source
        if not source.exists():
            raise StoreError(f"{source.name} does not exist")
        try:
            text = _read(source)
            current, _ = parsers.parse_ssh_config(source)
        except (OSError, parsers.ParseError) as exc:
            raise StoreError(f"Could not read {source.name}: {exc}") from exc
        live = next(
            (
                candidate for candidate in current
                if candidate.start_line == block.start_line
                and candidate.patterns == block.patterns
                and candidate.editable
            ),
            None,
        )
        if live is None:
            raise StoreError(
                f"{source.name} changed on disk since the last scan; rescan and try again"
            )
        cleaned_patterns = [pattern.strip() for pattern in patterns if pattern.strip()]
        if not cleaned_patterns:
            raise StoreError("A Host block needs at least one pattern")
        lines = text.splitlines(keepends=True)
        if not lines:
            raise StoreError(f"{source.name} is empty")
        rewritten = self._rewrite_block(lines, live, cleaned_patterns, values)
        backup = self._backup(source)
        _atomic_write(source, "".join(rewritten))
        changed = ", ".join(sorted(key for key, value in values.items()))
        self.activity.append(
            "edit-config",
            f"Edited Host {', '.join(cleaned_patterns)} ({changed})",
            {"backups": [str(backup)]},
        )
        return backup

    @staticmethod
    def _rewrite_block(
        lines: List[str],
        block: ConfigBlock,
        patterns: Sequence[str],
        values: Dict[str, str],
    ) -> List[str]:
        start = block.start_line - 1
        end = min(block.end_line, len(lines))
        header = lines[start]
        first_newline = header.find("\n")
        newline = "\r\n" if header[first_newline - 1:first_newline] == "\r" else "\n"
        header_text = header.rstrip("\r\n")
        indent = header_text[: len(header_text) - len(header_text.lstrip())]
        keyword = header_text.strip().split(None, 1)[0]
        new_header = f"{indent}{keyword} {' '.join(patterns)}{newline}"

        canonical = {
            "hostname": "HostName",
            "user": "User",
            "port": "Port",
            "identityfile": "IdentityFile",
            "proxyjump": "ProxyJump",
        }
        wanted = {key: value.strip() for key, value in values.items()}
        seen: Set[str] = set()
        body: List[str] = []
        for raw in lines[start + 1:end]:
            content = raw.rstrip("\r\n")
            eol = raw[len(content):] or newline
            stripped = content.strip()
            if not stripped or stripped.startswith("#"):
                body.append(raw)
                continue
            parts = stripped.split(None, 1)
            key = parts[0].lower()
            if key not in wanted:
                body.append(raw)
                continue
            if key in seen:
                continue  # collapse duplicates of an edited directive
            seen.add(key)
            new_value = wanted[key]
            if not new_value:
                continue  # empty value removes the directive
            leading = content[: len(content) - len(content.lstrip())] or "    "
            body.append(f"{leading}{parts[0]} {new_value}{eol}")

        additions: List[str] = []
        for key, name in canonical.items():
            if key in seen or key not in wanted:
                continue
            if wanted[key]:
                additions.append(f"    {name} {wanted[key]}{newline}")
        return [*lines[:start], new_header, *body, *additions, *lines[end:]]

    def delete_config_blocks(self, block_ids: Sequence[Tuple[Path, int, Tuple[str, ...]]]) -> DeleteResult:
        """Remove whole ``Host`` blocks, addressed by (source, start line, patterns)."""
        backups: List[Path] = []
        wanted: List[ConfigBlock] = []
        warnings: List[str] = []
        for source, start, patterns in block_ids:
            wanted.append(
                ConfigBlock(source=Path(source), start_line=start, end_line=start, patterns=patterns)
            )
        removed, block_warnings = self._remove_config_blocks(wanted, backups)
        warnings.extend(block_warnings)
        if removed:
            labels = ", ".join(block.label for block in wanted)
            self.activity.append(
                "delete-config",
                f"Removed {removed} ssh_config block(s): {labels}",
                {"backups": [str(path) for path in backups]},
            )
        return DeleteResult(
            config_blocks_removed=removed,
            backups=tuple(backups),
            warnings=tuple(warnings),
        )

    # -- convenience ------------------------------------------------------
    def backups(self) -> List[Path]:
        try:
            return sorted(
                (path for path in self.backup_dir.iterdir() if "__" in path.name),
                reverse=True,
            )
        except OSError:
            return []
