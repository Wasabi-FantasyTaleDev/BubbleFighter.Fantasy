"""Export options dialog."""

from __future__ import annotations

from dataclasses import dataclass

from PySide6.QtWidgets import (QCheckBox, QComboBox, QDialog, QDialogButtonBox, QFileDialog, QFormLayout, QHBoxLayout,
                               QLabel, QLineEdit, QPushButton, QSpinBox, QVBoxLayout)

from ..export.batch import ExportOptions

PRESETS = [("256 x 256", (256, 256)), ("512 x 512", (512, 512)), ("1024 x 1024", (1024, 1024)),
           ("512 x 768", (512, 768)), ("768 x 512", (768, 512)), ("Custom", None)]


@dataclass
class ExportChoice:
    out_dir: str
    options: ExportOptions


class ExportDialog(QDialog):
    def __init__(self, parent, *, item_count: int, scope: str, palette_size: int, detail_wearer: str | None,
                 default_dir: str = ""):
        super().__init__(parent)
        self.setWindowTitle("Export items")
        self.item_count = item_count
        self.palette_size = palette_size
        self.detail_wearer = detail_wearer
        self._updating = False

        layout = QVBoxLayout(self)
        layout.addWidget(QLabel(f"{item_count} item(s): {scope}"))
        form = QFormLayout()
        layout.addLayout(form)

        self.folder = QLineEdit(default_dir)
        browse = QPushButton("Browse...")
        browse.clicked.connect(self._browse)
        row = QHBoxLayout()
        row.addWidget(self.folder, 1)
        row.addWidget(browse)
        form.addRow("Output folder:", row)

        self.preset = QComboBox()
        for label, _ in PRESETS:
            self.preset.addItem(label)
        self.preset.setCurrentIndex(1)
        self.width_spin = QSpinBox()
        self.height_spin = QSpinBox()
        for spin in (self.width_spin, self.height_spin):
            spin.setRange(16, 4096)
            spin.setValue(512)
        self.square = QCheckBox("Square (lock width = height)")
        self.square.setChecked(True)
        size_row = QHBoxLayout()
        size_row.addWidget(self.preset)
        size_row.addWidget(self.width_spin)
        size_row.addWidget(QLabel("x"))
        size_row.addWidget(self.height_spin)
        form.addRow("Size:", size_row)
        form.addRow("", self.square)

        self.mode = QComboBox()
        self.mode.addItem("Transparent preview", "transparent")
        self.mode.addItem("Complete card", "card")
        self.mode.addItem("Both", "both")
        form.addRow("Mode:", self.mode)

        self.aspect = QComboBox()
        self.aspect.addItem("Expand: keep the full official framing", "expand")
        self.aspect.addItem("Client: same as the game control", "client")
        form.addRow("Aspect policy:", self.aspect)

        self.padding = QSpinBox()
        self.padding.setRange(0, 512)
        form.addRow("Padding (px):", self.padding)

        self.card_scale = QSpinBox()
        self.card_scale.setRange(1, 4)
        form.addRow("Card scale:", self.card_scale)

        self.sprays = QCheckBox("Include all spray variants (unsprayed + every palette colour)")
        form.addRow("", self.sprays)

        self.wearer = QComboBox()
        self.wearer.addItem("Default wearer", None)
        if detail_wearer:
            self.wearer.addItem(f"Current detail wearer ({detail_wearer})", detail_wearer)
        form.addRow("Wearer:", self.wearer)

        self.overwrite = QComboBox()
        for label, value in (("Skip existing files", "skip"), ("Overwrite", "overwrite"), ("Rename (add ~N)", "rename")):
            self.overwrite.addItem(label, value)
        form.addRow("If a file exists:", self.overwrite)

        self.jobs_label = QLabel()
        layout.addWidget(self.jobs_label)
        buttons = QDialogButtonBox(QDialogButtonBox.StandardButton.Ok | QDialogButtonBox.StandardButton.Cancel)
        buttons.accepted.connect(self._accept)
        buttons.rejected.connect(self.reject)
        layout.addWidget(buttons)

        self.preset.currentIndexChanged.connect(self._preset_changed)
        self.width_spin.valueChanged.connect(self._width_changed)
        self.height_spin.valueChanged.connect(self._height_changed)
        self.square.toggled.connect(self._square_toggled)
        for widget in (self.mode, self.sprays):
            (widget.currentIndexChanged if isinstance(widget, QComboBox) else widget.toggled).connect(self._update_jobs)
        self._update_jobs()

    # -- behaviour -------------------------------------------------------------------------
    def _browse(self) -> None:
        folder = QFileDialog.getExistingDirectory(self, "Choose the output folder", self.folder.text())
        if folder:
            self.folder.setText(folder)

    def _preset_changed(self, index: int) -> None:
        size = PRESETS[index][1]
        if size is None:
            return
        self._updating = True
        self.width_spin.setValue(size[0])
        self.height_spin.setValue(size[1])
        self.square.setChecked(size[0] == size[1])
        self._updating = False

    def _mark_custom(self) -> None:
        if not self._updating:
            current = (self.width_spin.value(), self.height_spin.value())
            for n, (_, size) in enumerate(PRESETS):
                if size == current:
                    self.preset.blockSignals(True)
                    self.preset.setCurrentIndex(n)
                    self.preset.blockSignals(False)
                    return
            self.preset.blockSignals(True)
            self.preset.setCurrentIndex(len(PRESETS) - 1)
            self.preset.blockSignals(False)

    def _width_changed(self, value: int) -> None:
        if not self._updating and self.square.isChecked():
            self._updating = True
            self.height_spin.setValue(value)
            self._updating = False
        self._mark_custom()

    def _height_changed(self, value: int) -> None:
        if not self._updating and self.square.isChecked():
            self._updating = True
            self.width_spin.setValue(value)
            self._updating = False
        self._mark_custom()

    def _square_toggled(self, on: bool) -> None:
        if on:
            self.height_spin.setValue(self.width_spin.value())

    def variant_count(self) -> int:
        return (1 + self.palette_size) if self.sprays.isChecked() else 1

    def job_count(self) -> int:
        modes = 2 if self.mode.currentData() == "both" else 1
        return self.item_count * self.variant_count() * modes

    def _update_jobs(self, *_args) -> None:
        self.jobs_label.setText(f"{self.job_count()} render job(s) "
                                f"({self.item_count} items x {self.variant_count()} variant(s))")

    def _accept(self) -> None:
        if not self.folder.text().strip():
            self._browse()
            if not self.folder.text().strip():
                return
        self.accept()

    def choice(self, palette_indices: list[int]) -> ExportChoice:
        sprays: list = [None] + list(palette_indices) if self.sprays.isChecked() else [None]
        options = ExportOptions(
            width=self.width_spin.value(), height=self.height_spin.value(), mode=self.mode.currentData(),
            aspect_policy=self.aspect.currentData(), padding=self.padding.value(), sprays=sprays,
            wearers=[self.wearer.currentData()], overwrite=self.overwrite.currentData(),
            card_scale=float(self.card_scale.value()))
        return ExportChoice(self.folder.text().strip(), options)
