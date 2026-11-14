"""Apply explicitly reviewed replacement pictures while retaining the baseline pack's files."""

from __future__ import annotations

import argparse
from collections import Counter
import copy
import hashlib
import json
from pathlib import Path
import shutil
import sys

from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from bf_item_viewer.export.atlas import build_atlas, cell_box, cell_hash, encode
from bf_item_viewer.export.pack import renderer_source_hash


def file_hash(path: Path) -> str:
    """Hash a file without holding its encoded contents in memory."""
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def child(root: Path, name: str) -> Path:
    """Resolve a relative artifact path and reject traversal outside its owner directory."""
    path = (root / name).resolve()
    if Path(name).is_absolute() or not path.is_relative_to(root.resolve()):
        raise ValueError(f"artifact path must stay inside its directory: {name}")
    return path


def read_json(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def write_json(path: Path, value: dict) -> None:
    path.write_text(json.dumps(value, ensure_ascii=False, indent=1), encoding="utf-8")


def repair(baseline: Path, plan_path: Path, output: Path) -> dict:
    """Append bounded atlases or native icons for an explicit plan and verify all retained data.

    Plan fields are baseline_pack_sha256, renderer_sha256 and replacements. Each replacement
    has build, id, kind (cell or icon), image (relative to the plan), and a nonempty reason.
    The output keeps the original generator and records separate replacement provenance.
    """
    baseline, plan_path, output = baseline.resolve(), plan_path.resolve(), output.resolve()
    if output.exists():
        raise ValueError("repair output must be a fresh directory")
    if output.is_relative_to(baseline) or baseline.is_relative_to(output):
        raise ValueError("baseline and repair output must be separate directories")
    if plan_path.is_relative_to(output):
        raise ValueError("the input plan must be outside the output directory")
    manifest = read_json(baseline / "pack.json")
    plan = read_json(plan_path)
    baseline_hash = file_hash(baseline / "pack.json")
    source_hash = renderer_source_hash()
    if plan["baseline_pack_sha256"] != baseline_hash:
        raise ValueError("baseline pack does not match the reviewed plan")
    if plan["renderer_sha256"] != source_hash:
        raise ValueError("replacement renderer does not match the reviewed plan")
    if manifest["format"] != 1:
        raise ValueError("only preview pack format 1 is supported")
    size = tuple(manifest["cell"])
    replacements = plan["replacements"]
    if not replacements:
        raise ValueError("repair plan is empty")
    indexes, selected, prepared = {}, set(), []
    for row in replacements:
        build, key = row["build"], str(row["id"])
        identity = (build, key)
        if identity in selected:
            raise ValueError(f"duplicate replacement: {identity}")
        selected.add(identity)
        if build not in manifest["builds"]:
            raise ValueError(f"unknown build: {build}")
        if build not in indexes:
            indexes[build] = read_json(child(baseline, manifest["builds"][build]))
        if key not in indexes[build]["items"]:
            raise ValueError(f"unknown item: {identity}")
        if row["kind"] not in ("cell", "icon") or not row["reason"]:
            raise ValueError(f"replacement needs a kind and review reason: {identity}")
        picture = child(plan_path.parent, row["image"])
        with Image.open(picture) as source:
            rgba = source.convert("RGBA")
            if row["kind"] == "cell" and rgba.size != size:
                raise ValueError(f"replacement cell has the wrong size: {identity}")
            if rgba.getchannel("A").getbbox() is None:
                raise ValueError(f"replacement picture is blank: {identity}")
            digest = cell_hash(rgba)
        prepared.append((row, picture, digest, file_hash(picture)))
    originals = {path.relative_to(baseline).as_posix(): file_hash(path)
                 for path in baseline.rglob("*") if path.is_file()}
    shutil.copytree(baseline, output)
    updated = copy.deepcopy(manifest)
    placements, page = {}, []
    columns, capacity = 8, 64

    def flush() -> None:
        if not page:
            return
        cells = []
        for _, path, _, _ in page:
            with Image.open(path) as source:
                cells.append(source.convert("RGBA"))
        atlas = build_atlas(cells, columns, size)
        data = encode(atlas.image, "webp", lossless=True)
        name = f"atlas/{hashlib.sha256(data).hexdigest()[:16]}.webp"
        target = child(output, name)
        target.parent.mkdir(parents=True, exist_ok=True)
        if target.exists() and file_hash(target) != hashlib.sha256(data).hexdigest():
            raise ValueError("content-addressed atlas name collision")
        target.write_bytes(data)
        atlas_id = len(updated["atlases"])
        updated["atlases"].append({"file": name, "cells": atlas.cells, "columns": columns,
                                   "width": atlas.width, "height": atlas.height})
        for cell, (_, _, digest, _) in enumerate(page):
            placements[digest] = (atlas_id, cell)
        page.clear()

    seen = set()
    for prepared_row in prepared:
        row, picture, digest, encoded_hash = prepared_row
        if row["kind"] != "cell" or digest in seen:
            continue
        seen.add(digest)
        page.append(prepared_row)
        if len(page) == capacity:
            flush()
    flush()
    changes, counts = [], {}
    for row, picture, digest, encoded_hash in prepared:
        build, key = row["build"], str(row["id"])
        entry = indexes[build]["items"][key]
        before = {field: entry.get(field) for field in ("atlas", "cell", "icon")}
        if file_hash(picture) != encoded_hash:
            raise ValueError("a prepared picture changed during assembly")
        if row["kind"] == "cell":
            entry["atlas"], entry["cell"] = placements[digest]
            entry.pop("icon", None)
        else:
            if picture.suffix.lower() != ".png":
                raise ValueError("native icons must be supplied as their original PNG")
            name = f"icons/{encoded_hash[:16]}.png"
            target = child(output, name)
            target.parent.mkdir(parents=True, exist_ok=True)
            if target.exists() and file_hash(target) != encoded_hash:
                raise ValueError("content-addressed icon name collision")
            shutil.copyfile(picture, target)
            entry.update(atlas=None, cell=None, icon=name)
        changes.append({"build": build, "id": int(key), "kind": row["kind"], "reason": row["reason"],
                        "before": before, "after": {field: entry.get(field) for field in before},
                        "rgba_sha256": digest, "input_file_sha256": encoded_hash})
    for build, index in indexes.items():
        write_json(child(output, manifest["builds"][build]), index)
    updated["generator"]["repair"] = {"baseline_pack_sha256": baseline_hash,
                                       "plan_sha256": file_hash(plan_path),
                                       "renderer_sha256": source_hash,
                                       "retained_atlases": len(manifest["atlases"]),
                                       "replacement_atlases": len(updated["atlases"]) - len(manifest["atlases"]),
                                       "replacement_encoding": "lossless-webp/original-png",
                                       "replacements": len(changes)}
    write_json(output / "pack.json", updated)
    by_atlas = {}
    for digest, (atlas_id, cell) in placements.items():
        by_atlas.setdefault(atlas_id, []).append((cell, digest))
    for atlas_id, uses in by_atlas.items():
        atlas = updated["atlases"][atlas_id]
        with Image.open(child(output, atlas["file"])) as source:
            source.load()
            for cell, expected in uses:
                image = source.crop(cell_box(cell, atlas["columns"], size)).convert("RGBA")
                if cell_hash(image) != expected:
                    raise ValueError("encoded replacement pixels differ from the reviewed input")
    mutable = {"pack.json", *(manifest["builds"][build] for build in indexes)}
    for relative, digest in originals.items():
        if file_hash(child(baseline, relative)) != digest:
            raise ValueError("baseline changed during repair")
        if relative not in mutable and file_hash(child(output, relative)) != digest:
            raise ValueError(f"retained file changed: {relative}")
    sprite_fields = {"atlas", "cell", "icon"}
    for build, relative in manifest["builds"].items():
        original, current = read_json(child(baseline, relative)), read_json(child(output, relative))
        if original.keys() != current.keys() or original["items"].keys() != current["items"].keys():
            raise ValueError("build index structure changed")
        if {k: v for k, v in original.items() if k != "items"} != {k: v for k, v in current.items() if k != "items"}:
            raise ValueError("sets or character metadata changed")
        for key, before in original["items"].items():
            after = current["items"][key]
            if (build, key) not in selected and before != after:
                raise ValueError(f"unselected reference changed: {build}/{key}")
            if {k: v for k, v in before.items() if k not in sprite_fields} != {k: v for k, v in after.items() if k not in sprite_fields}:
                raise ValueError(f"item metadata changed: {build}/{key}")
        kinds = Counter(row["kind"] for row in changes if row["build"] == build)
        counts[build] = {"replaced": sum(kinds.values()), "cells": kinds["cell"], "icons": kinds["icon"],
                         "preserved": len(original["items"]) - sum(kinds.values())}
    if renderer_source_hash() != source_hash:
        raise ValueError("renderer changed during repair")
    return {"baseline_pack_sha256": baseline_hash, "candidate_pack_sha256": file_hash(output / "pack.json"),
            "renderer_sha256": source_hash, "retained_files_verified": len(originals) - len(mutable),
            "unique_replacement_cells": len(placements), "per_build": counts, "changes": changes,
            "pack_bytes": sum(path.stat().st_size for path in output.rglob("*") if path.is_file())}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("baseline", type=Path)
    parser.add_argument("plan", type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--receipt", required=True, type=Path)
    args = parser.parse_args()
    result = repair(args.baseline, args.plan, args.output)
    write_json(args.receipt, result)
    print(json.dumps({key: value for key, value in result.items() if key != "changes"}, indent=2))
