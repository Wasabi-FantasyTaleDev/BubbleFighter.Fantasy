"""Batch export: transparent previews and native-style cards, with a manifest for every file.

Aspect policy. The preview camera is orthographic with ``canvas_width`` units across the output
width (client rule). Two documented policies keep the official composition when the requested
output is not the card's preview aspect:

* ``expand`` (default): keep the full official framing of the reference preview rectangle and
  add transparent space on the longer side. The model's pixels-per-unit scale is uniform and the
  official composition is a centred sub-rectangle of the output.
* ``client``: hand the requested rectangle straight to the client's projection, exactly as the
  client would draw a preview control of that shape (width-anchored; height reveals more or less).

Changing only the resolution (same aspect) never changes the composition in either policy.
"""

from __future__ import annotations

import json
import re
import threading
import time
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Callable

from PIL import Image

from .. import __version__
from ..cards.card import CardArt, draw_card, preview_size
from ..service import ClientSession, PreviewRequest

MAX_PIXELS = 4096 * 4096
SAFE = re.compile(r"[^\w.-]+")

ProgressFn = Callable[[int, int, str], None]


@dataclass
class ExportOptions:
    width: int = 512
    height: int = 512
    mode: str = "transparent"           # transparent | card | both
    aspect_policy: str = "expand"       # expand | client
    supersample: int = 3
    padding: int = 0                    # extra transparent pixels on every side
    sprays: list = field(default_factory=lambda: [None])   # palette indices; None = unsprayed
    wearers: list = field(default_factory=lambda: [None])  # None = default wearer
    overwrite: str = "skip"             # skip | overwrite | rename
    card_scale: float = 1.0
    background: tuple | None = None     # opaque RGBA background for "transparent" mode when set


@dataclass
class ManifestEntry:
    item_id: int
    build: str
    status: str
    output: str | None
    render_mode: str
    spray_index: int | None
    wearer: str | None
    width: int
    height: int
    camera: dict | None = None
    composition: dict | None = None
    sources: list | None = None
    warnings: list = field(default_factory=list)
    error: str | None = None


def safe_name(text: str) -> str:
    text = SAFE.sub("_", text).strip("._")
    return text[:80] or "item"


def _reference_aspect(build: str) -> float:
    w, h = preview_size(build)
    return h / w


def render_transparent(session: ClientSession, item_id: int, options: ExportOptions, spray, wearer) -> tuple[Image.Image, dict]:
    w, h = options.width, options.height
    if w <= 0 or h <= 0 or w * h > MAX_PIXELS:
        raise ValueError(f"output size {w}x{h} is outside 1..{MAX_PIXELS} pixels")
    inner_w, inner_h = w - 2 * options.padding, h - 2 * options.padding
    if inner_w <= 0 or inner_h <= 0:
        raise ValueError("padding leaves no room for the preview")
    if options.aspect_policy == "expand":
        ref = _reference_aspect(session.build)
        # The official framing is the card preview rectangle, fitted inside the output; the rest of
        # the output is the same canvas extended around it (same pixels per model unit).
        fit_w = min(inner_w, round(inner_h / ref))
        result = session.render_preview(PreviewRequest(item_id, inner_w, inner_h, wearer, spray,
                                                       supersample=options.supersample,
                                                       canvas_scale=inner_w / fit_w))
        image = Image.fromarray(result.rgba)
        result.metadata["camera"]["official_frame"] = [fit_w, round(fit_w * ref)]
    elif options.aspect_policy == "client":
        result = session.render_preview(PreviewRequest(item_id, inner_w, inner_h, wearer, spray, supersample=options.supersample))
        image = Image.fromarray(result.rgba)
    else:
        raise ValueError(f"unknown aspect policy {options.aspect_policy!r}")
    if options.padding:
        padded = Image.new("RGBA", (w, h), (0, 0, 0, 0))
        padded.alpha_composite(image, (options.padding, options.padding))
        image = padded
    if options.background is not None:
        bg = Image.new("RGBA", image.size, tuple(options.background))
        bg.alpha_composite(image)
        image = bg
    meta = dict(result.metadata)
    meta["aspect_policy"] = options.aspect_policy
    meta["output_size"] = [w, h]
    return image, meta


def render_icon(session: ClientSession, item_id: int, options: ExportOptions) -> tuple[Image.Image, dict]:
    """A 2D item: its icon at native resolution, centred, scaled down only when it does not fit
    (never stretched, never upscaled past an integer factor), alpha preserved."""
    from ..resources.textures import decode_image
    import numpy as np
    item = session.item(item_id)
    tex = decode_image(session.index.read(item.icon), item.icon)
    icon = Image.fromarray((np.clip(tex.rgba, 0, 1) * 255).round().astype("uint8"))
    w, h = options.width - 2 * options.padding, options.height - 2 * options.padding
    factor = min(w / icon.width, h / icon.height)
    if factor < 1:
        icon = icon.resize((max(1, round(icon.width * factor)), max(1, round(icon.height * factor))), Image.LANCZOS)
    elif factor >= 2:
        k = int(factor)
        icon = icon.resize((icon.width * k, icon.height * k), Image.NEAREST)
    out = Image.new("RGBA", (options.width, options.height), (0, 0, 0, 0))
    out.alpha_composite(icon, ((options.width - icon.width) // 2, (options.height - icon.height) // 2))
    if options.background is not None:
        bg = Image.new("RGBA", out.size, tuple(options.background))
        bg.alpha_composite(out)
        out = bg
    meta = {"build": session.build, "item_id": item_id, "icon": item.icon, "render": "2d icon",
            "icon_native_size": list(tex.size), "sources": [session._prov(item.icon)], "warnings": list(item.warnings)}
    return out, meta


def render_card(session: ClientSession, art: CardArt, item_id: int, options: ExportOptions, spray, wearer) -> tuple[Image.Image, dict]:
    item = session.item(item_id)
    pw, ph = preview_size(session.build)
    scale = options.card_scale
    preview = None
    meta: dict = {}
    if item.is_3d:
        # Render at the scaled preview size so a large card keeps full preview detail.
        result = session.render_preview(PreviewRequest(item_id, round(pw * scale), round(ph * scale), wearer, spray,
                                                       supersample=options.supersample))
        preview = Image.fromarray(result.rgba)
        meta = result.metadata
    elif item.icon:
        from ..resources.textures import decode_image
        import numpy as np
        tex = decode_image(session.index.read(item.icon), item.icon)
        preview = Image.fromarray((np.clip(tex.rgba, 0, 1) * 255).astype("uint8"))
        meta = {"build": session.build, "item_id": item_id, "icon": item.icon,
                "sources": [session._prov(item.icon)], "warnings": list(item.warnings)}
    card = draw_card(art, name=item.name, preview=preview,
                     money_type=item.price.money_type if item.price else "",
                     amount=item.price.amount if item.price else None,
                     badge=item.display_tag, status_text="" if item.status.value == "Renderable" else item.status.value,
                     scale=options.card_scale)
    meta["card_layout_status"] = art.layout.get("status")
    return card, meta


def _target(out_dir: Path, name: str, policy: str) -> Path | None:
    path = out_dir / name
    if not path.exists() or policy == "overwrite":
        return path
    if policy == "skip":
        return None
    stem, suffix = path.stem, path.suffix
    for n in range(2, 10000):
        candidate = out_dir / f"{stem}~{n}{suffix}"
        if not candidate.exists():
            return candidate
    raise FileExistsError(f"no free name for {name}")


def export_items(session: ClientSession, item_ids: list[int], out_dir: Path | str, options: ExportOptions,
                 progress: ProgressFn | None = None, cancel: threading.Event | None = None) -> Path:
    """Export items one at a time (bounded memory) and write ``manifest.json``. Returns its path."""
    out_dir = Path(out_dir).resolve()
    try:
        out_dir.relative_to(session.client_root)
        raise ValueError("refusing to write exports into the client folder")
    except ValueError as error:
        if "refusing" in str(error):
            raise
    out_dir.mkdir(parents=True, exist_ok=True)
    art = CardArt(session.index, session.build, session.client_root) if options.mode in ("card", "both") else None
    modes = ["transparent", "card"] if options.mode == "both" else [options.mode]
    jobs = [(i, s, w, m) for i in item_ids for s in options.sprays for w in options.wearers for m in modes]
    entries: list[ManifestEntry] = []
    started = time.time()
    cancelled = False
    for n, (item_id, spray, wearer, mode) in enumerate(jobs):
        if progress:
            progress(n, len(jobs), f"{item_id} {mode}")
        if cancel is not None and cancel.is_set():
            cancelled = True
            break
        entry = ManifestEntry(item_id, session.build, "failed", None, mode, spray, wearer, options.width, options.height)
        try:
            item = session.item(item_id)
            if mode == "transparent" and not item.is_3d and not item.icon:
                entry.status = "skipped"
                entry.error = f"nothing to draw ({item.status.value})"
                entries.append(entry)
                continue
            if mode == "transparent" and not item.is_3d:
                image, meta = render_icon(session, item_id, options)
            elif mode == "transparent":
                image, meta = render_transparent(session, item_id, options, spray, wearer)
            else:
                image, meta = render_card(session, art, item_id, options, spray, wearer)
                entry.width, entry.height = image.size
            parts = [session.build, str(item_id), safe_name(item.name), mode]
            if spray is not None:
                parts.append(f"spray{spray}")
            if wearer:
                parts.append(safe_name(wearer))
            if mode == "transparent":
                parts.append(f"{options.width}x{options.height}")
            target = _target(out_dir, "_".join(parts) + ".png", options.overwrite)
            if target is None:
                entry.status = "skipped"
                entry.error = "file exists (overwrite policy: skip)"
            else:
                tmp = target.with_suffix(".part")
                image.save(tmp, format="PNG")
                tmp.replace(target)
                entry.status = "written"
                entry.output = target.name
            entry.camera = meta.get("camera")
            entry.composition = meta.get("composition")
            entry.sources = meta.get("sources")
            entry.warnings = meta.get("warnings", [])
        except Exception as error:  # every failure is recorded, never dropped
            entry.error = f"{type(error).__name__}: {error}"
        entries.append(entry)
    if progress:
        progress(len(entries), len(jobs), "cancelled" if cancelled else "done")
    manifest = {
        "renderer_version": __version__,
        "build": session.build,
        "client_root": str(session.client_root),
        "options": asdict(options),
        "requested_jobs": len(jobs),
        "completed_jobs": len(entries),
        "cancelled": cancelled,
        "seconds": round(time.time() - started, 2),
        "entries": [asdict(e) for e in entries],
    }
    stamp = time.strftime("%Y%m%d-%H%M%S")
    path = _target(out_dir, f"manifest-{session.build}-{stamp}.json", "rename")
    path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
    return path
