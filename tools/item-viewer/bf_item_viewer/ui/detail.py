"""Detail panel: large preview with drag rotation, item metadata, wearer / spray choice and provenance."""

from __future__ import annotations

import json
import math

from PIL import Image
from PySide6.QtCore import QObject, QRunnable, Qt, QThreadPool, Signal
from PySide6.QtGui import QColor, QIcon, QPainter, QPixmap
from PySide6.QtWidgets import (QComboBox, QFormLayout, QHBoxLayout, QLabel, QPlainTextEdit, QPushButton, QScrollArea,
                               QSizePolicy, QVBoxLayout, QWidget)

from ..cards.card import load_layout
from ..catalog.model import CatalogItem
from ..export.batch import ExportOptions, render_icon
from ..service import ClientSession, PreviewRequest
from .cardstore import pil_to_pixmap

DEGREES_PER_PIXEL = 0.5
IDENTITY = (0.0, 0.0, 0.0, 1.0)


def yaw_pitch_quaternion(yaw_deg: float, pitch_deg: float) -> tuple:
    """Quaternion (x, y, z, w): yaw about Y applied after pitch about X; identity when both are zero."""
    hy, hp = math.radians(yaw_deg) / 2, math.radians(pitch_deg) / 2
    qy = (0.0, math.sin(hy), 0.0, math.cos(hy))
    qx = (math.sin(hp), 0.0, 0.0, math.cos(hp))
    ax, ay, az, aw = qy
    bx, by, bz, bw = qx
    q = (aw * bx + ax * bw + ay * bz - az * by,
         aw * by - ax * bz + ay * bw + az * bx,
         aw * bz + ax * by - ay * bx + az * bw,
         aw * bw - ax * bx - ay * by - az * bz)
    norm = math.sqrt(sum(v * v for v in q)) or 1.0
    return tuple(v / norm for v in q)


class _RenderSignals(QObject):
    done = Signal(int, object, dict, str)     # generation, PIL image or None, metadata, error


class _RenderTask(QRunnable):
    def __init__(self, session: ClientSession, signals: _RenderSignals, generation: int, request: PreviewRequest,
                 icon_only: bool):
        super().__init__()
        self.session, self.signals, self.generation = session, signals, generation
        self.request, self.icon_only = request, icon_only

    def run(self) -> None:
        try:
            r = self.request
            if self.icon_only:
                image, meta = render_icon(self.session, r.item_id, ExportOptions(width=r.width, height=r.height))
            else:
                result = self.session.render_preview(r)
                image, meta = Image.fromarray(result.rgba), result.metadata
            self.signals.done.emit(self.generation, image, meta, "")
        except Exception as error:
            self.signals.done.emit(self.generation, None, {}, f"{type(error).__name__}: {error}")


class PreviewLabel(QWidget):
    """Shows the rendered pixmap on a flat background and reports mouse drags."""

    dragged = Signal(float, float)    # dx, dy in pixels since the last event
    released = Signal()

    def __init__(self, parent=None):
        super().__init__(parent)
        self.pixmap: QPixmap | None = None
        self.message = ""
        self._last = None
        self.setCursor(Qt.CursorShape.OpenHandCursor)

    def set_pixmap(self, pixmap: QPixmap | None, message: str = "") -> None:
        self.pixmap, self.message = pixmap, message
        self.update()

    def paintEvent(self, event) -> None:
        painter = QPainter(self)
        painter.fillRect(self.rect(), QColor("#3a4048"))
        if self.pixmap is not None:
            scaled = self.pixmap.scaled(self.size(), Qt.AspectRatioMode.KeepAspectRatio,
                                        Qt.TransformationMode.SmoothTransformation) \
                if (self.pixmap.width() > self.width() or self.pixmap.height() > self.height()) else self.pixmap
            painter.drawPixmap((self.width() - scaled.width()) // 2, (self.height() - scaled.height()) // 2, scaled)
        if self.message:
            painter.setPen(QColor("#c8ccd2"))
            painter.drawText(self.rect(), Qt.AlignmentFlag.AlignCenter | Qt.TextFlag.TextWordWrap, self.message)

    def mousePressEvent(self, event) -> None:
        if event.button() == Qt.MouseButton.LeftButton:
            self._last = event.position()
            self.setCursor(Qt.CursorShape.ClosedHandCursor)

    def mouseMoveEvent(self, event) -> None:
        if self._last is not None:
            pos = event.position()
            self.dragged.emit(pos.x() - self._last.x(), pos.y() - self._last.y())
            self._last = pos

    def mouseReleaseEvent(self, event) -> None:
        if self._last is not None and event.button() == Qt.MouseButton.LeftButton:
            self._last = None
            self.setCursor(Qt.CursorShape.OpenHandCursor)
            self.released.emit()


class DetailPanel(QWidget):
    wearer_or_spray_changed = Signal()

    def __init__(self, parent=None):
        super().__init__(parent)
        self.session: ClientSession | None = None
        self.item: CatalogItem | None = None
        self.yaw = 0.0
        self.pitch = 0.0
        self.last_metadata: dict = {}
        self._generation = 0
        self._busy = False
        self._queued: tuple | None = None
        self._suspended = False
        self._signals = _RenderSignals(self)
        self._signals.done.connect(self._on_rendered)
        self._pool = QThreadPool(self)
        self._pool.setMaxThreadCount(1)
        self._base_size = (405, 354)

        self.preview = PreviewLabel()
        self.preview.dragged.connect(self._on_drag)
        self.preview.released.connect(self._on_release)
        self.preview.setSizePolicy(QSizePolicy.Policy.Expanding, QSizePolicy.Policy.Fixed)

        self.camera_label = QLabel("Camera: official")
        self.reset_button = QPushButton("Reset to official view")
        self.reset_button.clicked.connect(self.reset_view)
        cam_row = QHBoxLayout()
        cam_row.addWidget(self.camera_label, 1)
        cam_row.addWidget(self.reset_button)

        self.wearer_combo = QComboBox()
        self.spray_combo = QComboBox()
        self.wearer_combo.activated.connect(self._on_choice)
        self.spray_combo.activated.connect(self._on_choice)

        self.info = QFormLayout()
        self.info.setLabelAlignment(Qt.AlignmentFlag.AlignRight)
        self._info_labels: dict[str, QLabel] = {}
        for key in ("Id", "Name", "Category", "Kind", "Status", "Price", "Warnings", "Catalog problems"):
            label = QLabel("-")
            label.setWordWrap(True)
            label.setTextInteractionFlags(Qt.TextInteractionFlag.TextSelectableByMouse)
            self._info_labels[key] = label
            self.info.addRow(key + ":", label)
        self.info.addRow("Wearer:", self.wearer_combo)
        self.info.addRow("Spray / colour:", self.spray_combo)

        self.provenance = QPlainTextEdit()
        self.provenance.setReadOnly(True)
        self.provenance.setPlaceholderText("Render provenance (sources, archives, composition, camera, missing textures)")
        self.provenance.setMinimumHeight(160)
        self.provenance.setLineWrapMode(QPlainTextEdit.LineWrapMode.NoWrap)

        inner = QWidget()
        layout = QVBoxLayout(inner)
        layout.addWidget(self.preview)
        layout.addLayout(cam_row)
        layout.addLayout(self.info)
        layout.addWidget(QLabel("Diagnostics / provenance of the last render:"))
        layout.addWidget(self.provenance, 1)
        scroll = QScrollArea()
        scroll.setWidgetResizable(True)
        scroll.setWidget(inner)
        outer = QVBoxLayout(self)
        outer.setContentsMargins(0, 0, 0, 0)
        outer.addWidget(scroll)
        self.setMinimumWidth(470)
        self._set_enabled(False)

    # -- session / item ------------------------------------------------------------------------
    def set_session(self, session: ClientSession | None) -> None:
        self.session = session
        self.item = None
        self._generation += 1
        self.preview.set_pixmap(None, "Click a card to see it here.")
        self._set_enabled(False)
        if session is not None:
            rect = load_layout(session.build).get("detail_preview", {}).get("rect", [0, 0, 405, 354])
            self._base_size = (rect[2], rect[3])
            self._palette = session.palette()
        self.preview.setFixedHeight(self._base_size[1])
        self.preview.setMinimumWidth(self._base_size[0])

    def suspend(self, flag: bool) -> None:
        """Stop starting renders (an export is running and may change shared render state)."""
        self._suspended = flag
        if flag:
            self._generation += 1
            self._pool.clear()
            self._pool.waitForDone()
            self._busy = False
            self._queued = None
        elif self.item is not None:
            self.rerender(3)

    def _set_enabled(self, on: bool) -> None:
        for w in (self.wearer_combo, self.spray_combo, self.reset_button):
            w.setEnabled(on)

    def show_item(self, item: CatalogItem | None) -> None:
        self.item = item
        self._generation += 1
        self.yaw = self.pitch = 0.0
        self.last_metadata = {}
        self.provenance.setPlainText("")
        if item is None or self.session is None:
            self.preview.set_pixmap(None, "Click a card to see it here.")
            self._set_enabled(False)
            return
        s = self.session
        self.wearer_combo.clear()
        self.wearer_combo.addItem("(default)", None)
        for name in s.wearers(item):
            self.wearer_combo.addItem(name, name)
        self.spray_combo.clear()
        self.spray_combo.addItem("Unsprayed", None)
        for index, (r, g, b) in sorted(self._palette.items()):
            pix = QPixmap(16, 16)
            pix.fill(QColor(round(r * 255), round(g * 255), round(b * 255)))
            self.spray_combo.addItem(QIcon(pix), f"Colour {index}", index)
        price = f"{item.price.amount:,} {item.price.money_type}" if item.price else "-"
        problems = [p for p in (s.catalog.problems if s.catalog else []) if str(item.item_id) in p]
        values = {
            "Id": str(item.item_id), "Name": item.name,
            "Category": f"{item.main_category or '-'} / {item.sub_category or '-'}",
            "Kind": item.kind.value, "Status": item.status.value, "Price": price,
            "Warnings": "\n".join(item.warnings) or "-", "Catalog problems": "\n".join(problems) or "-",
        }
        for key, text in values.items():
            self._info_labels[key].setText(text)
        renderable = item.is_3d or bool(item.icon)
        self._set_enabled(renderable)
        self.wearer_combo.setEnabled(item.is_3d and self.wearer_combo.count() > 1)
        self.spray_combo.setEnabled(item.is_3d)
        self.reset_button.setEnabled(item.is_3d)
        self._update_camera_label()
        if renderable:
            self.preview.set_pixmap(None, "Rendering...")
            self.rerender(3)
        else:
            self.preview.set_pixmap(None, f"No preview: {item.status.value}")

    def current_wearer(self) -> str | None:
        return self.wearer_combo.currentData() if self.wearer_combo.count() else None

    def current_spray(self) -> int | None:
        return self.spray_combo.currentData() if self.spray_combo.count() else None

    # -- camera --------------------------------------------------------------------------------
    def quaternion(self) -> tuple:
        if self.yaw == 0.0 and self.pitch == 0.0:
            return IDENTITY
        return yaw_pitch_quaternion(self.yaw, self.pitch)

    def _update_camera_label(self) -> None:
        official = self.quaternion() == IDENTITY
        self.camera_label.setText("Camera: official" if official else
                                  f"Camera: user-rotated (yaw {self.yaw:.1f}, pitch {self.pitch:.1f} deg)")

    def _on_drag(self, dx: float, dy: float) -> None:
        if self.item is None or not self.item.is_3d:
            return
        self.yaw += dx * DEGREES_PER_PIXEL
        self.pitch += dy * DEGREES_PER_PIXEL
        self._update_camera_label()
        self.rerender(1)

    def _on_release(self) -> None:
        if self.item is not None and self.item.is_3d:
            self.rerender(3)

    def reset_view(self) -> None:
        self.yaw = self.pitch = 0.0
        self._update_camera_label()
        if self.item is not None:
            self.rerender(3)

    def _on_choice(self, *_args) -> None:
        self.rerender(3)

    # -- rendering -----------------------------------------------------------------------------
    def _render_size(self) -> tuple[int, int]:
        w, h = self._base_size
        scale = 1.5 if self.preview.width() >= w * 1.5 else 1.0
        return round(w * scale), round(h * scale)

    def rerender(self, supersample: int) -> None:
        if self.item is None or self.session is None or self._suspended:
            return
        w, h = self._render_size()
        self.preview.setFixedHeight(h if w > self._base_size[0] else self._base_size[1])
        request = PreviewRequest(self.item.item_id, w, h, self.current_wearer(), self.current_spray(),
                                 self.quaternion(), supersample)
        self._queued = (request, not self.item.is_3d)
        self._pump()

    def _pump(self) -> None:
        if self._busy or self._queued is None:
            return
        request, icon_only = self._queued
        self._queued = None
        self._busy = True
        self._pool.start(_RenderTask(self.session, self._signals, self._generation, request, icon_only))

    def _on_rendered(self, generation: int, image, metadata: dict, error: str) -> None:
        self._busy = False
        if generation == self._generation and self.item is not None:
            if error:
                self.preview.set_pixmap(None, f"Render failed: {error}")
                self.provenance.setPlainText(error)
            else:
                self.preview.set_pixmap(pil_to_pixmap(image))
                self.last_metadata = metadata
                self.provenance.setPlainText(json.dumps(metadata, indent=2, ensure_ascii=False, default=str))
        self._pump()

    def wait_idle(self) -> None:
        self._pool.waitForDone()
