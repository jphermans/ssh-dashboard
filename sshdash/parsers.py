"""Read-only parsers for OpenSSH artefacts.

Nothing in this module writes to disk. Everything is defensive: a malformed
file yields warnings instead of an exception, because the dashboard must stay
usable even when a hand-edited config is broken.
"""

from __future__ import annotations

import base64
import fnmatch
import glob
import hashlib
import hmac
import os
import shutil
import subprocess
from pathlib import Path
from typing import Dict, Iterable, List, Optional, Sequence, Tuple

from .model import ConfigBlock, KeyInfo, KnownHostLine, split_host_token

KNOWN_HOSTS_MARKERS = ("@cert-authority", "@revoked")


class ParseError(RuntimeError):
    """Raised only for files that cannot be read at all."""


def read_text(path: Path) -> str:
    """Read a text file without ever raising Unicode errors."""
    try:
        return path.read_text(encoding="utf-8", errors="surrogateescape")
    except FileNotFoundError:
        raise ParseError(f"{path} does not exist")
    except PermissionError as exc:
        raise ParseError(f"Permission denied reading {path}") from exc
    except OSError as exc:
        raise ParseError(f"Could not read {path}: {exc}") from exc


# ---------------------------------------------------------------------------
# known_hosts
# ---------------------------------------------------------------------------

def parse_known_hosts(path: Path) -> Tuple[List[KnownHostLine], List[str]]:
    """Parse ``known_hosts`` into line objects plus human readable warnings."""
    warnings: List[str] = []
    if not path.exists():
        return [], [f"{path.name} not found"]
    text = read_text(path)
    lines: List[KnownHostLine] = []
    for lineno, raw in enumerate(text.splitlines(), start=1):
        stripped = raw.strip()
        if not stripped or stripped.startswith("#"):
            continue
        parts = stripped.split()
        marker = ""
        if parts and parts[0].lower() in KNOWN_HOSTS_MARKERS:
            marker = parts.pop(0).lower()
        if len(parts) < 3:
            warnings.append(f"Line {lineno}: not a valid known_hosts entry, skipped")
            continue
        tokens = tuple(token for token in parts[0].split(",") if token)
        if not tokens:
            warnings.append(f"Line {lineno}: empty host field, skipped")
            continue
        lines.append(
            KnownHostLine(
                lineno=lineno,
                raw=raw,
                tokens=tokens,
                key_type=parts[1],
                key_blob=parts[2],
                comment=" ".join(parts[3:]),
                marker=marker,
            )
        )
    return lines, warnings


def key_fingerprint(key_blob: str) -> str:
    """``SHA256:...`` fingerprint of a base64 public key blob (like ssh-keygen)."""
    try:
        raw = base64.b64decode(key_blob + "=" * (-len(key_blob) % 4))
    except Exception:
        return ""
    digest = hashlib.sha256(raw).digest()
    return "SHA256:" + base64.b64encode(digest).decode("ascii").rstrip("=")


def short_fingerprint(fingerprint: str, width: int = 26) -> str:
    if len(fingerprint) <= width:
        return fingerprint
    return fingerprint[: width - 1] + "…"


def hashed_token_matches(token: str, hostname: str) -> bool:
    """Return True when a ``|1|salt|hash`` token belongs to ``hostname``.

    OpenSSH's hashed format is ``HMAC-SHA1(salt, hostname)`` with the salt and
    digest base64 encoded, so it can be verified without reversing the hash.
    """
    if not token.startswith("|1|"):
        return False
    parts = token.split("|")
    if len(parts) != 4:
        return False
    _, version, salt_b64, digest_b64 = parts
    if version != "1":
        return False
    try:
        salt = base64.b64decode(salt_b64 + "=" * (-len(salt_b64) % 4))
        expected = base64.b64decode(digest_b64 + "=" * (-len(digest_b64) % 4))
    except Exception:
        return False
    actual = hmac.new(salt, hostname.encode("utf-8"), hashlib.sha1).digest()
    return hmac.compare_digest(actual, expected)


def token_matches_host(token: str, hostname: str) -> bool:
    """Match a known_hosts token against a queried hostname (hashing included)."""
    if token.startswith("|1|"):
        return hashed_token_matches(token, hostname)
    plain, _ = split_host_token(token)
    return fnmatch.fnmatchcase(hostname.lower(), plain.lower())


# ---------------------------------------------------------------------------
# ssh_config
# ---------------------------------------------------------------------------

def _expand_include(value: str, base_dir: Path) -> List[Path]:
    value = os.path.expanduser(value.strip())
    if not os.path.isabs(value):
        value = str(base_dir / value)
    matches = sorted(glob.glob(value))
    return [Path(m) for m in matches if Path(m).is_file()]


def parse_ssh_config(
    path: Path,
    _depth: int = 0,
    _seen: Optional[set] = None,
) -> Tuple[List[ConfigBlock], List[str]]:
    """Parse an ssh_config, following ``Include`` directives (bounded depth)."""
    warnings: List[str] = []
    if not path.exists():
        return [], [f"{path.name} not found"]
    resolved = path.expanduser().resolve()
    seen = _seen if _seen is not None else set()
    if resolved in seen or _depth > 6:
        return [], []
    seen.add(resolved)

    text = read_text(path)
    raw_lines = text.splitlines()
    blocks: List[ConfigBlock] = []
    includes: List[Tuple[str, Path]] = []

    current: Optional[dict] = None

    def flush() -> None:
        nonlocal current
        if current is None:
            return
        options = tuple(current["options"])
        end = current["last_content"] or current["start"]
        blocks.append(
            ConfigBlock(
                source=path,
                start_line=current["start"],
                end_line=end,
                patterns=tuple(current["patterns"]),
                options=options,
                kind=current["kind"],
                editable=_depth == 0,
            )
        )
        current = None

    for index, raw in enumerate(raw_lines, start=1):
        stripped = raw.strip()
        if not stripped or stripped.startswith("#"):
            continue
        if raw[:1].isspace():
            # Continuation / option line inside the current block.
            if current is None:
                warnings.append(f"Line {index}: option outside any Host block, ignored")
                continue
            parts = stripped.split(None, 1)
            key = parts[0]
            value = parts[1].strip() if len(parts) > 1 else ""
            current["options"].append((key, value))
            current["last_content"] = index
            if key.lower() == "include" and value:
                includes.append((value, path.parent))
            continue

        parts = stripped.split(None, 1)
        keyword = parts[0].lower()
        arguments = parts[1] if len(parts) > 1 else ""
        if keyword in ("host", "match"):
            flush()
            patterns = arguments.split() if keyword == "host" else [arguments.strip()]
            current = {
                "start": index,
                "last_content": index if arguments else 0,
                "patterns": patterns,
                "options": [],
                "kind": "match" if keyword == "match" else "host",
            }
        else:
            # Global option before any Host block: ssh applies it to everyone.
            if current is None:
                current = {
                    "start": index,
                    "last_content": index,
                    "patterns": ["*"],
                    "options": [],
                    "kind": "host",
                }
            current["options"].append((parts[0], arguments.strip()))
            current["last_content"] = index
            if keyword == "include" and arguments:
                includes.append((arguments, path.parent))
    flush()

    for value, base_dir in includes:
        for include_path in _expand_include(value, base_dir):
            sub_blocks, sub_warnings = parse_ssh_config(
                include_path, _depth=_depth + 1, _seen=seen
            )
            blocks.extend(sub_blocks)
            warnings.extend(sub_warnings)

    return blocks, warnings


# ---------------------------------------------------------------------------
# Keys
# ---------------------------------------------------------------------------

_BITS_BY_TYPE = {
    "ssh-ed25519": "256",
    "ssh-rsa": "",
    "rsa": "",
    "ecdsa-sha2-nistp256": "256",
    "ecdsa-sha2-nistp384": "384",
    "ecdsa-sha2-nistp521": "521",
    "sk-ssh-ed25519@openssh.com": "256",
    "sk-ecdsa-sha2-nistp256@openssh.com": "256",
}


def _ssh_keygen() -> Optional[str]:
    return shutil.which("ssh-keygen")


def _ssh_keygen_info(path: Path) -> Optional[Tuple[str, str, str]]:
    """Return ``(type, bits, fingerprint)`` using ssh-keygen when available."""
    binary = _ssh_keygen()
    if not binary:
        return None
    try:
        result = subprocess.run(
            [binary, "-E", "sha256", "-l", "-f", str(path)],
            capture_output=True,
            text=True,
            timeout=5,
        )
    except (OSError, subprocess.SubprocessError):
        return None
    if result.returncode != 0 or not result.stdout.strip():
        return None
    # "256 SHA256:abc... comment (ED25519)"
    fields = result.stdout.strip().split(None, 2)
    if len(fields) < 2:
        return None
    bits = fields[0]
    fingerprint = fields[1]
    key_type = ""
    if "(" in result.stdout and result.stdout.rstrip().endswith(")"):
        key_type = result.stdout.rstrip(")\n").rsplit("(", 1)[-1].lower()
    return key_type, bits, fingerprint


def _public_key_parts(path: Path) -> Tuple[str, str, str]:
    try:
        text = read_text(path).strip()
    except ParseError:
        return "", "", ""
    parts = text.split(None, 2)
    if len(parts) < 2:
        return "", "", ""
    return parts[0], parts[1], parts[2] if len(parts) > 2 else ""


def _is_private_key(path: Path) -> bool:
    try:
        with path.open("r", encoding="utf-8", errors="ignore") as handle:
            head = handle.read(64)
    except OSError:
        return False
    return head.startswith("-----BEGIN") and "PRIVATE KEY" in head


def _key_needs_passphrase(path: Path) -> Optional[bool]:
    binary = _ssh_keygen()
    if not binary:
        return None
    try:
        result = subprocess.run(
            [binary, "-y", "-P", "", "-f", str(path)],
            capture_output=True,
            text=True,
            timeout=5,
        )
    except (OSError, subprocess.SubprocessError):
        return None
    if result.returncode == 0:
        return False
    message = (result.stderr or "").lower()
    if "passphrase" in message or "encrypted" in message:
        return True
    return None


def _permissions(path: Path) -> str:
    if os.name == "nt":
        return "n/a"
    try:
        return oct(path.stat().st_mode & 0o777)[2:].rjust(3, "0")
    except OSError:
        return "---"


SIDECAR_NAMES = {
    "known_hosts",
    "known_hosts.old",
    "config",
    "authorized_keys",
    "authorized_keys2",
}


def scan_keys(ssh_dir: Path) -> List[KeyInfo]:
    """Collect key pairs from ``ssh_dir`` (non-recursive, sidecars excluded)."""
    keys: List[KeyInfo] = []
    if not ssh_dir.is_dir():
        return keys
    for entry in sorted(ssh_dir.iterdir()):
        if not entry.is_file() or entry.name in SIDECAR_NAMES:
            continue
        if entry.name.startswith(".") or entry.name.endswith(".pub"):
            continue
        private = _is_private_key(entry)
        public = ssh_dir / f"{entry.name}.pub"
        if not private and not public.exists():
            continue
        reference = public if public.exists() else entry
        key_type, bits, fingerprint = _ssh_keygen_info(reference) or ("", "", "")
        comment = ""
        if public.exists():
            pub_type, pub_blob, comment = _public_key_parts(public)
            if not key_type:
                key_type = pub_type.rsplit("-", 1)[-1] if "-" in pub_type else pub_type
            if not bits:
                bits = _BITS_BY_TYPE.get(pub_type, "")
            if not fingerprint and pub_blob:
                fingerprint = key_fingerprint(pub_blob)
        try:
            modified = entry.stat().st_mtime
        except OSError:
            modified = 0.0
        keys.append(
            KeyInfo(
                path=entry,
                name=entry.name,
                key_type=key_type.lower(),
                bits=bits,
                fingerprint=fingerprint,
                comment=comment,
                encrypted=_key_needs_passphrase(entry) if private else None,
                has_public=public.exists(),
                permissions=_permissions(entry),
                modified=modified,
            )
        )
    return keys
