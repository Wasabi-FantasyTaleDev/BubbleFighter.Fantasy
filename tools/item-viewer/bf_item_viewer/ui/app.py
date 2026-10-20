"""GUI entry: ``python -m bf_item_viewer gui [client_root] [--cache DIR]``."""

from __future__ import annotations

import argparse
import sys


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(prog="bf_item_viewer gui", description="Graphical item viewer")
    parser.add_argument("client_root", nargs="?", help="client folder to open at start")
    parser.add_argument("--cache", help="cache root directory (default: LOCALAPPDATA/BFItemViewer/cache)")
    args = parser.parse_args(argv)

    from PySide6.QtWidgets import QApplication
    from .main_window import MainWindow
    from .theme import apply_dark_theme

    app = QApplication.instance() or QApplication(sys.argv[:1])
    apply_dark_theme(app)
    window = MainWindow(cache_root=args.cache)
    window.show()
    if args.client_root:
        window.open_client(args.client_root)
    return app.exec()
