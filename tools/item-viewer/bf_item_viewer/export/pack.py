"""Preview pack: atlases of item previews for every client build, with shared cells stored once.

Layout of the output folder::

    pack.json            format, generator, cell size, atlas list (file, cells, columns, width, height)
                         and the per-build index files
    atlas/<hash>.webp    content-addressed atlas images (first 16 hex of the file's SHA-256)
    builds/<build>.json  per item: atlas, cell, name, slogan, description, detail1, detail2, main, sub

Two layers avoid work and duplicates:

* input key: SHA-256 of the render settings, the camera row and the contents of every file the
  preview reads (meshes, motions, resolved textures). A key that was rendered before (in this or
  another build) is not rendered again.
* cell hash: SHA-256 of the RGBA pixels. Different inputs that draw the same picture share one cell.

Cells are numbered in order of first appearance (build, category, item id), so one build's items
sit in few atlases. Work files (rendered cells, key table) live in a separate folder and make a
second run cheap.
"""

from __future__ import annotations

import csv
import hashlib
import io
import json
import os
import platform
import re
import sys
import time
from concurrent.futures import ProcessPoolExecutor
from dataclasses import asdict, dataclass, replace
from pathlib import Path

import numpy as np
import PIL
from PIL import Image

from .. import __version__
from ..builds import BuildMap, BuildSpec, load_build_map
from ..archives.index import ResourceIndex
from ..catalog.builder import CHARACTER_BASE
from ..catalog.model import Kind
from ..catalog.spreadsheet import read_sheets
from ..service import ClientSession, PreviewRequest
from .atlas import CELL, COLUMNS, FORMATS, ROWS, build_atlas, cell_hash, encode, split_pages
from .batch import ExportOptions, render_transparent

PACK_FORMAT = 1
PACK_KINDS = (Kind.WEAPON, Kind.CHARACTER, Kind.COSTUME_PART, Kind.SLOT_ITEM)
# The categories the ban-pick editor lists; items of these without a model are indexed without a cell.
PACK_MAIN = ("Clothes", "Accessories", "Weapons", "Characters")


def _row_text(raw: dict, name: str) -> str:
    wanted_key = name.lower()
    for key, value in raw.items():
        if key.lower() == wanted_key and value:
            return str(value)
    return ""


TRANSLATION_TABLE = "StringTable/TranslatedXmlTable.csv"
TRANSLATION_PREFIX = "ShopItem2-ShopItem-itemId-"
SET_TRANSLATION_PREFIX = "ShopItem2-StockSet-stockSetId-"
# Pack field -> ShopItem column; the translation table names the column after the item id.
TEXT_COLUMNS = {"name": "itemName", "slogan": "slogan", "description": "itemDesc", "detail1": "desc1", "detail2": "desc2"}


def translations(session: ClientSession, column: str | None) -> dict[tuple[str, str], str]:
    """(item id, ShopItem column) -> translated text from the client's translation table. The Chinese clients keep
    the Korean text in ShopItem2 for about a fifth of their items and ship the Chinese text only in this table."""
    if not column or not session.index.exists(TRANSLATION_TABLE):
        return {}
    raw = session.index.read(TRANSLATION_TABLE)
    text = raw.decode("utf-16" if raw.startswith((b"\xff\xfe", b"\xfe\xff")) else "utf-8-sig")
    table = {}
    for row in csv.DictReader(io.StringIO(text), delimiter="\t"):
        category = row.get("SubCategory") or ""
        if not row.get(column):
            continue
        if category.startswith(TRANSLATION_PREFIX):
            table[(row.get("KeyString") or "", category[len(TRANSLATION_PREFIX):])] = row[column]
        elif category.startswith(SET_TRANSLATION_PREFIX):
            table[(row.get("KeyString") or "", "StockSet-stockSetId-" + category[len(SET_TRANSLATION_PREFIX):])] = row[column]
    return table


def item_texts(item, table: dict[tuple[str, str], str] | None = None) -> dict:
    """The native item-info fields of a ShopItem row (kr-812 sub_95BB60 fills lblSlogan, lblItemDesc,
    lblDesc1 and lblDesc2 from them), with the client's translation applied; empty when the row has none."""
    table = table or {}
    texts = {"name": item.name}
    for field, column in TEXT_COLUMNS.items():
        own = item.name if field == "name" else _row_text(item.raw, column)
        texts[field] = table.get((str(item.item_id), column)) or own
    return texts


WEAPON_GRAPH = "Config/ItemSpecGraphWeapon_Generated.csv"
# The five gauges of the weapon popup in order: Damage, HitRatio, MoveSpeed (labelled weight), FireCoolTick (fire
# rate), Eiz (auto aim). kr-812 Ui/DlgWeaponInfoPopup.ui names them; ShopItem2 SpecUi_* fill them in legacy builds.
SPEC_COLUMNS = ("SpecUi_Damage", "SpecUi_Hitratio", "SpecUi_MoveSpeed", "SpecUi_FireCoolTick", "SpecUi_Eiz")
# Modern builds keep only SpecUi_Check in ShopItem2; their DlgInfoPopupWeapon.ui shows the same five labels, filled
# from the generated graph table.
GRAPH_COLUMNS = ("Damage", "HitRatio", "Weight", "AttackSpeed", "Eiz")


def weapon_graph(session: ClientSession) -> dict[int, list[int]]:
    """Item id -> the five weapon gauge values from the build's generated graph table, when it ships one."""
    if not session.index.exists(WEAPON_GRAPH):
        return {}
    graph = {}
    for row in csv.DictReader(io.StringIO(session.index.read(WEAPON_GRAPH).decode("utf-8-sig"))):
        try:
            graph[int(row["ShopItemId"])] = [int(float(row[column])) for column in GRAPH_COLUMNS]
        except (KeyError, TypeError, ValueError):
            continue
    return graph


def weapon_spec(item, graph: dict[int, list[int]]) -> list[int] | None:
    """The weapon popup's gauge values (0-100) for an item the client shows them for; None otherwise."""
    check = _row_text(item.raw, "SpecUi_Check")
    if check and check != "1":
        return None
    if item.item_id in graph:
        return graph[item.item_id]
    if check != "1":
        return None
    try:
        return [int(float(_row_text(item.raw, column))) for column in SPEC_COLUMNS]
    except ValueError:
        return None


ARMOR_GRAPH = "Config/ItemSpecGraphArmor_Generated.csv"
# DlgInfoPopupSetArmor.ui gauges in order: pnlArmorPoint, pnlReloadSpeed, pnlMoveSpeed.
ARMOR_COLUMNS = ("ArmorPoint", "ReloadSpeed", "MoveSpeed")
SET_SUB_CATEGORY = "SetClothes"
STOCK_SLOTS = 10


def armor_graph(index: ResourceIndex) -> dict[str, list[int]]:
    """Stock set id -> the three set-armor gauge values, from a build that ships the generated table."""
    if not index.exists(ARMOR_GRAPH):
        return {}
    graph = {}
    for row in csv.DictReader(io.StringIO(index.read(ARMOR_GRAPH).decode("utf-8-sig"))):
        try:
            graph[row["StockSetId"]] = [int(float(row[column])) for column in ARMOR_COLUMNS]
        except (KeyError, TypeError, ValueError):
            continue
    return graph


def armor_tables(build_map: BuildMap, cache_root: Path) -> dict[str, dict[str, list[int]]]:
    """Every mapped build's set-armor table. Only modern clients ship one; the owner asked for the stats on every
    build, so older builds borrow them by stock set id (KR, TW and ID builds share ids; CN builds share theirs)."""
    tables = {}
    for spec in build_map.builds:
        root = build_map.root(spec)
        if not root.is_dir():
            continue
        index = ResourceIndex(root, cache_root)
        if not index.open_cached():
            index.build()
        table = armor_graph(index)
        if table:
            tables[spec.id] = table
    return tables


def donor_order(own: str, specs: list[BuildSpec]) -> list[str]:
    """Where a build borrows data its own client lacks (owner's rule): newer clients of its own region first, newest
    first; then the Korean clients, newest first; then the rest. The region is the build id's prefix, the year the
    release's."""
    def region(spec: BuildSpec) -> str:
        return spec.id.split("-")[0]

    def year(spec: BuildSpec) -> int:
        found = re.findall(r"\d{4}", spec.release)
        return int(found[0]) if found else 0
    mine = next((spec for spec in specs if spec.id == own), None)
    home = region(mine) if mine else ""
    others = [spec for spec in specs if spec.id != own]
    return [spec.id for spec in sorted(others, key=lambda spec: (region(spec) != home, region(spec) != "kr", -year(spec)))]


def set_stats(set_id: str, own: str, tables: dict[str, dict[str, list[int]]], order: list[str]) -> list[int] | None:
    """The build's own value first, then the donors in order."""
    for build in [own, *order]:
        if set_id in tables.get(build, {}):
            return tables[build][set_id]
    return None


def stock_sets(session: ClientSession, own: str, tables: dict, texts: dict[tuple[str, str], str],
               order: list[str]) -> list[dict]:
    """The shop's clothing sets: names and texts (translated like items), member item ids, the characters allowed,
    the shop icon and the set-armor gauges."""
    sheets = read_sheets(session.index.read(session.profile.raw["catalog"]["shop_workbook"]), {"StockSet", "Stock"})
    stock = {row.get("stockId"): row.get("itemId") for row in sheets.get("Stock", [])}
    rows = [row for row in sheets.get("StockSet", []) if row.get("subCategory") == SET_SUB_CATEGORY]
    icon_folder = session.profile.raw["catalog"]["item_icon_folder"]
    sets, seen = [], set()
    for row in rows:
        set_id = row.get("stockSetId") or ""
        members = []
        for slot in range(1, STOCK_SLOTS + 1):
            item = stock.get(row.get(f"stockId{slot:02d}"))
            try:
                if item and int(float(item)) > 0:
                    members.append(int(float(item)))
            except ValueError:
                continue
        if not set_id or not members or tuple(sorted(members)) in seen:
            continue
        seen.add(tuple(sorted(members)))

        def text(column: str) -> str:
            return texts.get((set_id, "StockSet-stockSetId-" + column)) or row.get(column) or ""
        icon = f"{icon_folder}/{set_id}.png"
        sets.append({"id": int(set_id), "name": text("stockSetName"), "slogan": text("slogan"),
                     "description": text("itemDesc"), "detail1": text("desc1"), "detail2": text("desc2"),
                     "items": members, "allow": row.get("allowChr") or "",
                     "stats": set_stats(set_id, own, tables, order),
                     "icon_path": icon if session.index.exists(icon) else None})
    return sets


def wanted(item) -> bool:
    return item.kind in PACK_KINDS or (item.main_category in PACK_MAIN and item.kind != Kind.SPRAY)


@dataclass
class WorkDirs:
    root: Path

    @property
    def cells(self) -> Path:
        return self.root / "cells"

    @property
    def keys(self) -> Path:
        return self.root / "keys"

    @property
    def icons(self) -> Path:
        return self.root / "icons"

    @property
    def results(self) -> Path:
        return self.root / "results"

    def make(self) -> None:
        self.icons.mkdir(parents=True, exist_ok=True)
        for folder in (self.cells, self.keys, self.results):
            folder.mkdir(parents=True, exist_ok=True)


def _write_atomic(path: Path, data: bytes) -> None:
    tmp = path.with_name(path.name + f".{os.getpid()}.part")
    tmp.write_bytes(data)
    tmp.replace(path)


def _profile_hash(spec: BuildSpec) -> str:
    return hashlib.sha256(json.dumps(spec.profile().raw, sort_keys=True).encode()).hexdigest()


def renderer_source_hash(package: Path = Path(__file__).resolve().parents[1]) -> str:
    """Fingerprint the Python implementation and its bundled rendering and profile data."""
    files = [(path.relative_to(package).as_posix(), hashlib.sha256(path.read_bytes()).hexdigest())
             for path in sorted(package.rglob("*")) if path.is_file() and path.suffix in (".py", ".json")]
    return hashlib.sha256(json.dumps(files).encode()).hexdigest()


def _file_hash(session: ClientSession, path: str, content: dict) -> str | None:
    if path not in content:
        content[path] = hashlib.sha256(session.index.read(path)).hexdigest() if session.index.exists(path) else None
    return content[path]


def input_key(session: ClientSession, item, request: PreviewRequest, render_settings: str, content: dict) -> str:
    """Hash of everything a preview depends on; ``content`` memoizes file hashes by path."""
    record = session.camera_record(item.item_id)
    paths = session.input_files(request)
    shading = session.profile.raw.get("shading", {})
    for section, field in (("toon", "ramp"), ("specular", "table")):
        setting = shading.get(section, {})
        if setting.get("enabled") and setting.get(field):
            paths.append(setting[field])
    if item.kind == Kind.SLOT_ITEM:
        # The workbook supplies the socket table and FighterConfig supplies the wearer's type.
        paths.extend(("Models/SlotItem/SlotLocation.csv", session.profile.raw["catalog"]["shop_workbook"],
                      "Config/FighterConfig.xml"))
    files = [(path, _file_hash(session, path, content)) for path in paths]
    state = [render_settings, item.kind.value, asdict(request), record.position, record.quaternion,
             record.zoom, files, item.raw.get("displayOption", "")]
    return hashlib.sha256(json.dumps(state, sort_keys=True).encode()).hexdigest()


def render_build(build_id: str, games_dir: str, work_dir: str, options: ExportOptions, source_hash: str) -> dict:
    """Render every packable item of one build into the work folder; returns its result record."""
    build_map = load_build_map(games_dir)
    spec = build_map.get(build_id)
    options = replace(options, reference_build=spec.base)
    root = build_map.root(spec)
    started = time.time()
    work = WorkDirs(Path(work_dir))
    work.make()
    if not root.is_dir():
        return {"build": build_id, "skipped": f"client folder {root} is missing"}
    try:
        session = ClientSession(root, work.root / "client-index", spec.profile())
        session.load()
    except Exception as error:
        return {"build": build_id, "skipped": f"cannot load: {type(error).__name__}: {error}"}
    catalog = session.catalog
    main_order = {c.name: c.order for c in catalog.main_categories}
    sub_order = {c.name: c.order for c in catalog.sub_categories}
    items = sorted((i for i in catalog.items if wanted(i)),
                   key=lambda i: (main_order.get(i.main_category, 1 << 20), i.main_category,
                                  sub_order.get(i.sub_category, 1 << 20), i.sub_category, i.item_id))
    settings = json.dumps([source_hash, platform.python_version(), PIL.__version__, np.__version__,
                           _profile_hash(spec), asdict(options)], sort_keys=True)
    content: dict = {}
    texts = translations(session, spec.text_column)
    graph = weapon_graph(session)

    def keep_icon(path: str) -> str:
        """The client's own pre-rendered picture, stored once by pixel hash like a rendered cell."""
        with Image.open(io.BytesIO(session.index.read(path))) as icon:
            image = icon.convert("RGBA")
        digest = cell_hash(image)
        target = work.icons / f"{digest}.png"
        if not target.exists():
            tmp = target.with_name(target.name + f".{os.getpid()}.part")
            image.save(tmp, format="PNG", compress_level=3)
            tmp.replace(target)
        return digest
    rendered = reused = failed = 0
    entries = []
    for number, item in enumerate(items, 1):
        entry = {"id": item.item_id, "main": item.main_category, "sub": item.sub_category,
                 **item_texts(item, texts), "spec": weapon_spec(item, graph),
                 "allow": _row_text(item.raw, "allowChr"), "cell": None, "status": item.status.value, "error": None}
        # kr-812 sub_9B1630 -> kr-812 sub_9B23D0 and kr-1256 sub_A29090 -> kr-1256 sub_A29C30
        # select the native icon before the model, including items that have both resources.
        if item.icon:
            try:
                entry["icon"] = keep_icon(item.icon)
            except Exception as error:  # recorded, never dropped
                entry["error"] = f"icon: {type(error).__name__}: {error}"
                failed += 1
        elif item.model is not None and item.model.meshes and item.kind in PACK_KINDS:
            try:
                request = PreviewRequest(item.item_id, CELL[0], CELL[1], supersample=options.supersample)
                key = input_key(session, item, request, settings, content)
                key_file = work.keys / key
                cached = key_file.read_text(encoding="ascii").strip() if key_file.exists() else ""
                if (len(cached) == 64 and all(char in "0123456789abcdef" for char in cached)
                        and (work.cells / f"{cached}.png").is_file()):
                    entry["cell"] = cached
                    reused += 1
                else:
                    image, _ = render_transparent(session, item.item_id, options, None, None)
                    image = image.convert("RGBA")
                    digest = cell_hash(image)
                    cell_file = work.cells / f"{digest}.png"
                    if not cell_file.exists():
                        tmp = cell_file.with_name(cell_file.name + f".{os.getpid()}.part")
                        image.save(tmp, format="PNG", compress_level=3)
                        tmp.replace(cell_file)
                    _write_atomic(key_file, digest.encode())
                    entry["cell"] = digest
                    rendered += 1
            except Exception as error:  # recorded, never dropped
                entry["error"] = f"{type(error).__name__}: {error}"
                failed += 1
        entries.append(entry)
        if number % 200 == 0:
            print(f"{build_id}: {number}/{len(items)} indexed, {rendered} rendered, {reused} reused, {failed} errors",
                  file=sys.stderr, flush=True)
    all_builds = load_build_map(games_dir)
    sets = stock_sets(session, build_id, armor_tables(all_builds, work.root / "client-index"), texts,
                      donor_order(build_id, all_builds.builds))
    for entry in sets:
        path = entry.pop("icon_path")
        entry["icon"] = keep_icon(path) if path else None
    # allowChr names FighterConfig rows; each one's character item (itemId = 300000 + field) carries the shown name.
    by_id = {item.item_id: item for item in catalog.items}
    characters = {}
    for fighter in catalog.fighters:
        shown = by_id.get(CHARACTER_BASE + fighter.field_id)
        if shown is not None:
            characters[fighter.name] = texts.get((str(shown.item_id), "itemName")) or shown.name
    result = {"build": build_id, "release": spec.release, "items": entries, "sets": sets, "characters": characters,
              "rendered": rendered,
              "reused": reused, "failed": failed, "seconds": round(time.time() - started, 1),
              "catalog_problems": catalog.problems[:5]}
    _write_atomic(work.results / f"{build_id}.json", json.dumps(result, ensure_ascii=False).encode())
    return result


def assemble(build_map: BuildMap, results: list[dict], out_dir: Path, work: WorkDirs, fmt: str = "webp",
             quality: int = 85, lossless: bool = False, compare: bool = True,
             source_hash: str | None = None) -> dict:
    """Number the unique cells, write atlases, build indexes and pack.json; returns statistics."""
    out_dir.mkdir(parents=True, exist_ok=True)
    (out_dir / "atlas").mkdir(exist_ok=True)
    (out_dir / "builds").mkdir(exist_ok=True)
    order: dict[str, int] = {}
    uses: dict[str, set] = {}
    for result in results:
        for entry in result["items"]:
            digest = entry["cell"]
            if digest:
                order.setdefault(digest, len(order))
                uses.setdefault(digest, set()).add(result["build"])
    per_page = COLUMNS * ROWS
    digests = sorted(order, key=order.get)
    atlases = []
    sizes = {"png": 0, "webp-lossless": 0, "webp-lossy": 0}
    for page in split_pages(digests, per_page):
        cells = []
        for digest in page:
            with Image.open(work.cells / f"{digest}.png") as image:
                cells.append(image.convert("RGBA"))
        atlas = build_atlas(cells, COLUMNS, CELL)
        selected = "png" if fmt == "png" else "webp-lossless" if lossless else "webp-lossy"
        data = encode(atlas.image, fmt, quality, lossless)
        name = hashlib.sha256(data).hexdigest()[:16]
        _write_atomic(out_dir / "atlas" / f"{name}.{fmt}", data)
        atlases.append({"file": f"atlas/{name}.{fmt}", "cells": atlas.cells, "columns": atlas.columns,
                        "width": atlas.width, "height": atlas.height})
        if compare:
            sizes["png"] += len(data if selected == "png" else encode(atlas.image, "png"))
            sizes["webp-lossless"] += len(data if selected == "webp-lossless" else encode(atlas.image, "webp", lossless=True))
            sizes["webp-lossy"] += len(data if selected == "webp-lossy" else encode(atlas.image, "webp", quality))
    placement = {d: (order[d] // per_page, order[d] % per_page) for d in digests}
    (out_dir / "icons").mkdir(exist_ok=True)
    written: dict[str, str] = {}

    def icon_file(digest: str) -> str:
        """icons/<first 16 hex>.<format>: one file per distinct picture across all builds."""
        if digest not in written:
            with Image.open(work.icons / f"{digest}.png") as icon:
                data = encode(icon.convert("RGBA"), fmt, quality, lossless)
            written[digest] = f"icons/{digest[:16]}.{fmt}"
            _write_atomic(out_dir / written[digest], data)
        return written[digest]
    # A weapon its own client gives no gauges borrows them by item id (donor_order): the owner wants the popup on
    # every weapon card.
    gauged = {r["build"]: {e["id"]: e["spec"] for e in r["items"] if e.get("spec")} for r in results}
    borrowed: dict[str, dict[int, list[int]]] = {}
    for build in gauged:
        borrowed[build] = {}
        for donor in donor_order(build, build_map.builds):
            for item_id, values in gauged.get(donor, {}).items():
                borrowed[build].setdefault(item_id, values)
    builds = {}
    per_build = {}
    for result in results:
        items = {}
        for entry in result["items"]:
            atlas, cell = placement[entry["cell"]] if entry["cell"] else (None, None)
            items[str(entry["id"])] = {"atlas": atlas, "cell": cell, "name": entry["name"],
                                       "slogan": entry["slogan"], "description": entry["description"],
                                       "detail1": entry["detail1"], "detail2": entry["detail2"],
                                       "main": entry["main"], "sub": entry["sub"]}
            if entry.get("spec"):
                items[str(entry["id"])]["spec"] = entry["spec"]
            elif entry["main"] == "Weapons" and borrowed.get(result["build"], {}).get(entry["id"]):
                items[str(entry["id"])]["spec"] = borrowed[result["build"]][entry["id"]]
            if entry.get("allow"):
                items[str(entry["id"])]["allow"] = entry["allow"]
            if entry.get("icon"):
                items[str(entry["id"])]["icon"] = icon_file(entry["icon"])
        sets = {str(entry["id"]): {**{key: value for key, value in entry.items() if key not in ("id", "icon")},
                                   "icon": icon_file(entry["icon"]) if entry.get("icon") else None}
                for entry in result.get("sets", [])}
        _write_atomic(out_dir / "builds" / f"{result['build']}.json",
                      json.dumps({"format": PACK_FORMAT, "build": result["build"], "items": items, "sets": sets,
                                  "characters": result.get("characters", {})},
                                 ensure_ascii=False, indent=1).encode())
        builds[result["build"]] = f"builds/{result['build']}.json"
        cells = {e["cell"] for e in result["items"] if e["cell"]}
        per_build[result["build"]] = {"items": len(items), "cells": len(cells), "failed": result.get("failed", 0)}
    generator = {"tool": "bf_item_viewer", "version": __version__,
                 "renderer_sha256": source_hash or renderer_source_hash(),
                 "image_format": fmt, "quality": None if (fmt == "png" or lossless) else quality,
                 "lossless": lossless or fmt == "png", "python": platform.python_version(), "pillow": PIL.__version__,
                 "profiles": {b.base: _profile_hash(b) for b in build_map.builds if b.id in builds}}
    pack = {"format": PACK_FORMAT, "generator": generator, "cell": list(CELL), "atlases": atlases, "builds": builds}
    _write_atomic(out_dir / "pack.json", json.dumps(pack, ensure_ascii=False, indent=1).encode())
    shared = sum(1 for d in digests if len(uses[d]) > 1)
    atlas_bytes = sum((out_dir / file).stat().st_size for file in {a["file"] for a in atlases})
    index_bytes = (out_dir / "pack.json").stat().st_size + sum((out_dir / file).stat().st_size for file in builds.values())
    return {"unique_cells": len(digests), "shared_cells": shared, "atlases": len(atlases), "per_build": per_build,
            "cell_references": sum(1 for r in results for e in r["items"] if e["cell"]),
            "atlas_bytes": atlas_bytes, "pack_bytes": atlas_bytes + index_bytes,
            "format_comparison_bytes": sizes if compare else None}


def build_pack(out_dir: str | Path, games_dir: str | Path | None = None, builds: list[str] | None = None,
               jobs: int = 2, fmt: str = "webp", quality: int = 85, lossless: bool = False,
               work_dir: str | Path | None = None, supersample: int = 3, progress=print) -> dict:
    """Render all builds (in parallel processes) and assemble the pack. Returns statistics."""
    build_map = load_build_map(games_dir)
    out_dir = Path(out_dir).resolve()
    work = WorkDirs(Path(work_dir).resolve() if work_dir else out_dir.with_name(out_dir.name + "-work"))
    ids = builds or [b.id for b in build_map.builds]
    if len(set(ids)) != len(ids):
        raise ValueError("pack build ids must be distinct")
    if fmt not in FORMATS or not 0 <= quality <= 100:
        raise ValueError("format must be png or webp and quality must be 0..100")
    if not 1 <= jobs <= len(build_map.builds) or not 1 <= supersample <= 4:
        raise ValueError(f"jobs must be 1..{len(build_map.builds)} and supersample must be 1..4")
    if out_dir.is_relative_to(work.root) or work.root.is_relative_to(out_dir):
        raise ValueError("pack output and render cache must be separate folders")
    for build_id in ids:
        root = build_map.root(build_map.get(build_id)).resolve()
        if out_dir.is_relative_to(root) or work.root.is_relative_to(root):
            raise ValueError("refusing to write pack output or cache into a client folder")
    work.make()
    source_hash = renderer_source_hash()
    options = ExportOptions(width=CELL[0], height=CELL[1], supersample=supersample,
                            format=fmt, quality=quality, lossless=lossless)
    results: dict[str, dict] = {}
    with ProcessPoolExecutor(max_workers=min(jobs, len(ids))) as pool:
        futures = {b: pool.submit(render_build, b, str(build_map.games_dir), str(work.root), options, source_hash) for b in ids}
        for build_id, future in futures.items():
            result = future.result()
            results[build_id] = result
            if "skipped" in result:
                progress(f"{build_id}: skipped ({result['skipped']})")
            else:
                progress(f"{build_id}: {len(result['items'])} items, rendered {result['rendered']}, "
                         f"reused {result['reused']}, failed {result['failed']}, {result['seconds']} s")
    done = [results[b] for b in ids if "skipped" not in results[b]]
    if renderer_source_hash() != source_hash:
        raise RuntimeError("renderer source changed during the run; rerun with the final source")
    stats = (assemble(build_map, done, out_dir, work, fmt, quality, lossless, source_hash=source_hash)
             if done else {"per_build": {}})
    stats["skipped"] = {b: r["skipped"] for b, r in results.items() if "skipped" in r}
    stats["unmapped"] = build_map.unmapped
    _write_atomic(work.root / "pack-stats.json", json.dumps(stats, ensure_ascii=False, indent=1).encode())
    return stats
