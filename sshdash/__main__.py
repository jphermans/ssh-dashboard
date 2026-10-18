"""Allow ``python -m sshdash`` (with the same Tk guard as the script)."""

from __future__ import annotations

import sys
from pathlib import Path

from .runtime import configure_platform, ensure_supported_tk

if __name__ == "__main__":
    ensure_supported_tk(Path(__file__).resolve(), sys.argv[1:])
    configure_platform()
    from .ui.app import main

    main()
