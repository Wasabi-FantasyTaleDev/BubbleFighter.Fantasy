"""Offscreen smoke test of the GUI against both test clients.

Run: ``.venv\Scripts\python tools\gui_smoke.py``; set BF_KR812_CLIENT / BF_KR1256_CLIENT to the client folders. Saves screenshots to ``output/screens`` and runs a
3-item export through the same worker the export dialog uses.
"""

from __future__ import annotations

import os
import sys
import time
from pathlib import Path

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")
os.environ.setdefault("QT_QPA_FONTDIR", r"C:\Windows\Fonts")   # the offscreen platform finds no fonts otherwise
ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))

from PySide6.QtCore import QCoreApplication, QEventLoop  # noqa: E402
from PySide6.QtWidgets import QApplication  # noqa: E402

from bf_item_viewer.export.batch import ExportOptions  # noqa: E402
from bf_item_viewer.ui.main_window import MainWindow  # noqa: E402
from bf_item_viewer.ui.theme import apply_dark_theme  # noqa: E402
from bf_item_viewer.ui.workers import ExportWorker  # noqa: E402

CLIENTS = {"kr812": os.environ.get("BF_KR812_CLIENT", r"D:\Software\Games\BFLegacy_KR2015"),
           "kr1256": os.environ.get("BF_KR1256_CLIENT", r"D:\Software\Games\BFKorea\BubbleFighter")}
CACHE = str(ROOT / ".cache")
SCREENS = ROOT / "output" / "screens"


def wait_until(predicate, timeout: float, what: str) -> None:
    end = time.time() + timeout
    while time.time() < end:
        QCoreApplication.processEvents(QEventLoop.ProcessEventsFlag.AllEvents, 50)
        if predicate():
            return
        time.sleep(0.02)
    raise TimeoutError(f"timed out waiting for {what}")


def run_client(app: QApplication, tag: str, folder: str) -> MainWindow:
    window = MainWindow(cache_root=CACHE)
    window.resize(1500, 900)
    window.show()
    ready = []
    window.session_ready.connect(lambda: ready.append(1))
    window.open_client(folder)
    wait_until(lambda: ready, 180, f"{tag} load")
    print(f"[{tag}] build={window.session.build} items={len(window.session.catalog.items)}")
    wait_until(lambda: window.store.render_count >= 9, 120, f"{tag} 9 card renders")
    wait_until(lambda: window.store.pending_count() == 0, 120, f"{tag} idle renders")
    for _ in range(10):
        QCoreApplication.processEvents()
    print(f"[{tag}] card renders={window.store.render_count}")
    SCREENS.mkdir(parents=True, exist_ok=True)
    window.grab().save(str(SCREENS / f"grid-{tag}.png"))
    return window


def select_and_detail(window: MainWindow, tag: str) -> list[int]:
    model = window.grid.card_model
    rows = [n for n, item in enumerate(model.items) if item.is_3d][:3]
    for row in rows:
        model.toggle(row)
    window.grid.current_item_changed.emit(model.items[rows[0]])
    wait_until(lambda: window.detail.last_metadata, 120, "detail render")
    # Drag rotation and reset.
    window.detail._on_drag(40, 0)
    window.detail._on_release()
    wait_until(lambda: window.detail.last_metadata.get("camera", {}).get("preset") == "user-rotated", 60, "rotated render")
    print(f"[{tag}] rotated preset:", window.detail.last_metadata["camera"]["preset"])
    window.detail.reset_view()
    wait_until(lambda: window.detail.last_metadata.get("camera", {}).get("preset") == "official", 60, "official render")
    for _ in range(10):
        QCoreApplication.processEvents()
    window.grab().save(str(SCREENS / f"detail-{tag}.png"))
    return [model.items[r].item_id for r in rows]


def run_export(window: MainWindow, ids: list[int], tag: str) -> None:
    out = ROOT / "output" / "gui-export-test" / tag
    options = ExportOptions(width=256, height=256, mode="both", overwrite="overwrite")
    window.store.pause()
    worker = ExportWorker(window.session, ids, str(out), options)
    result = {}
    worker.finished_ok.connect(lambda path, counts: result.update(path=path, counts=counts))
    worker.failed.connect(lambda message: result.update(error=message))
    worker.start()
    wait_until(lambda: result, 300, "export")
    worker.wait()
    window.store.resume()
    print(f"[{tag}] export:", result)
    assert "error" not in result and result["counts"]["written"] >= len(ids), result


def main() -> int:
    app = QApplication.instance() or QApplication(sys.argv[:1])
    apply_dark_theme(app)
    for tag, folder in CLIENTS.items():
        window = run_client(app, tag, folder)
        # exercise filters: search, sort and a category tab
        window.search.setText("a")
        window.refresh()
        window.search.setText("")
        window.category_bar.setCurrentIndex(min(2, window.category_bar.count() - 1))
        wait_until(lambda: window.store.render_count >= 1, 60, "filtered renders")
        window.category_bar.setCurrentIndex(0)
        window.refresh()
        wait_until(lambda: window.store.pending_count() == 0, 120, "idle")
        ids = select_and_detail(window, tag)
        run_export(window, ids, tag)
        window.close()
        window.deleteLater()
    print("smoke ok")
    return 0


if __name__ == "__main__":
    sys.exit(main())
