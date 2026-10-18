"""Interpreter capability checks.

macOS still ships Python 3.9 with Tk 8.5, which hangs the moment a modern Tk
window is mapped. Rather than crash (or freeze) for the user, the app checks the
Tk version *before* creating a root window and re-launches itself with a
supported interpreter when one is installed.
"""

from __future__ import annotations

import os
import shutil
import subprocess
import sys
from pathlib import Path
from typing import Iterable, Optional, Sequence, Tuple

MINIMUM_TK = (8, 6)

#: Ordered best-first. Homebrew paths are explicit because PATH may be minimal
#: when the app is started from Finder.
CANDIDATE_INTERPRETERS: Tuple[str, ...] = (
    "python3.14",
    "python3.13",
    "python3.12",
    "python3.11",
    "python3",
    "/opt/homebrew/bin/python3.14",
    "/opt/homebrew/bin/python3.13",
    "/opt/homebrew/bin/python3.12",
    "/usr/local/bin/python3.14",
    "/usr/local/bin/python3.13",
    "/usr/local/bin/python3.12",
    "python",
)

RELAUNCH_ENV = "SSHDASH_RELAUNCHED"
AUTOCLOSE_ENV = "SSHDASH_AUTOCLOSE_MS"


def tk_version() -> Tuple[int, int]:
    """Return the Tk version of the *current* interpreter, without a display."""
    try:
        import tkinter
    except Exception:
        return (0, 0)
    version = float(tkinter.TkVersion)
    return (int(version), int(round((version - int(version)) * 10)))


def _probe(executable: str) -> Optional[Tuple[int, int]]:
    """Ask another interpreter for its Tk version (import only, no window)."""
    try:
        result = subprocess.run(
            [
                executable,
                "-c",
                "import tkinter, sys; print(tkinter.TkVersion); "
                "assert sys.version_info >= (3, 9)",
            ],
            capture_output=True,
            text=True,
            timeout=12,
        )
    except (OSError, subprocess.SubprocessError):
        return None
    if result.returncode != 0:
        return None
    try:
        version = float(result.stdout.strip().splitlines()[-1])
    except (ValueError, IndexError):
        return None
    return (int(version), int(round((version - int(version)) * 10)))


def find_supported_interpreter(
    candidates: Iterable[str] = CANDIDATE_INTERPRETERS,
    current: Optional[str] = None,
) -> Optional[str]:
    current_real = os.path.realpath(current or sys.executable)
    for candidate in candidates:
        executable = shutil.which(candidate)
        if not executable:
            continue
        if os.path.realpath(executable) == current_real:
            continue
        version = _probe(executable)
        if version and version >= MINIMUM_TK:
            return executable
    return None


def configure_platform() -> None:
    """Per-platform setup that must happen before the first Tk window exists."""
    if os.name != "nt":
        return
    try:
        import ctypes

        try:
            ctypes.windll.shcore.SetProcessDpiAwareness(1)  # per-monitor DPI aware
        except Exception:
            ctypes.windll.user32.SetProcessDPIAware()
        # Group the window under its own taskbar identity so the .ico is used.
        ctypes.windll.shell32.SetCurrentProcessExplicitAppUserModelID(
            "io.jphsystems.sshdashboard"
        )
    except Exception:
        pass


def _alert(message: str) -> None:
    """Best-effort native error dialog when Tk cannot be used."""
    if sys.platform == "darwin":
        try:
            script = 'display alert "SSH Dashboard" message "%s" as critical' % message.replace('"', "'")
            subprocess.run(["osascript", "-e", script], timeout=15, capture_output=True)
            return
        except (OSError, subprocess.SubprocessError):
            pass
    sys.stderr.write(message + "\n")


def ensure_supported_tk(script: Path, argv: Sequence[str] = ()) -> None:
    """Guarantee a Tk new enough to draw the UI, or exit with a clear message."""
    if tk_version() >= MINIMUM_TK:
        return
    if os.environ.get(RELAUNCH_ENV) != "1":
        interpreter = find_supported_interpreter()
        if interpreter:
            env = dict(os.environ)
            env[RELAUNCH_ENV] = "1"
            if os.name == "nt":
                # Windows has no execve: start the child and hand over.
                try:
                    subprocess.Popen([interpreter, str(script), *argv], env=env)
                    sys.exit(0)
                except OSError:
                    pass
            else:
                try:
                    os.execve(interpreter, [interpreter, str(script), *argv], env)
                except OSError:
                    pass
    version = tk_version()
    message = (
        f"SSH Dashboard needs Tk {MINIMUM_TK[0]}.{MINIMUM_TK[1]} or newer "
        f"(this interpreter has Tk {version[0]}.{version[1]}).\n\n"
        "Install one with:  brew install python-tk@3.14\n"
        "Then launch with:  ./run.sh"
    )
    _alert(message)
    print(message, file=sys.stderr)
    sys.exit(1)
