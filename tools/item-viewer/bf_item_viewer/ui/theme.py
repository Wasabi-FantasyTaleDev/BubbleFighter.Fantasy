"""Dark application palette (Fusion style) so every widget stays readable next to the dark card grid."""

from __future__ import annotations

from PySide6.QtGui import QColor, QPalette
from PySide6.QtWidgets import QApplication


def apply_dark_theme(app: QApplication) -> None:
    app.setStyle("Fusion")
    palette = QPalette()
    roles = {
        QPalette.ColorRole.Window: "#2d2d2d", QPalette.ColorRole.WindowText: "#e0e0e0",
        QPalette.ColorRole.Base: "#1f1f1f", QPalette.ColorRole.AlternateBase: "#2a2a2a",
        QPalette.ColorRole.Text: "#e0e0e0", QPalette.ColorRole.Button: "#3a3a3a",
        QPalette.ColorRole.ButtonText: "#e0e0e0", QPalette.ColorRole.ToolTipBase: "#3a3a3a",
        QPalette.ColorRole.ToolTipText: "#e0e0e0", QPalette.ColorRole.Highlight: "#4a78b5",
        QPalette.ColorRole.HighlightedText: "#ffffff", QPalette.ColorRole.PlaceholderText: "#8a8a8a",
    }
    for role, colour in roles.items():
        palette.setColor(role, QColor(colour))
    for role in (QPalette.ColorRole.WindowText, QPalette.ColorRole.Text, QPalette.ColorRole.ButtonText):
        palette.setColor(QPalette.ColorGroup.Disabled, role, QColor("#777777"))
    app.setPalette(palette)
