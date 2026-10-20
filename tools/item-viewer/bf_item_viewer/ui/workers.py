"""Background threads: session loading and batch export. Workers only emit signals; they never touch widgets."""

from __future__ import annotations

import json
import threading
from pathlib import Path

from PySide6.QtCore import QThread, Signal

from ..export.batch import ExportOptions, export_items
from ..service import ClientSession


class LoadWorker(QThread):
    """Open a client folder, index it (incrementally) and build or load the catalog."""

    progress = Signal(str, int, int)
    loaded = Signal(object)        # ClientSession
    failed = Signal(str)
    cancelled = Signal()

    def __init__(self, client_root: str, cache_root: str | None, rescan: bool = False, parent=None):
        super().__init__(parent)
        self.client_root = client_root
        self.cache_root = cache_root
        self.rescan = rescan
        self.cancel_event = threading.Event()

    def cancel(self) -> None:
        self.cancel_event.set()

    def run(self) -> None:
        try:
            session = ClientSession(self.client_root, self.cache_root)
            session.load(progress=lambda stage, done, total: self.progress.emit(stage, int(done), int(total)),
                         cancel=self.cancel_event, rescan=self.rescan)
            if self.cancel_event.is_set():
                self.cancelled.emit()
                return
            self.loaded.emit(session)
        except InterruptedError:
            self.cancelled.emit()
        except Exception as error:     # shown to the user in a message box
            self.failed.emit(f"{type(error).__name__}: {error}")


class ExportWorker(QThread):
    """Run :func:`export_items` off the UI thread."""

    progress = Signal(int, int, str)
    finished_ok = Signal(str, dict)    # manifest path, counts
    failed = Signal(str)

    def __init__(self, session: ClientSession, item_ids: list[int], out_dir: str, options: ExportOptions, parent=None):
        super().__init__(parent)
        self.session = session
        self.item_ids = list(item_ids)
        self.out_dir = out_dir
        self.options = options
        self.cancel_event = threading.Event()

    def cancel(self) -> None:
        self.cancel_event.set()

    def run(self) -> None:
        try:
            path = export_items(self.session, self.item_ids, self.out_dir, self.options,
                                progress=lambda done, total, label: self.progress.emit(int(done), int(total), str(label)),
                                cancel=self.cancel_event)
            self.finished_ok.emit(str(path), summarize_manifest(path))
        except Exception as error:
            self.failed.emit(f"{type(error).__name__}: {error}")


def summarize_manifest(path: Path | str) -> dict:
    """Counts of written / skipped / failed entries plus job totals."""
    data = json.loads(Path(path).read_text(encoding="utf-8"))
    counts = {"written": 0, "skipped": 0, "failed": 0}
    for entry in data.get("entries", []):
        status = entry.get("status", "failed")
        counts[status] = counts.get(status, 0) + 1
    counts["requested"] = data.get("requested_jobs", 0)
    counts["completed"] = data.get("completed_jobs", 0)
    counts["cancelled"] = bool(data.get("cancelled"))
    return counts
