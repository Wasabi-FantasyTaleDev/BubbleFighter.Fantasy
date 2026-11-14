"""Atlas export of one client: many item previews combined into a few images plus a JSON index."""

from __future__ import annotations

import json
from pathlib import Path

from PIL import Image

from ..service import ClientSession
from .atlas import CELL, COLUMNS, ROWS, build_atlas, cell_box, cell_hash, encode
from .batch import MAX_PIXELS, ExportOptions, render_transparent


def export_atlas(session: ClientSession, item_ids: list[int], out_dir: Path | str, fmt: str = "webp",
                 quality: int = 85, lossless: bool = False, columns: int = COLUMNS, rows: int = ROWS,
                 cell: tuple[int, int] = CELL, supersample: int = 3, progress=None) -> Path:
    """Render ``item_ids``, store identical previews once, write ``atlas-NN.<fmt>`` pages and
    ``atlas-index.json`` (per item: page, cell, column, row, pixel box). Returns the index path."""
    out_dir = Path(out_dir).resolve()
    try:
        out_dir.relative_to(session.client_root)
        raise ValueError("refusing to write exports into the client folder")
    except ValueError as error:
        if "refusing" in str(error):
            raise
    per_page = columns * rows
    if min(columns, rows, *cell) < 1 or per_page * cell[0] * cell[1] > MAX_PIXELS:
        raise ValueError(f"atlas page must have positive dimensions and at most {MAX_PIXELS} pixels")
    if fmt == "webp" and max(columns * cell[0], rows * cell[1]) > 16383:
        raise ValueError("WebP atlas dimensions cannot exceed 16383 pixels")
    out_dir.mkdir(parents=True, exist_ok=True)
    options = ExportOptions(width=cell[0], height=cell[1], supersample=supersample)
    slot_of: dict[str, int] = {}
    entries = []
    cells: list[Image.Image] = []
    pages = []

    def flush_page() -> None:
        atlas = build_atlas(cells, columns, cell)
        name = f"atlas-{len(pages):02d}.{fmt}"
        (out_dir / name).write_bytes(encode(atlas.image, fmt, quality, lossless))
        pages.append({"file": name, "cells": atlas.cells, "columns": columns, "width": atlas.width,
                      "height": atlas.height})
        cells.clear()

    for n, item_id in enumerate(item_ids):
        if progress:
            progress(n, len(item_ids), str(item_id))
        item = session.item(item_id)
        entry = {"item_id": item_id, "name": item.name, "cell": None, "error": None}
        if item.is_3d:
            try:
                image, _ = render_transparent(session, item_id, options, None, None)
                digest = cell_hash(image)
            except Exception as error:  # recorded, never dropped
                entry["error"] = f"{type(error).__name__}: {error}"
            else:
                if digest not in slot_of:
                    slot_of[digest] = len(slot_of)
                    cells.append(image)
                    if len(cells) == per_page:
                        flush_page()
                page, index = divmod(slot_of[digest], per_page)
                entry.update({"atlas": page, "cell": index, "column": index % columns, "row": index // columns,
                              "box": list(cell_box(index, columns, cell))})
        entries.append(entry)
    if cells:
        flush_page()
    index = {"format": 1, "build": session.build, "cell": list(cell), "atlases": pages, "items": entries}
    path = out_dir / "atlas-index.json"
    path.write_text(json.dumps(index, ensure_ascii=False, indent=1), encoding="utf-8")
    return path
