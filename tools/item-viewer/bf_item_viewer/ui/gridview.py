"""Virtualized card grid: model, delegate and view (QListView in icon mode, uniform cells)."""

from __future__ import annotations

from PySide6.QtCore import QAbstractListModel, QModelIndex, QPoint, QRect, QSize, Qt, Signal
from PySide6.QtGui import QColor, QPainter
from PySide6.QtWidgets import QAbstractItemView, QListView, QStyledItemDelegate

from ..catalog.model import CatalogItem
from .cardstore import CardStore

ITEM_ROLE = Qt.ItemDataRole.UserRole + 1
BACKGROUND = QColor("#252525")
MIN_COLUMNS = 3


class CardModel(QAbstractListModel):
    """The filtered, sorted item list plus the selection set."""

    selection_changed = Signal()

    def __init__(self, parent=None):
        super().__init__(parent)
        self.items: list[CatalogItem] = []
        self.rows: dict[int, int] = {}
        self.selected: set[int] = set()
        self.store: CardStore | None = None

    def set_store(self, store: CardStore | None) -> None:
        if self.store is not None:
            try:
                self.store.preview_ready.disconnect(self._on_preview)
            except (RuntimeError, TypeError):
                pass
        self.store = store
        if store is not None:
            store.preview_ready.connect(self._on_preview)

    def set_items(self, items: list[CatalogItem]) -> None:
        self.beginResetModel()
        self.items = items
        self.rows = {item.item_id: n for n, item in enumerate(items)}
        self.endResetModel()

    def rowCount(self, parent=QModelIndex()) -> int:
        return 0 if parent.isValid() else len(self.items)

    def data(self, index, role=Qt.ItemDataRole.DisplayRole):
        if not index.isValid() or index.row() >= len(self.items):
            return None
        item = self.items[index.row()]
        if role == ITEM_ROLE:
            return item
        if role == Qt.ItemDataRole.DisplayRole:
            return item.name
        if role == Qt.ItemDataRole.ToolTipRole:
            return f"{item.item_id}  {item.name}\n{item.main_category} / {item.sub_category}\n{item.status.value}"
        return None

    def _on_preview(self, item_id: int) -> None:
        row = self.rows.get(item_id)
        if row is not None:
            index = self.index(row)
            self.dataChanged.emit(index, index)

    # -- selection -------------------------------------------------------------------------
    def is_selected(self, item_id: int) -> bool:
        return item_id in self.selected

    def _touch(self, ids) -> None:
        for item_id in ids:
            row = self.rows.get(item_id)
            if row is not None:
                self.dataChanged.emit(self.index(row), self.index(row))
        self.selection_changed.emit()

    def toggle(self, row: int) -> None:
        item_id = self.items[row].item_id
        self.selected.symmetric_difference_update({item_id})
        self._touch([item_id])

    def select_range(self, first: int, last: int, additive: bool) -> None:
        lo, hi = sorted((first, last))
        ids = {self.items[r].item_id for r in range(lo, hi + 1)}
        changed = set(ids) if additive else (self.selected | ids)
        self.selected = (self.selected | ids) if additive else ids
        self._touch(changed)

    def select_all_visible(self) -> None:
        ids = {item.item_id for item in self.items}
        changed = ids - self.selected
        self.selected |= ids
        self._touch(changed)

    def clear_selection(self) -> None:
        changed = set(self.selected)
        self.selected.clear()
        self._touch(changed)

    def selected_ids(self) -> list[int]:
        """Selected ids in the order of the catalog (stable, independent of the current sort)."""
        return sorted(self.selected)


class CardDelegate(QStyledItemDelegate):
    def __init__(self, model: CardModel, parent=None):
        super().__init__(parent)
        self.card_model = model

    def sizeHint(self, option, index) -> QSize:
        store = self.card_model.store
        return QSize(*store.cell_size) if store else QSize(181, 182)

    def paint(self, painter: QPainter, option, index) -> None:
        store = self.card_model.store
        item = index.data(ITEM_ROLE)
        if store is None or item is None:
            return
        pixmap = store.card_pixmap(item, self.card_model.is_selected(item.item_id))
        x = option.rect.x() + (option.rect.width() - pixmap.width()) // 2
        y = option.rect.y() + (option.rect.height() - pixmap.height()) // 2
        painter.drawPixmap(x, y, pixmap)


class CardGrid(QListView):
    """Icon-mode list view; clicks toggle selection (ctrl adds, shift selects a range)."""

    current_item_changed = Signal(object)     # CatalogItem or None
    view_range_changed = Signal()

    def __init__(self, parent=None):
        super().__init__(parent)
        self.card_model = CardModel(self)
        self.setModel(self.card_model)
        self.setItemDelegate(CardDelegate(self.card_model, self))
        self.setViewMode(QListView.ViewMode.IconMode)
        self.setFlow(QListView.Flow.LeftToRight)
        self.setWrapping(True)
        self.setResizeMode(QListView.ResizeMode.Adjust)
        self.setMovement(QListView.Movement.Static)
        self.setUniformItemSizes(True)
        self.setSpacing(0)
        self.setSelectionMode(QAbstractItemView.SelectionMode.NoSelection)
        self.setVerticalScrollMode(QAbstractItemView.ScrollMode.ScrollPerPixel)
        self.setHorizontalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAlwaysOff)
        self.setEditTriggers(QAbstractItemView.EditTrigger.NoEditTriggers)
        self.setStyleSheet("QListView { background: #252525; border: 0; }")
        self.setMouseTracking(False)
        self._anchor = 0
        self._cell = QSize(181, 182)
        self.verticalScrollBar().valueChanged.connect(self._range_changed)
        self.verticalScrollBar().setSingleStep(60)

    def set_store(self, store: CardStore | None) -> None:
        self.card_model.set_store(store)
        if store is not None:
            self._cell = QSize(*store.cell_size)
            self.setGridSize(self._cell)
            self.setMinimumWidth(MIN_COLUMNS * self._cell.width() + 24)

    # -- geometry --------------------------------------------------------------------------
    def columns(self) -> int:
        return max(1, self.viewport().width() // max(1, self._cell.width()))

    def visible_rows(self) -> tuple[int, int]:
        """First and last item index that are (roughly) on screen."""
        count = len(self.card_model.items)
        if count == 0:
            return 0, -1
        cols = self.columns()
        top = self.verticalScrollBar().value() // max(1, self._cell.height())
        rows_on_screen = self.viewport().height() // max(1, self._cell.height()) + 1
        first = min(count - 1, top * cols)
        last = min(count - 1, (top + rows_on_screen) * cols + cols - 1)
        return first, last

    def update_wanted(self) -> None:
        store = self.card_model.store
        if store is None:
            return
        first, last = self.visible_rows()
        cols = self.columns()
        lo, hi = max(0, first - 2 * cols), min(len(self.card_model.items) - 1, last + 2 * cols)
        ids = [self.card_model.items[n].item_id for n in range(lo, hi + 1)] if hi >= lo else []
        store.set_wanted(ids, first, self.card_model.rows)

    def _range_changed(self, *_args) -> None:
        self.update_wanted()
        self.view_range_changed.emit()

    def paintEvent(self, event) -> None:
        self.update_wanted()
        super().paintEvent(event)

    def resizeEvent(self, event) -> None:
        super().resizeEvent(event)
        self._range_changed()

    def set_items(self, items: list[CatalogItem]) -> None:
        self.card_model.set_items(items)
        self.verticalScrollBar().setValue(0)
        self._anchor = 0
        self._range_changed()

    # -- selection input -------------------------------------------------------------------
    def mousePressEvent(self, event) -> None:
        if event.button() != Qt.MouseButton.LeftButton:
            return super().mousePressEvent(event)
        index = self.indexAt(event.position().toPoint())
        if not index.isValid():
            return
        row = index.row()
        mods = event.modifiers()
        model = self.card_model
        if mods & Qt.KeyboardModifier.ShiftModifier:
            model.select_range(self._anchor, row, additive=bool(mods & Qt.KeyboardModifier.ControlModifier))
        else:
            model.toggle(row)      # plain click toggles; ctrl-click adds, which is the same operation
            self._anchor = row
        self.current_item_changed.emit(model.items[row])
