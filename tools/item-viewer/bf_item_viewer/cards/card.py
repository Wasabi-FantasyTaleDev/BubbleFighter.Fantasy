"""Native-style shop/inventory cards drawn from the client's own card art.

Art: ``Images/Shop2/item_box/*.png`` with ``.smf`` sprite tables. SMF (inferred from the bytes
of every item_box table in kr-812 and kr-1256): ``u32 version (2), u32 frame_count``, then per
frame ``i32 left, top, right, bottom`` and four ``i32`` that are -1 in every shipped table.
Layout rectangles come from ``data/cards/<build>.json`` with their evidence grade.
The card's preview is the same render the detail view and exports use, at the card's preview
rectangle size.
"""

from __future__ import annotations

import io
import json
import struct
from dataclasses import dataclass
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

CARD_DIR = Path(__file__).resolve().parent.parent / "data" / "cards"


@dataclass(frozen=True)
class SpriteFrame:
    left: int
    top: int
    right: int
    bottom: int


def parse_smf(data: bytes) -> list[SpriteFrame]:
    if len(data) < 8:
        raise ValueError("SMF table too short")
    version, count = struct.unpack_from("<2I", data)
    if version != 2 or count > 4096 or len(data) < 8 + 32 * count:
        raise ValueError(f"unsupported SMF table (version {version}, {count} frames, {len(data)} bytes)")
    frames = []
    for i in range(count):
        left, top, right, bottom = struct.unpack_from("<4i", data, 8 + 32 * i)
        frames.append(SpriteFrame(left, top, right, bottom))
    return frames


def load_layout(build: str) -> dict:
    path = CARD_DIR / f"{build}.json"
    if not path.is_file():
        raise FileNotFoundError(f"no card layout for {build}")
    return json.loads(path.read_text(encoding="utf-8"))


def _argb(text: str) -> tuple:
    v = int(text, 16)
    return (v >> 16) & 255, (v >> 8) & 255, v & 255, (v >> 24) & 255


class CardArt:
    """Loads and caches sprites and fonts from one client."""

    def __init__(self, index, build: str, client_root: Path):
        self.index = index
        self.layout = load_layout(build)
        self.client_root = Path(client_root)
        self._sprites: dict[tuple, Image.Image] = {}
        self._fonts: dict[int, ImageFont.FreeTypeFont] = {}
        self.missing: list[str] = []

    def sprite(self, name: str, frame: int = 0) -> Image.Image | None:
        key = (name, frame)
        if key in self._sprites:
            return self._sprites[key]
        folder = self.layout["frames"]["atlas_folder"]
        png, smf = f"{folder}/{name}.png", f"{folder}/{name}.smf"
        if not self.index.exists(png):
            self.missing.append(png)
            self._sprites[key] = None
            return None
        atlas = Image.open(io.BytesIO(self.index.read(png))).convert("RGBA")
        image = atlas
        if self.index.exists(smf):
            frames = parse_smf(self.index.read(smf))
            f = frames[min(frame, len(frames) - 1)]
            image = atlas.crop((f.left, f.top, f.right, f.bottom))
        self._sprites[key] = image
        return image

    def font(self, size: int) -> ImageFont.ImageFont:
        if size in self._fonts:
            return self._fonts[size]
        font = None
        for rel in self.layout.get("fonts", []):
            path = self.client_root / rel
            if path.is_file():
                try:
                    font = ImageFont.truetype(str(path), size)
                    break
                except OSError:
                    continue
        if font is None:
            self.missing.append("client font")
            font = ImageFont.load_default()
        self._fonts[size] = font
        return font


def preview_size(build: str) -> tuple[int, int]:
    rect = load_layout(build)["preview"]["rect"]
    return rect[2], rect[3]


def format_price(amount: int, money_type: str, layout: dict) -> str:
    suffix = layout["price"]["suffix"].get(money_type.lower(), money_type)
    return f"{amount:,}{suffix}"


def _fit(draw: ImageDraw.ImageDraw, text: str, font, width: int) -> str:
    if draw.textlength(text, font=font) <= width:
        return text
    while text and draw.textlength(text + "…", font=font) > width:
        text = text[:-1]
    return text + "…"


def draw_card(art: CardArt, *, name: str, preview: Image.Image | None, money_type: str = "", amount: int | None = None,
              badge: str = "", selected: bool = False, status_text: str = "", scale: float = 1.0) -> Image.Image:
    """Compose one card at ``scale`` times the client's pixel size.

    Card art is resampled to the scale, text is drawn at the scaled font size and ``preview`` is used at
    full resolution: pass a preview rendered at ``preview rect * scale`` for a sharp large card. A preview
    of another size is centred and only ever scaled down, never stretched.
    """
    layout = art.layout
    k = float(scale)
    if k <= 0:
        raise ValueError("scale must be positive")

    def r(v):
        return round(v * k)

    w, h = layout["card_size"]
    W, H = r(w), r(h)
    frame_name = layout["frames"]["by_money"].get((money_type or "").lower(), layout["frames"]["by_money"]["default"])
    frame_index = layout["frames"]["frame_index"]["selected" if selected else "normal"]
    card = Image.new("RGBA", (W, H), (0, 0, 0, 0))

    def sprite(name_, frame_=0):
        im = art.sprite(name_, frame_)
        if im is None or k == 1.0:
            return im
        return im.resize((max(1, r(im.width)), max(1, r(im.height))), Image.LANCZOS)

    background = art.sprite(frame_name, frame_index)
    if background is not None:
        bg = background.crop((0, 0, min(w, background.width), min(h, background.height)))
        card.alpha_composite(bg if k == 1.0 else bg.resize((r(bg.width), r(bg.height)), Image.LANCZOS))
    else:
        ImageDraw.Draw(card).rounded_rectangle((0, 0, W - 1, H - 1), r(6), fill=(214, 233, 246, 255))
    px, py, pw, ph = (r(v) for v in layout["preview"]["rect"])
    if preview is not None:
        im = preview
        if im.width > pw or im.height > ph:
            im = im.copy()
            im.thumbnail((pw, ph), Image.LANCZOS)
        layer = Image.new("RGBA", (W, H), (0, 0, 0, 0))
        layer.alpha_composite(im, (px + (pw - im.width) // 2, py + (ph - im.height) // 2))
        # The preview control is clipped to the well (its rounded corners come from the card art).
        mask = Image.new("L", (W, H), 0)
        ImageDraw.Draw(mask).rounded_rectangle((px, py, px + pw - 1, py + ph - 1), r(layout["preview"].get("corner", 5)),
                                               fill=255)
        layer.putalpha(Image.composite(layer.getchannel("A"), Image.new("L", (W, H), 0), mask))
        card.alpha_composite(layer)
    draw = ImageDraw.Draw(card)
    tx, ty, tw, th = (r(v) for v in layout["title"]["rect"])
    font = art.font(r(layout["title"]["font_size"]))
    draw.text((tx, ty + th // 2), _fit(draw, name, font, tw), font=font, fill=_argb(layout["title"]["colour"]),
              anchor="lm")
    if badge and badge in layout["badge"]["atlas"]:
        im = sprite(layout["badge"]["atlas"][badge], 0)
        if im is not None:
            card.alpha_composite(im, tuple(r(v) for v in layout["badge"]["pos"]))
    rx, ry, rw, rh = (r(v) for v in layout["price"]["rect"])
    text = format_price(amount, money_type, layout) if amount is not None else ""
    if status_text:
        sfont = art.font(r(max(9, layout["title"]["font_size"] - 2)))
        draw.text((px + pw - r(4), py + r(3)), status_text, font=sfont, fill=(255, 210, 120, 255), anchor="ra",
                  stroke_width=max(1, r(1)), stroke_fill=(0, 0, 0, 200))
    if text:
        font = art.font(r(layout["price"]["font_size"]))
        draw.text((rx + rw // 2, ry + rh // 2), _fit(draw, text, font, rw - r(4)), font=font,
                  fill=_argb(layout["price"]["colour"]), anchor="mm")
    for key in ("buy_button", "zzim_button"):
        spec = layout.get(key)
        if spec:
            im = sprite(spec["atlas"], spec.get("frame", 0))
            if im is not None:
                card.alpha_composite(im, tuple(r(v) for v in spec["pos"]))
    return card
