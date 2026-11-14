"""Image encoding and atlas packing for preview cells.

A cell is one item preview (RGBA). Cells are compared by their pixels, never by name, so a cell
that is identical in several places is stored once. An atlas places up to ``columns * rows`` cells
in reading order: cell ``k`` sits at column ``k % columns``, row ``k // columns``.
"""

from __future__ import annotations

import hashlib
import io
from dataclasses import dataclass

from PIL import Image

CELL = (328, 240)               # twice the 164x120 model viewport of the launcher UI
COLUMNS, ROWS = 8, 8            # 64 cells per atlas
DEFAULT_QUALITY = 85
FORMATS = ("png", "webp")


def cell_hash(image: Image.Image) -> str:
    """SHA-256 of the size and every RGBA byte."""
    image = image.convert("RGBA")
    digest = hashlib.sha256(f"{image.width}x{image.height}:".encode())
    digest.update(image.tobytes())
    return digest.hexdigest()


def encode(image: Image.Image, fmt: str = "png", quality: int = DEFAULT_QUALITY, lossless: bool = False) -> bytes:
    """PNG, or WebP with alpha: lossy keeps the alpha channel at full quality, lossless is exact."""
    if fmt not in FORMATS:
        raise ValueError(f"unknown image format {fmt!r}; use one of {FORMATS}")
    if not 0 <= quality <= 100:
        raise ValueError("quality must be 0..100")
    out = io.BytesIO()
    if fmt == "png":
        image.save(out, format="PNG")
    elif lossless:
        image.save(out, format="WEBP", lossless=True, exact=True, quality=75, method=4)
    else:
        image.save(out, format="WEBP", quality=quality, alpha_quality=100, method=4)
    return out.getvalue()


@dataclass
class Atlas:
    image: Image.Image
    cells: int
    columns: int

    @property
    def width(self) -> int:
        return self.image.width

    @property
    def height(self) -> int:
        return self.image.height


def build_atlas(cells: list[Image.Image], columns: int = COLUMNS, cell: tuple[int, int] = CELL) -> Atlas:
    """Place ``cells`` (each ``cell`` sized) in reading order; the image is as tall as its rows."""
    if not cells:
        raise ValueError("an atlas needs at least one cell")
    rows = -(-len(cells) // columns)
    sheet = Image.new("RGBA", (columns * cell[0], rows * cell[1]), (0, 0, 0, 0))
    for k, image in enumerate(cells):
        if image.size != tuple(cell):
            raise ValueError(f"cell {k} is {image.size}, expected {tuple(cell)}")
        sheet.paste(image.convert("RGBA"), ((k % columns) * cell[0], (k // columns) * cell[1]))
    return Atlas(sheet, len(cells), columns)


def split_pages(cells: list, per_page: int = COLUMNS * ROWS) -> list[list]:
    """Consecutive pages of at most ``per_page`` cells."""
    return [cells[i:i + per_page] for i in range(0, len(cells), per_page)]


def cell_box(index: int, columns: int = COLUMNS, cell: tuple[int, int] = CELL) -> tuple[int, int, int, int]:
    """Pixel box (left, top, right, bottom) of cell ``index`` in an atlas."""
    x, y = (index % columns) * cell[0], (index // columns) * cell[1]
    return x, y, x + cell[0], y + cell[1]
