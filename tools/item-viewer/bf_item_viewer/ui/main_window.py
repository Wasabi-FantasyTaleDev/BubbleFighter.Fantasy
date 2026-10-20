"""Main window: client selection, category/filter bars, the card grid, the detail dock and exports."""

from __future__ import annotations

import math
import os
from pathlib import Path

from PySide6.QtCore import QTimer, Qt, QUrl, Signal
from PySide6.QtGui import QAction, QColor, QDesktopServices
from PySide6.QtWidgets import (QApplication, QButtonGroup, QCheckBox, QComboBox, QDockWidget, QFileDialog, QHBoxLayout,
                               QLabel, QLineEdit, QMainWindow, QMenu, QMessageBox, QProgressDialog, QPushButton,
                               QScrollArea, QTabBar, QTabWidget, QToolBar, QToolButton, QVBoxLayout, QWidget)

from ..catalog.model import Status
from ..service import ClientSession
from . import config
from .cardstore import CardStore
from .detail import DetailPanel
from .diagnostics import DiagnosticsView
from .export_dialog import ExportDialog
from .filtering import (SORT_ID, SORT_NAME, SORT_PRICE, VIEW_ALL, VIEW_INVENTORY, VIEW_SHOP, base_filter, sort_items,
                        status_counts)
from .gridview import CardGrid
from .workers import ExportWorker, LoadWorker


class MainWindow(QMainWindow):
    session_ready = Signal()

    def __init__(self, cache_root: str | None = None):
        super().__init__()
        self.setWindowTitle("BF Item Viewer")
        self.resize(1500, 900)
        self.cache_root = cache_root
        self.session: ClientSession | None = None
        self.store: CardStore | None = None
        self.load_worker: LoadWorker | None = None
        self.export_worker: ExportWorker | None = None
        self._progress: QProgressDialog | None = None
        self._main_filter: str | None = None
        self._sub_filter: str | None = None
        self._refreshing = False
        self._status_checked: dict[Status, bool] = {s: True for s in Status}

        self._build_toolbar()
        self._build_central()
        self._build_detail_dock()
        self.page_label = QLabel("")
        self.statusBar().addPermanentWidget(self.page_label)
        self.statusBar().showMessage("Open a client folder to begin.")

        self._search_timer = QTimer(self)
        self._search_timer.setSingleShot(True)
        self._search_timer.setInterval(250)
        self._search_timer.timeout.connect(self.refresh)

    # -- construction ----------------------------------------------------------------------
    def _build_toolbar(self) -> None:
        bar = QToolBar("Client")
        bar.setMovable(False)
        self.addToolBar(bar)
        open_action = QAction("Open client folder...", self)
        open_action.triggered.connect(self.choose_folder)
        bar.addAction(open_action)
        self.recent_button = QToolButton()
        self.recent_button.setText("Recent folders")
        self.recent_button.setPopupMode(QToolButton.ToolButtonPopupMode.InstantPopup)
        self.recent_menu = QMenu(self.recent_button)
        self.recent_button.setMenu(self.recent_menu)
        bar.addWidget(self.recent_button)
        self.rescan_action = QAction("Rescan", self)
        self.rescan_action.setEnabled(False)
        self.rescan_action.triggered.connect(self.rescan)
        bar.addAction(self.rescan_action)
        self._rebuild_recent_menu()

    def _build_central(self) -> None:
        self.tabs = QTabWidget()
        self.setCentralWidget(self.tabs)

        items_page = QWidget()
        v = QVBoxLayout(items_page)
        v.setContentsMargins(4, 4, 4, 4)

        self.category_bar = QTabBar()
        self.category_bar.setExpanding(False)
        self.category_bar.setUsesScrollButtons(True)
        self.category_bar.currentChanged.connect(self._category_changed)
        v.addWidget(self.category_bar)

        self.sub_scroll = QScrollArea()
        self.sub_scroll.setFixedHeight(54)
        self.sub_scroll.setWidgetResizable(True)
        self.sub_scroll.setFrameShape(QScrollArea.Shape.NoFrame)
        self.sub_scroll.setVerticalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAlwaysOff)
        self.sub_holder = QWidget()
        self.sub_layout = QHBoxLayout(self.sub_holder)
        self.sub_layout.setContentsMargins(0, 0, 0, 0)
        self.sub_scroll.setWidget(self.sub_holder)
        self.sub_group = QButtonGroup(self)
        self.sub_group.setExclusive(True)
        self.sub_group.buttonClicked.connect(self._sub_clicked)
        v.addWidget(self.sub_scroll)

        row = QHBoxLayout()
        self.search = QLineEdit()
        self.search.setPlaceholderText("Search name or id")
        self.search.setClearButtonEnabled(True)
        self.search.textChanged.connect(lambda _t: self._search_timer.start())
        row.addWidget(self.search, 1)
        self.sort_combo = QComboBox()
        for label, key in (("Sort: id", SORT_ID), ("Sort: name", SORT_NAME), ("Sort: price", SORT_PRICE)):
            self.sort_combo.addItem(label, key)
        self.sort_combo.currentIndexChanged.connect(self.refresh)
        row.addWidget(self.sort_combo)
        self.descending = QCheckBox("Descending")
        self.descending.toggled.connect(self.refresh)
        row.addWidget(self.descending)
        self.view_combo = QComboBox()
        for label, key in (("All items", VIEW_ALL), ("Shop view", VIEW_SHOP), ("Inventory view", VIEW_INVENTORY)):
            self.view_combo.addItem(label, key)
        self.view_combo.currentIndexChanged.connect(self._view_changed)
        row.addWidget(self.view_combo)
        self.status_button = QToolButton()
        self.status_button.setText("Status filter")
        self.status_button.setPopupMode(QToolButton.ToolButtonPopupMode.InstantPopup)
        self.status_menu = QMenu(self.status_button)
        self.status_actions: dict[Status, QAction] = {}
        for status in Status:
            action = QAction(status.value, self.status_menu)
            action.setCheckable(True)
            action.setChecked(True)
            action.toggled.connect(self._status_toggled)
            self.status_menu.addAction(action)
            self.status_actions[status] = action
        self.status_button.setMenu(self.status_menu)
        row.addWidget(self.status_button)
        v.addLayout(row)

        row2 = QHBoxLayout()
        self.select_all_button = QPushButton("Select all visible")
        self.select_all_button.clicked.connect(self._select_all)
        self.clear_button = QPushButton("Clear selection")
        self.clear_button.clicked.connect(self._clear_selection)
        self.selection_label = QLabel("0 selected")
        self.export_selected_button = QPushButton("Export selected...")
        self.export_selected_button.clicked.connect(lambda: self.export_dialog_flow("selected"))
        self.export_visible_button = QPushButton("Export all visible...")
        self.export_visible_button.clicked.connect(lambda: self.export_dialog_flow("visible"))
        self.export_all_button = QPushButton("Export all catalog items...")
        self.export_all_button.clicked.connect(lambda: self.export_dialog_flow("all"))
        for w in (self.select_all_button, self.clear_button, self.selection_label):
            row2.addWidget(w)
        row2.addStretch(1)
        for w in (self.export_selected_button, self.export_visible_button, self.export_all_button):
            row2.addWidget(w)
        v.addLayout(row2)

        self.grid = CardGrid()
        self.grid.current_item_changed.connect(self._current_changed)
        self.grid.view_range_changed.connect(self._update_page_label)
        self.grid.card_model.selection_changed.connect(self._update_selection_label)
        self.grid.card_model.modelReset.connect(self._update_selection_label)
        v.addWidget(self.grid, 1)

        self.tabs.addTab(items_page, "Items")
        self.diagnostics = DiagnosticsView()
        self.diagnostics.show_session(None)
        self.tabs.addTab(self.diagnostics, "Diagnostics")
        self._set_controls_enabled(False)

    def _build_detail_dock(self) -> None:
        self.detail = DetailPanel()
        dock = QDockWidget("Item detail", self)
        dock.setWidget(self.detail)
        dock.setFeatures(QDockWidget.DockWidgetFeature.DockWidgetMovable | QDockWidget.DockWidgetFeature.DockWidgetFloatable)
        self.addDockWidget(Qt.DockWidgetArea.RightDockWidgetArea, dock)
        self.detail_dock = dock
        self.resizeDocks([dock], [500], Qt.Orientation.Horizontal)

    def _set_controls_enabled(self, on: bool) -> None:
        for w in (self.search, self.sort_combo, self.descending, self.view_combo, self.status_button, self.select_all_button,
                  self.clear_button, self.export_selected_button, self.export_visible_button, self.export_all_button,
                  self.category_bar):
            w.setEnabled(on)
        self.rescan_action.setEnabled(on)

    # -- recent folders / opening ----------------------------------------------------------
    def _rebuild_recent_menu(self) -> None:
        self.recent_menu.clear()
        recent = config.recent_folders()
        if not recent:
            self.recent_menu.addAction("(none)").setEnabled(False)
        for folder in recent:
            action = self.recent_menu.addAction(folder)
            action.triggered.connect(lambda _checked=False, f=folder: self.open_client(f))
        if recent:
            self.recent_menu.addSeparator()
            self.recent_menu.addAction("Clear list").triggered.connect(self._clear_recent)

    def _clear_recent(self) -> None:
        cfg = config.load_config()
        cfg["recent_folders"] = []
        config.save_config(cfg)
        self._rebuild_recent_menu()

    def choose_folder(self) -> None:
        start = config.recent_folders()[0] if config.recent_folders() else ""
        folder = QFileDialog.getExistingDirectory(self, "Open client folder", start)
        if folder:
            self.open_client(folder)

    def rescan(self) -> None:
        if self.session is not None:
            self.open_client(str(self.session.client_root), rescan=True)

    def open_client(self, folder: str, rescan: bool = False) -> None:
        if self.load_worker is not None and self.load_worker.isRunning():
            return
        if self.export_worker is not None and self.export_worker.isRunning():
            QMessageBox.information(self, "Export running", "Wait for the running export to finish first.")
            return
        if not Path(folder).is_dir():
            QMessageBox.critical(self, "Cannot open folder", f"{folder} is not a folder.")
            return
        self._release_session()
        worker = LoadWorker(folder, self.cache_root, rescan, self)
        self.load_worker = worker
        dialog = QProgressDialog(f"Opening {folder} ...", "Cancel", 0, 0, self)
        dialog.setWindowTitle("Loading client")
        dialog.setWindowModality(Qt.WindowModality.WindowModal)
        dialog.setMinimumDuration(0)
        dialog.setAutoClose(False)
        dialog.setAutoReset(False)
        dialog.setMinimumWidth(480)
        dialog.canceled.connect(worker.cancel)
        self._progress = dialog
        worker.progress.connect(self._on_load_progress)
        worker.loaded.connect(lambda session: self._on_loaded(session, folder))
        worker.failed.connect(self._on_load_failed)
        worker.cancelled.connect(self._on_load_cancelled)
        worker.finished.connect(self._close_progress)
        dialog.show()
        worker.start()

    def _on_load_progress(self, stage: str, done: int, total: int) -> None:
        dialog = self._progress
        if dialog is None:
            return
        dialog.setLabelText(stage)
        if total > 1:
            dialog.setRange(0, total)
            dialog.setValue(min(done, total))
        else:
            dialog.setRange(0, 0)

    def _close_progress(self) -> None:
        if self._progress is not None:
            self._progress.hide()
            self._progress.deleteLater()
            self._progress = None

    def _on_load_failed(self, message: str) -> None:
        self._close_progress()
        self.statusBar().showMessage("Loading failed.")
        QMessageBox.critical(self, "Cannot load client", message)

    def _on_load_cancelled(self) -> None:
        self._close_progress()
        self.statusBar().showMessage("Loading cancelled.")

    def _release_session(self) -> None:
        if self.store is not None:
            self.store.shutdown()
        self.detail.set_session(None)
        self.grid.set_items([])
        self.grid.card_model.set_store(None)
        self.store = None
        self.session = None
        self.diagnostics.show_session(None)
        self._set_controls_enabled(False)

    def _on_loaded(self, session: ClientSession, folder: str) -> None:
        self._close_progress()
        self.session = session
        self.store = CardStore(session, workers=3)
        self.grid.set_store(self.store)
        self.detail.set_session(session)
        self.diagnostics.show_session(session)
        config.add_recent(str(session.client_root))
        self._rebuild_recent_menu()
        self.setWindowTitle(f"BF Item Viewer - {session.build} - {session.client_root}")
        self._populate_categories()
        self._set_controls_enabled(True)
        self.statusBar().showMessage(f"{session.build}: {len(session.catalog.items)} items", 8000)
        self.session_ready.emit()

    # -- categories and filters --------------------------------------------------------------
    def _populate_categories(self) -> None:
        catalog = self.session.catalog
        self._refreshing = True
        while self.category_bar.count():
            self.category_bar.removeTab(0)
        self.category_bar.addTab(f"All ({len(catalog.items)})")
        self.category_bar.setTabData(0, None)
        used = {i.main_category for i in catalog.items}
        for cat in sorted(catalog.main_categories, key=lambda c: c.order):
            if cat.name in used:
                n = sum(1 for i in catalog.items if i.main_category == cat.name)
                index = self.category_bar.addTab(f"{cat.display or cat.name} ({n})")
                self.category_bar.setTabData(index, cat.name)
                self.category_bar.setTabToolTip(index, cat.name)
        self.category_bar.setCurrentIndex(0)
        self._main_filter = None
        self._sub_filter = None
        self._refreshing = False
        self._populate_subcategories()
        self.refresh()

    def _category_changed(self, index: int) -> None:
        if self._refreshing or self.session is None:
            return
        self._main_filter = self.category_bar.tabData(index)
        self._sub_filter = None
        self._populate_subcategories()
        self.refresh()

    def _populate_subcategories(self) -> None:
        for button in list(self.sub_group.buttons()):
            self.sub_group.removeButton(button)
            self.sub_layout.removeWidget(button)
            button.deleteLater()
        while self.sub_layout.count():
            self.sub_layout.takeAt(0)
        catalog = self.session.catalog
        names = {}
        for item in catalog.items:
            if self._main_filter is None or item.main_category == self._main_filter:
                if item.sub_category:
                    names[item.sub_category] = None
        display = {c.name: (c.display or c.name) for c in catalog.sub_categories}
        entries = [("All", None, "")] + [(display.get(n, n), n, n) for n in sorted(names)]
        for label, value, tip in entries:
            button = QPushButton(label)
            button.setCheckable(True)
            button.setToolTip(tip)
            button.setProperty("sub", value)
            button.setChecked(value is None)
            self.sub_group.addButton(button)
            self.sub_layout.addWidget(button)
        self.sub_layout.addStretch(1)

    def _sub_clicked(self, button) -> None:
        self._sub_filter = button.property("sub")
        self.refresh()

    def _view_changed(self) -> None:
        self.refresh()

    def _status_toggled(self, _checked: bool) -> None:
        if not self._refreshing:
            self.refresh()

    def refresh(self, *_args) -> None:
        """Re-apply every filter and sort, then hand the result to the grid."""
        if self.session is None or self._refreshing:
            return
        self._refreshing = True
        catalog = self.session.catalog
        base = base_filter(catalog, main=self._main_filter, sub=self._sub_filter, query=self.search.text(),
                           view_mode=self.view_combo.currentData())
        counts = status_counts(base)
        for status, action in self.status_actions.items():
            action.setText(f"{status.value} ({counts[status]})")
        allowed = {s for s, a in self.status_actions.items() if a.isChecked()}
        items = [i for i in base if i.status in allowed]
        items = sort_items(items, self.sort_combo.currentData(), self.descending.isChecked())
        self.status_button.setText(f"Status filter ({len(allowed)}/{len(Status)})")
        self._refreshing = False
        self.grid.set_items(items)
        self.statusBar().showMessage(f"{len(items)} of {len(catalog.items)} items shown", 5000)

    # -- selection and detail ----------------------------------------------------------------
    def _current_changed(self, item) -> None:
        self.detail.show_item(item)

    def _select_all(self) -> None:
        self.grid.card_model.select_all_visible()

    def _clear_selection(self) -> None:
        self.grid.card_model.clear_selection()

    def _update_selection_label(self) -> None:
        self.selection_label.setText(f"{len(self.grid.card_model.selected)} selected")

    def _update_page_label(self) -> None:
        count = len(self.grid.card_model.items)
        if count == 0:
            self.page_label.setText("No items")
            return
        first, last = self.grid.visible_rows()
        per_page = max(1, self.grid.viewport().height() // max(1, self.grid._cell.height())) * self.grid.columns()
        self.page_label.setText(f"Items {first + 1}-{last + 1} of {count}    Page {first // per_page + 1} / "
                                f"{math.ceil(count / per_page)}")

    # -- export ----------------------------------------------------------------------------
    def export_dialog_flow(self, scope: str) -> None:
        if self.session is None or self.export_worker is not None:
            return
        model = self.grid.card_model
        if scope == "selected":
            ids, text = model.selected_ids(), "the selected cards"
        elif scope == "visible":
            ids, text = [i.item_id for i in model.items], "everything currently shown"
        else:
            ids, text = [i.item_id for i in self.session.catalog.items], "the whole catalog"
        if not ids:
            QMessageBox.information(self, "Nothing to export", "No items in this scope.")
            return
        palette = sorted(self.session.palette())
        default_dir = str(config.load_config().get("export_dir", ""))
        dialog = ExportDialog(self, item_count=len(ids), scope=text, palette_size=len(palette),
                              detail_wearer=self.detail.current_wearer(), default_dir=default_dir)
        if dialog.exec() != dialog.DialogCode.Accepted:
            return
        choice = dialog.choice(palette)
        if scope == "all":
            answer = QMessageBox.question(
                self, "Export the whole catalog",
                f"This will run {dialog.job_count()} render job(s) for {len(ids)} items into\n{choice.out_dir}\n\nContinue?")
            if answer != QMessageBox.StandardButton.Yes:
                return
        cfg = config.load_config()
        cfg["export_dir"] = choice.out_dir
        config.save_config(cfg)
        self.start_export(ids, choice.out_dir, choice.options, dialog.job_count())

    def start_export(self, ids: list[int], out_dir: str, options, jobs: int) -> None:
        """Run the export in a worker thread with a cancellable progress dialog."""
        if self.store is not None:
            self.store.pause()          # the export may switch shared camera state; keep other renders out
        self.detail.suspend(True)
        worker = ExportWorker(self.session, ids, out_dir, options, self)
        self.export_worker = worker
        dialog = QProgressDialog("Exporting...", "Cancel", 0, max(1, jobs), self)
        dialog.setWindowTitle("Export")
        dialog.setWindowModality(Qt.WindowModality.WindowModal)
        dialog.setMinimumDuration(0)
        dialog.setAutoClose(False)
        dialog.setAutoReset(False)
        dialog.setMinimumWidth(420)
        dialog.canceled.connect(worker.cancel)
        self._progress = dialog
        worker.progress.connect(self._on_export_progress)
        worker.finished_ok.connect(self._on_export_done)
        worker.failed.connect(self._on_export_failed)
        worker.finished.connect(self._export_finished)
        dialog.show()
        worker.start()

    def _on_export_progress(self, done: int, total: int, label: str) -> None:
        if self._progress is not None:
            self._progress.setMaximum(max(1, total))
            self._progress.setValue(min(done, total))
            self._progress.setLabelText(f"{done} / {total}  {label}")

    def _export_finished(self) -> None:
        self._close_progress()
        self.export_worker = None
        if self.store is not None:
            self.store.resume()
            self.grid.viewport().update()
        self.detail.suspend(False)

    def _on_export_failed(self, message: str) -> None:
        self._close_progress()
        QMessageBox.critical(self, "Export failed", message)

    def _on_export_done(self, manifest: str, counts: dict) -> None:
        self._close_progress()
        folder = str(Path(manifest).parent)
        head = "Export cancelled." if counts.get("cancelled") else "Export finished."
        box = QMessageBox(QMessageBox.Icon.Information, "Export", self)
        box.setText(f"{head}\n\nWritten: {counts['written']}\nSkipped: {counts['skipped']}\nFailed: {counts['failed']}\n"
                    f"Jobs: {counts['completed']} of {counts['requested']}\n\nManifest:\n{manifest}")
        open_button = box.addButton("Open folder", QMessageBox.ButtonRole.ActionRole)
        box.addButton(QMessageBox.StandardButton.Close)
        box.exec()
        if box.clickedButton() is open_button:
            QDesktopServices.openUrl(QUrl.fromLocalFile(folder))

    # -- shutdown --------------------------------------------------------------------------
    def closeEvent(self, event) -> None:
        for worker in (self.load_worker, self.export_worker):
            if worker is not None and worker.isRunning():
                worker.cancel()
                worker.wait(30000)
        if self.store is not None:
            self.store.shutdown()
        self.detail.wait_idle()
        super().closeEvent(event)
