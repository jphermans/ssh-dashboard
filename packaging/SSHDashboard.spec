# -*- mode: python ; coding: utf-8 -*-
"""PyInstaller spec — builds a windowed app with the product icon.

macOS:   pyinstaller --noconfirm --clean packaging/SSHDashboard.spec
         → dist/SSH Dashboard.app
Windows: pyinstaller --noconfirm --clean packaging/SSHDashboard.spec
         → dist/SSH Dashboard/SSH Dashboard.exe
"""

import sys
from pathlib import Path

ROOT = Path(SPECPATH).resolve().parent
ASSETS = ROOT / "sshdash" / "ui" / "assets"
ICON = ASSETS / ("SSHDashboard.icns" if sys.platform == "darwin" else "SSHDashboard.ico")

a = Analysis(
    [str(ROOT / "ssh_dashboard.py")],
    pathex=[str(ROOT)],
    binaries=[],
    datas=[(str(ASSETS), "sshdash/ui/assets")],
    hiddenimports=[],
    hookspath=[],
    runtime_hooks=[],
    excludes=["pytest", "setuptools", "pip", "tkinter.test"],
    noarchive=False,
)

pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    [],
    exclude_binaries=True,
    name="SSH Dashboard",
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=False,
    console=False,
    icon=str(ICON),
    disable_windowed_traceback=False,
)

coll = COLLECT(
    exe,
    a.binaries,
    a.datas,
    strip=False,
    upx=False,
    name="SSH Dashboard",
)

if sys.platform == "darwin":
    app = BUNDLE(
        coll,
        name="SSH Dashboard.app",
        icon=str(ICON),
        bundle_identifier="io.jphsystems.sshdashboard",
        info_plist={
            "CFBundleName": "SSH Dashboard",
            "CFBundleDisplayName": "SSH Dashboard",
            "CFBundleShortVersionString": "1.0.0",
            "CFBundleVersion": "1",
            "NSHighResolutionCapable": True,
            "LSMinimumSystemVersion": "11.0",
            "NSHumanReadableCopyright": "© 2026 JPHsystems",
        },
    )
