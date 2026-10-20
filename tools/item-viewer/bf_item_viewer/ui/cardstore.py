"""Lazy card rendering: preview images from a small thread pool, cards composed on the UI thread.

Workers render only the preview image (the expensive part) and hand it back through a signal;
card art, fonts and QPixmaps are touched only on the UI thread. Both the preview LRU and the
card LRU are bounded.
"""

from __future__ import annotations

from collections import OrderedDict

from PIL import Image
from PySide6.QtCore import QObject, QRunnable, QThreadPool, Signal
from PySide6.QtGui import QImage, QPixmap

from ..cards.card import CardArt, draw_card, load_layout, preview_size
from ..catalog.model import CatalogItem, Status
from ..service import ClientSession, PreviewRequest

NO_PREVIEW = object()      # the item has nothing to draw (metadata only) or rendering failed


def pil_to_pixmap(image: Image.Image) -> QPixmap:
    image = image.convert("RGBA")
    qimage = QImage(image.tobytes("raw", "RGBA"), image.width, image.height, 4 * image.width,
                    QImage.Format.Format_RGBA8888).copy()
    return QPixmap.fromImage(qimage)


def grid_metrics(build: str) -> tuple[tuple[int, int], tuple[int, int]]:
    """((card w, card h), (cell w, cell h)): the cell pitch comes from the build's own slot markers."""
    layout = load_layout(build)
    cw, ch = layout["card_size"]
    grid = layout["grid"]
    xs, ys = grid["origins_x"], grid["origins_y"]
    pitch_x = xs[1] - xs[0] if len(xs) > 1 else grid["slot_size"][0]
    pitch_y = ys[1] - ys[0] if len(ys) > 1 else grid["slot_size"][1]
    return (cw, ch), (max(pitch_x, cw), max(pitch_y, ch))


def icon_preview(session: ClientSession, item: CatalogItem) -> Image.Image:
    from ..resources.textures import decode_image
    import numpy as np
    tex = decode_image(session.index.read(item.icon), item.icon)
    return Image.fromarray((np.clip(tex.rgba, 0, 1) * 255).round().astype("uint8"))


class _Signals(QObject):
    done = Signal(int, object, str)      # item id, PIL image or None, error text ("" = ok, "skipped" = dropped)


class _PreviewTask(QRunnable):
    def __init__(self, store: "CardStore", item: CatalogItem, size: tuple[int, int]):
        super().__init__()
        self.store, self.item, self.size = store, item, size
        self.setAutoDelete(True)

    def run(self) -> None:
        signals, item = self.store.signals, self.item
        if item.item_id not in self.store.wanted:
            signals.done.emit(item.item_id, None, "skipped")
            return
        try:
            if item.is_3d:
                result = self.store.session.render_preview(
                    PreviewRequest(item.item_id, self.size[0], self.size[1], supersample=2))
                image = Image.fromarray(result.rgba)
            elif item.icon:
                image = icon_preview(self.store.session, item)
            else:
                image = None
            signals.done.emit(item.item_id, image, "")
        except Exception as error:
            signals.done.emit(item.item_id, None, f"{type(error).__name__}: {error}")


class CardStore(QObject):
    """Owns preview rendering, the LRU caches and the card art of one session."""

    preview_ready = Signal(int)

    def __init__(self, session: ClientSession, workers: int = 3, preview_capacity: int = 600,
                 card_capacity: int = 600, parent=None):
        super().__init__(parent)
        self.session = session
        self.art = CardArt(session.index, session.build, session.client_root)
        self.card_size, self.cell_size = grid_metrics(session.build)
        self.preview_size = preview_size(session.build)
        self.pool = QThreadPool(self)
        self.pool.setMaxThreadCount(max(1, min(4, workers)))
        self.signals = _Signals(self)
        self.signals.done.connect(self._on_done)
        self.preview_capacity = preview_capacity
        self.card_capacity = card_capacity
        self._previews: OrderedDict[int, object] = OrderedDict()
        self._cards: OrderedDict[tuple, QPixmap] = OrderedDict()
        self._placeholders: OrderedDict[tuple, QPixmap] = OrderedDict()
        self._pending: set[int] = set()
        self.errors: dict[int, str] = {}
        self.wanted: frozenset = frozenset()
        self.anchor_row = 0
        self.rows: dict[int, int] = {}
        self.paused = False
        self.render_count = 0

    # -- control ---------------------------------------------------------------------------
    def set_wanted(self, item_ids, anchor_row: int, rows: dict[int, int]) -> None:
        self.wanted = frozenset(item_ids)
        self.anchor_row = anchor_row
        self.rows = rows

    def pause(self) -> None:
        """Stop rendering (used while an export runs) and wait for in-flight tasks."""
        self.paused = True
        self.pool.clear()
        self.pool.waitForDone()
        self._pending.clear()

    def resume(self) -> None:
        self.paused = False

    def shutdown(self) -> None:
        self.wanted = frozenset()
        self.pool.clear()
        self.pool.waitForDone()

    # -- cards -----------------------------------------------------------------------------
    def card_pixmap(self, item: CatalogItem, selected: bool) -> QPixmap:
        key = (item.item_id, selected)
        card = self._cards.get(key)
        if card is not None:
            self._cards.move_to_end(key)
            return card
        needs_render = item.is_3d or bool(item.icon)
        preview = self._previews.get(item.item_id)
        if needs_render and preview is None:
            self._request(item)
            return self._placeholder(item, selected)
        card = self._compose(item, None if preview is NO_PREVIEW or preview is None else preview, selected)
        self._cards[key] = card
        while len(self._cards) > self.card_capacity:
            self._cards.popitem(last=False)
        if item.item_id in self._previews:
            self._previews.move_to_end(item.item_id)
        return card

    def _compose(self, item: CatalogItem, preview, selected: bool, status_text: str | None = None) -> QPixmap:
        if status_text is None:
            status_text = "" if item.status == Status.RENDERABLE else item.status.value
            if item.item_id in self.errors:
                status_text = "render failed"
        image = draw_card(self.art, name=item.name, preview=preview,
                          money_type=item.price.money_type if item.price else "",
                          amount=item.price.amount if item.price else None,
                          badge=item.display_tag, selected=selected, status_text=status_text)
        return pil_to_pixmap(image)

    def _placeholder(self, item: CatalogItem, selected: bool) -> QPixmap:
        key = (item.item_id, selected)
        pix = self._placeholders.get(key)
        if pix is None:
            pix = self._compose(item, None, selected, "loading")
            self._placeholders[key] = pix
            while len(self._placeholders) > 300:
                self._placeholders.popitem(last=False)
        return pix

    def _request(self, item: CatalogItem) -> None:
        if self.paused or item.item_id in self._pending:
            return
        self._pending.add(item.item_id)
        distance = abs(self.rows.get(item.item_id, 0) - self.anchor_row)
        self.pool.start(_PreviewTask(self, item, self.preview_size), max(0, 1_000_000 - distance))

    def _on_done(self, item_id: int, image, error: str) -> None:
        self._pending.discard(item_id)
        if error == "skipped":
            return       # scrolled away before it started; it will be requested again if shown
        self.render_count += 1
        if error:
            self.errors[item_id] = error
        self._previews[item_id] = image if image is not None else NO_PREVIEW
        while len(self._previews) > self.preview_capacity:
            self._previews.popitem(last=False)
        self._placeholders.pop((item_id, False), None)
        self._placeholders.pop((item_id, True), None)
        self._cards.pop((item_id, False), None)
        self._cards.pop((item_id, True), None)
        self.preview_ready.emit(item_id)

    def pending_count(self) -> int:
        return len(self._pending)
