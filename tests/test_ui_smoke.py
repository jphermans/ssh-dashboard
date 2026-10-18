"""UI smoke test: builds the real window once and drives it.

macOS Tk does not tolerate creating and destroying several ``Tk()`` roots in one
process, so the window is created once for the whole class and reset between
tests. Nothing is deleted from ``~/.ssh``.
"""

from __future__ import annotations

import subprocess
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))


def _display_available() -> bool:
    """Probe in a throwaway process so a missing display cannot crash the suite."""
    import os

    if os.environ.get("SSHDASH_SKIP_UI") == "1":
        return False
    try:
        result = subprocess.run(
            [sys.executable, "-c", "import tkinter; r = tkinter.Tk(); r.update(); r.destroy()"],
            capture_output=True,
            timeout=20,
        )
    except (OSError, subprocess.SubprocessError):
        return False
    return result.returncode == 0


HAS_DISPLAY = _display_available()


@unittest.skipUnless(HAS_DISPLAY, "no display available")
class DashboardSmokeTests(unittest.TestCase):
    app = None

    @classmethod
    def setUpClass(cls) -> None:
        from sshdash.ui.app import DashboardApp

        cls.app = DashboardApp()
        cls.app.update()

    @classmethod
    def tearDownClass(cls) -> None:
        if cls.app is not None:
            cls.app.destroy()

    def setUp(self) -> None:
        self.app._switch_view("hosts")
        self.app._clear_search()
        self.app._clear_selection()
        self.app.update()

    def test_every_view_renders(self) -> None:
        for view in ("hosts", "config", "keys", "activity"):
            self.app._switch_view(view)
            self.app.update()
            self.assertEqual(self.app.view, view)
            self.assertIsNotNone(self.app.tree)
        self.app._switch_view("hosts")

    def test_selection_and_footer(self) -> None:
        iids = list(self.app._row_lookup)
        self.assertTrue(iids, "expected hosts from ~/.ssh")
        first = iids[0]
        self.app._toggle_row(first)
        self.assertIn(first, self.app.selected["hosts"])
        self.assertTrue(self.app.delete_btn._enabled)
        self.app._toggle_row(first)
        self.assertEqual(self.app.selected["hosts"], set())

    def test_select_all_and_clear(self) -> None:
        self.app._toggle_all()
        self.assertEqual(len(self.app.selected["hosts"]), len(self.app.rows))
        self.app._clear_selection()
        self.assertEqual(self.app.selected["hosts"], set())

    def test_detail_panel_for_each_view(self) -> None:
        for view in ("hosts", "config", "keys", "activity"):
            self.app._switch_view(view)
            for iid in list(self.app._row_lookup)[:3]:
                self.app.tree.selection_set(iid)
                self.app.update()
                self.assertTrue(self.app.rail_body.winfo_children())
        self.app._switch_view("hosts")

    def test_search_filters_and_empty_state(self) -> None:
        self.app.search.set("zzzz-no-such-host")
        self.app.query = "zzzz-no-such-host"
        self.app._render_table()
        self.app.update()
        self.assertEqual(self.app.rows, [])
        self.assertIsNotNone(self.app._empty_overlay)
        self.app._clear_search()
        self.app.update()
        self.assertTrue(self.app.rows)

    def test_sorting_toggles(self) -> None:
        self.app._sort_by("address")
        self.assertEqual(self.app.sort["hosts"], ("address", False))
        self.app._sort_by("address")
        self.assertEqual(self.app.sort["hosts"], ("address", True))
        self.app._sort_by("label")

    def test_theme_toggle_rebuilds(self) -> None:
        before = self.app.palette.key
        self.app._toggle_theme()
        self.app.update()
        self.assertNotEqual(self.app.palette.key, before)
        self.app._toggle_theme()
        self.app.update()
        self.assertEqual(self.app.palette.key, before)

    def test_menubar_and_state(self) -> None:
        labels = [
            self.app.menubar.entrycget(index, "label")
            for index in range((self.app.menubar.index("end") or 0) + 1)
            if self.app.menubar.type(index) == "cascade"
        ]
        for expected in ("File", "Edit", "View", "Tools", "Help"):
            self.assertIn(expected, labels)
        self.app._switch_view("config")
        editable = [row for row in self.app.rows if row["payload"].deletable]
        self.assertTrue(editable)
        self.app.tree.selection_set(editable[0]["iid"])
        self.app.update()
        self.assertIsNotNone(self.app._selected_block())
        self.assertEqual(
            self.app.tools_menu.entrycget("Edit Selected Block\u2026", "state"), "normal"
        )

    def test_view_menu_switches_views(self) -> None:
        self.app.view_menu.invoke("Keys")
        self.app.update()
        self.assertEqual(self.app.view, "keys")
        self.app.view_menu.invoke("Known hosts")
        self.app.update()
        self.assertEqual(self.app.view, "hosts")

    def test_settings_dialog_returns_values(self) -> None:
        from sshdash.ui.widgets import SettingsDialog

        dialog = SettingsDialog(
            self.app, palette=self.app.palette, fonts=self.app.fonts,
            theme="dark", confirm_before_delete=True, remove_config_default=True,
            ssh_dir=str(self.app.store.ssh_dir), backup_dir=str(self.app.store.backup_dir),
            backup_count=2,
        )
        dialog.present()
        self.app.update()
        dialog._select_theme("light")
        dialog._confirm_var.set(False)
        dialog._save()
        self.app.update()
        self.assertEqual(
            dialog.result,
            {"theme": "light", "confirm_before_delete": False, "remove_config_default": True},
        )

    def test_config_editor_prefills_fields(self) -> None:
        from sshdash.ui.widgets import ConfigBlockDialog

        self.app._switch_view("config")
        block = next(row["payload"] for row in self.app.rows if row["payload"].deletable)
        dialog = ConfigBlockDialog(
            self.app, palette=self.app.palette, fonts=self.app.fonts,
            block=block, on_save=lambda *args: None,
        )
        self.app.update()
        self.assertEqual(dialog._fields["host"].get(), " ".join(block.patterns))
        self.assertEqual(
            dialog._fields["hostname"].get(), block.option("hostname") or ""
        )
        dialog.cancel()
        self.app.update()

    def test_export_writes_csv(self) -> None:
        import tempfile

        from tkinter import filedialog

        self.app._switch_view("hosts")
        original = filedialog.asksaveasfilename
        target = Path(tempfile.mkdtemp()) / "out.csv"
        filedialog.asksaveasfilename = lambda **kwargs: str(target)
        try:
            self.app._export_rows()
        finally:
            filedialog.asksaveasfilename = original
        self.assertTrue(target.exists())
        header = target.read_text().splitlines()[0]
        self.assertIn("Host", header)
        self.assertIn("Address", header)

    def test_dialog_constructs(self) -> None:
        from sshdash.ui.widgets import ConfirmDeleteDialog

        dialog = ConfirmDeleteDialog(
            self.app, palette=self.app.palette, fonts=self.app.fonts,
            hosts=["alpha", "beta"], target_label="2 hosts",
            checkbox_label="Also remove 1 matching ssh_config host block",
            checkbox_default=True,
        )
        dialog.present()
        self.app.update()
        self.assertTrue(dialog.winfo_exists())
        dialog.cancel()
        self.app.update()

    def test_window_minimum_fits_table(self) -> None:
        self.app.geometry("1140x700")
        self.app.update()
        self.assertGreater(self.app.tree.winfo_width(), 380)
        self.app.geometry("1400x860")
        self.app.update()


if __name__ == "__main__":
    unittest.main(verbosity=2)
