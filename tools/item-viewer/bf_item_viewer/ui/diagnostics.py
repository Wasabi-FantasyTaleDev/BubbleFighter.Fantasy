"""Diagnostics tab: session summary, catalog problems and archive status."""

from __future__ import annotations

import json

from PySide6.QtWidgets import QHeaderView, QPlainTextEdit, QSplitter, QTableWidget, QTableWidgetItem, QWidget, QVBoxLayout
from PySide6.QtCore import Qt

from ..service import ClientSession


class DiagnosticsView(QWidget):
    def __init__(self, parent=None):
        super().__init__(parent)
        self.text = QPlainTextEdit()
        self.text.setReadOnly(True)
        self.text.setLineWrapMode(QPlainTextEdit.LineWrapMode.NoWrap)
        self.table = QTableWidget(0, 4)
        self.table.setHorizontalHeaderLabels(["Archive", "Kind", "Members", "Error"])
        self.table.horizontalHeader().setSectionResizeMode(0, QHeaderView.ResizeMode.Stretch)
        self.table.setEditTriggers(QTableWidget.EditTrigger.NoEditTriggers)
        split = QSplitter(Qt.Orientation.Vertical)
        split.addWidget(self.text)
        split.addWidget(self.table)
        split.setSizes([300, 300])
        layout = QVBoxLayout(self)
        layout.addWidget(split)

    def show_session(self, session: ClientSession | None) -> None:
        if session is None:
            self.text.setPlainText("No client loaded.")
            self.table.setRowCount(0)
            return
        parts = ["== Session summary ==", json.dumps(session.summary(), indent=2, ensure_ascii=False, default=str),
                 "", "== Catalog problems =="]
        problems = session.catalog.problems if session.catalog else []
        parts += [f"- {p}" for p in problems] or ["(none)"]
        parts += ["", "== Camera table problems =="]
        parts += [f"- {p}" for p in session.camera_problems] or ["(none)"]
        self.text.setPlainText("\n".join(parts))
        statuses = session.index.archive_statuses()
        self.table.setRowCount(len(statuses))
        for row, st in enumerate(statuses):
            for col, value in enumerate((st.relative_path, st.kind, str(st.members), st.error or "")):
                self.table.setItem(row, col, QTableWidgetItem(value))
