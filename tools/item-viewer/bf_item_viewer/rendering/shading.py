"""Toon ramp, specular highlight and outline parameters from the client's own files.

Grades and evidence for every rule are in the build profile's ``shading`` section.
"""

from __future__ import annotations

import io
import re
from dataclasses import dataclass

import numpy as np
from PIL import Image

_COLOR = re.compile(r"<Color\b([^>]*)/?>", re.I)
_ATTR = re.compile(r"(\w+)\s*=\s*'([^']*)'")


@dataclass
class Highlight:
    description: str
    rgb: tuple
    brightness: float
    power: float


@dataclass
class ShadingData:
    ramp: np.ndarray | None
    highlights: list
    light_world: np.ndarray
    outline_thickness: float
    outline_colour: tuple | None
    toon: bool
    specular: bool

    def highlight_for(self, tint: tuple) -> Highlight | None:
        """The highlight entry whose colour equals the tint (kr-1256 lists one per spray colour), else
        the last entry, which every part starts with (kr-1256 sub_107FCD0: count - 1)."""
        if not self.highlights:
            return None
        target = tuple(round(c * 255) for c in tint)
        for h in self.highlights:
            if h.rgb == target:
                return h
        return self.highlights[-1]


def parse_highlights(data: bytes) -> list[Highlight]:
    text = None
    for enc in ("utf-16", "utf-8-sig", "cp949"):
        try:
            text = data.decode(enc)
            break
        except UnicodeDecodeError:
            continue
    if text is None:
        return []
    out = []
    for m in _COLOR.finditer(text):
        a = dict(_ATTR.findall(m.group(1)))
        try:
            out.append(Highlight(a.get("description", ""), (int(a["r"]), int(a["g"]), int(a["b"])),
                                 float(a.get("brightness", "0")), float(a.get("power", "1"))))
        except (KeyError, ValueError):
            continue
    return out


def load_shading(index, profile) -> ShadingData:
    cfg = profile.raw.get("shading", {})
    toon = cfg.get("toon", {})
    ramp = None
    if toon.get("enabled") and index.exists(toon.get("ramp", "")):
        image = Image.open(io.BytesIO(index.read(toon["ramp"]))).convert("RGB")
        row = min(int(toon.get("ramp_row", 0)), image.height - 1)
        ramp = np.asarray(image, dtype=np.float32)[row] / 255.0
    spec = cfg.get("specular", {})
    highlights = parse_highlights(index.read(spec["table"])) if spec.get("enabled") and index.exists(spec.get("table", "")) else []
    outline = cfg.get("outline", {})
    colour = None
    if outline.get("enabled"):
        v = int(outline.get("colour", "0xFF000000"), 16)
        colour = (((v >> 16) & 255) / 255, ((v >> 8) & 255) / 255, (v & 255) / 255, 1.0)
    return ShadingData(ramp, highlights, np.asarray(toon.get("light_world", [0, -1, 0]), dtype=np.float64),
                       float(outline.get("thickness", 0.1)), colour, ramp is not None, bool(highlights))
