"""Scan a preview pack one atlas at a time and write private visual review sheets."""

from __future__ import annotations

import argparse
from collections import defaultdict
import json
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont


def measure(image: Image.Image) -> dict:
    """Flag geometry observations; flags require review and do not establish a rendering bug."""
    alpha = image.getchannel("A")
    bounds = alpha.getbbox()
    if bounds is None:
        return {"bounds": None, "flags": ["blank"]}
    width, height = image.size
    left, top, right, bottom = bounds
    flags = []
    occupancy = float(np.count_nonzero(np.asarray(alpha) > 16) / (width * height))
    centre = ((left + right) / (2 * width), (top + bottom) / (2 * height))
    if max(abs(value - .5) for value in centre) > .20:
        flags.append("off_centre")
    if occupancy < .015:
        flags.append("small_coverage")
    if left == 0 or top == 0 or right == width or bottom == height:
        flags.append("touches_edge")
    # Downsample before flood filling. Join antialias gaps so outlines are not separate components.
    mask = np.asarray(alpha.resize((82, 60)).filter(ImageFilter.MaxFilter(3))) > 32
    visited = np.zeros(mask.shape, dtype=bool)
    components = []
    for y, x in zip(*np.nonzero(mask)):
        if visited[y, x]:
            continue
        pending = [(int(y), int(x))]
        visited[y, x] = True
        points = []
        while pending:
            py, px = pending.pop()
            points.append((py, px))
            for ny, nx in ((py-1, px), (py+1, px), (py, px-1), (py, px+1)):
                if 0 <= ny < 60 and 0 <= nx < 82 and mask[ny, nx] and not visited[ny, nx]:
                    visited[ny, nx] = True
                    pending.append((ny, nx))
        if len(points) >= 8:
            coordinates = np.asarray(points)
            components.append({"pixels": len(points), "centre": coordinates.mean(0).tolist()})
    significant = [part for part in components if part["pixels"] >= max(8, sum(p["pixels"] for p in components)*.08)]
    if any(np.linalg.norm((np.asarray(a["centre"])-b["centre"])/[60, 82]) > .45
           for i, a in enumerate(significant) for b in significant[i+1:]):
        flags.append("distant_components")
    return {"bounds": list(bounds), "centre": centre, "occupancy": occupancy,
            "components": components, "flags": flags}


def scan(pack_root: Path, builds: list[str], output: Path, font_path: str | None = None) -> dict:
    """Audit requested builds with at most one decoded atlas and one contact sheet in memory."""
    output.mkdir(parents=True, exist_ok=True)
    manifest = json.loads((pack_root / "pack.json").read_text(encoding="utf-8"))
    width, height = manifest["cell"]
    references = defaultdict(list)
    report = {build: {"items": 0, "flagged": [], "counts": defaultdict(int)} for build in builds}
    for build in builds:
        index = json.loads((pack_root / manifest["builds"][build]).read_text(encoding="utf-8"))
        for item_id, entry in index["items"].items():
            if entry.get("atlas") is not None:
                report[build]["items"] += 1
                references[entry["atlas"]].append((build, item_id, entry))
    font = ImageFont.truetype(font_path, 12) if font_path else ImageFont.load_default()
    # Small sheets are flushed as they fill; no decoded cell cache is retained.
    sheets, occupied, pages = {}, defaultdict(int), defaultdict(int)

    def flush(build):
        pages[build] += 1
        sheets.pop(build).save(output / f'{build}-flagged-{pages[build]:02d}.png')
        occupied[build] = 0

    for atlas_id, uses in sorted(references.items()):
        atlas = manifest["atlases"][atlas_id]
        with Image.open(pack_root / atlas["file"]) as source:
            source.load()
            for build, item_id, entry in uses:
                cell = entry["cell"]
                x, y = cell % atlas["columns"] * width, cell // atlas["columns"] * height
                image = source.crop((x,y,x+width,y+height)).convert("RGBA")
                result = measure(image)
                if not result["flags"]:
                    continue
                report[build]["flagged"].append({"id": int(item_id), "name": entry["name"],
                                                 "main": entry["main"], "sub": entry["sub"],
                                                 "atlas": atlas_id, "cell": cell, **result})
                for flag in result["flags"]:
                    report[build]["counts"][flag] += 1
                if build not in sheets:
                    sheets[build] = Image.new("RGB", (width*4,(height+58)*4), "#637279")
                index = occupied[build]
                sx, sy = index % 4 * width, index // 4 * (height+58)
                sheets[build].paste(image,(sx,sy+58),image)
                draw = ImageDraw.Draw(sheets[build])
                labels = [f'{item_id} {entry["name"]}', f'{entry["main"]}/{entry["sub"]}', ', '.join(result["flags"])]
                for row, label in enumerate(labels):
                    draw.text((sx+4,sy+row*18+2),label[:45],font=font,fill="white")
                occupied[build] += 1
                if occupied[build] == 16:
                    flush(build)
        print(f'atlas {atlas_id}: {len(uses)} references scanned', flush=True)
    for build in list(sheets):
        flush(build)
    (output / "audit.json").write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding="utf-8")
    return {build: {"scanned": data["items"], "flagged": len(data["flagged"]),
                    "counts": dict(data["counts"])} for build, data in report.items()}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("pack", type=Path)
    parser.add_argument("--builds", required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--font")
    arguments = parser.parse_args()
    print(json.dumps(scan(arguments.pack,arguments.builds.split(","),arguments.output,arguments.font),indent=2))
