#!/usr/bin/env python3
"""SSH Dashboard — entry point.

Run with ``./run.sh`` (picks the best Python with Tk), or directly::

    python3.14 ssh_dashboard.py

``--check`` prints the runtime environment without opening a window.
"""

from __future__ import annotations

import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(PROJECT_ROOT))

from sshdash.runtime import configure_platform, ensure_supported_tk, tk_version  # noqa: E402


def _check() -> int:
    import platform

    from sshdash import __version__
    from sshdash.config import support_dir
    from sshdash.store import SshStore

    store = SshStore()
    version = tk_version()
    print(f"SSH Dashboard {__version__}")
    print(f"Python        {platform.python_version()} ({sys.executable})")
    print(f"Tk            {version[0]}.{version[1]}  "
          f"({'supported' if version >= (8, 6) else 'too old'})")
    print(f"Platform      {platform.platform()}")
    print(f"SSH folder    {store.ssh_dir}  "
          f"({'exists' if store.ssh_dir.is_dir() else 'missing'})")
    print(f"Backups       {store.backup_dir} ({len(store.backups())})")
    print(f"Settings      {support_dir()}")
    return 0 if version >= (8, 6) else 1


def main() -> None:
    if "--check" in sys.argv[1:]:
        sys.exit(_check())
    ensure_supported_tk(Path(__file__).resolve(), sys.argv[1:])
    configure_platform()
    from sshdash.ui.app import main as run

    run()


if __name__ == "__main__":
    main()
