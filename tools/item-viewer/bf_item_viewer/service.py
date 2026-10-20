"""One client session: index, catalog, cameras and preview rendering behind a single API.

The GUI, the CLI and batch export all call :meth:`ClientSession.render_preview`, so a grid
thumbnail, the detail view and an exported PNG of the same item and settings are the same
picture at different resolutions.
"""

from __future__ import annotations

import hashlib
import json
import os
import pickle
import threading
from dataclasses import asdict, dataclass, field, replace
from pathlib import Path

import numpy as np

from . import __version__
from .archives.index import ResourceIndex
from .catalog.builder import CatalogBuilder
from .catalog.model import Catalog, CatalogItem, Kind
from .catalog.spreadsheet import read_sheets
from .rendering.attach import SlotTable
from .rendering.camera import (CameraRecord, PreviewProfile, ViewSettings, available_profiles, build_camera,
                               parse_camera_table)
from .rendering.compose import Composer
from .rendering.scene import render
from .resources.textures import TextureResolver

CATALOG_CACHE_VERSION = 4


def default_cache_root() -> Path:
    base = os.environ.get("LOCALAPPDATA") or str(Path.home() / ".cache")
    return Path(base) / "BFItemViewer" / "cache"


def detect_profile(client_root: Path) -> PreviewProfile:
    """Pick the build profile whose marker files exist in the client folder."""
    matches = []
    for profile in available_profiles():
        markers = profile.raw.get("detect", {}).get("any_file", [])
        if any((client_root / m).exists() for m in markers):
            matches.append(profile)
    if not matches:
        raise LookupError(f"{client_root} matches no known build profile "
                          f"({', '.join(p.build for p in available_profiles())})")
    if len(matches) > 1:
        raise LookupError(f"{client_root} matches several build profiles: {[p.build for p in matches]}")
    return matches[0]


def parse_argb(text: str) -> tuple[float, float, float]:
    value = int(text, 16)
    return ((value >> 16) & 255) / 255.0, ((value >> 8) & 255) / 255.0, (value & 255) / 255.0


@dataclass
class PreviewRequest:
    item_id: int
    width: int = 256
    height: int = 256
    wearer: str | None = None
    spray_index: int | None = None        # palette index; None = unsprayed
    user_quaternion: tuple = (0.0, 0.0, 0.0, 1.0)
    supersample: int = 3
    shading: bool = True                  # toon ramp, specular and outline per the profile
    canvas_scale: float = 1.0             # export "expand" policy; 1.0 = the client's own control


@dataclass
class PreviewResult:
    rgba: np.ndarray
    metadata: dict = field(default_factory=dict)


class ClientSession:
    """Everything about one selected client folder."""

    def __init__(self, client_root: Path | str, cache_root: Path | str | None = None,
                 profile: PreviewProfile | None = None):
        self.client_root = Path(client_root).resolve()
        self.cache_root = Path(cache_root) if cache_root else default_cache_root()
        self.profile = profile or detect_profile(self.client_root)
        self.index = ResourceIndex(self.client_root, self.cache_root)
        self.catalog: Catalog | None = None
        self.cameras: dict[int, CameraRecord] = {}
        self.camera_problems: list[str] = []
        self._slot_table: SlotTable | None = None
        self._resolver: TextureResolver | None = None
        self._composer: Composer | None = None
        self._lock = threading.RLock()
        self._by_id: tuple | None = None

    @property
    def build(self) -> str:
        return self.profile.build

    # -- loading ---------------------------------------------------------------------------
    def load(self, progress=None, cancel: threading.Event | None = None, rescan: bool = False) -> dict:
        """Index (incrementally) and build or load the catalog. ``progress(stage, done, total)``."""
        report = progress or (lambda stage, done, total: None)
        if rescan or not self.index.open_cached():
            report("indexing archives", 0, 1)
            self.index.build(progress=lambda d, t, name: report(f"indexing {name}", d, t), cancel=cancel)
        report("reading camera table", 0, 1)
        table = self.profile.preview["camera_table"]
        if self.index.exists(table):
            self.cameras, self.camera_problems = parse_camera_table(self.index.read(table))
        else:
            self.camera_problems = [f"{table} is missing; every item uses the default camera record"]
        self.catalog = self._load_catalog(report, cancel)
        self._resolver = TextureResolver(self.index, self.profile.raw.get("texture_pools", ["Models/Characters/Texture"]))
        self._composer = Composer(self.index)
        from .rendering.shading import load_shading
        self.shading = load_shading(self.index, self.profile)
        report("ready", 1, 1)
        return self.summary()

    def _catalog_key(self) -> str:
        wb = self.profile.raw["catalog"]["shop_workbook"]
        prov = self.index.provenance(wb)
        state = json.dumps([CATALOG_CACHE_VERSION, self.build, __version__, prov and [prov.source_file, prov.size],
                            self.index.summary()["members"]])
        return hashlib.sha1(state.encode()).hexdigest()[:16]

    def _load_catalog(self, report, cancel) -> Catalog:
        path = self.index.cache_dir / f"catalog-{self.build}-{self._catalog_key()}.pkl"
        if path.is_file():
            try:
                with open(path, "rb") as handle:
                    return pickle.load(handle)
            except Exception:
                path.unlink(missing_ok=True)
        builder = CatalogBuilder(self.index, self.profile, progress=lambda m: report(m, 0, 1))
        catalog = builder.build_catalog()
        if cancel is not None and cancel.is_set():
            raise InterruptedError("cancelled")
        tmp = path.with_suffix(".tmp")
        with open(tmp, "wb") as handle:
            pickle.dump(catalog, handle)
        tmp.replace(path)
        return catalog

    def summary(self) -> dict:
        out = self.index.summary()
        out["build"] = self.build
        if self.catalog:
            out["items"] = len(self.catalog.items)
            out["statuses"] = self.catalog.counts()
            out["catalog_problems"] = self.catalog.problems
        out["camera_records"] = len(self.cameras)
        out["camera_problems"] = self.camera_problems[:20]
        return out

    # -- per item ----------------------------------------------------------------------------
    def item(self, item_id: int) -> CatalogItem:
        if self.catalog is None:
            raise RuntimeError("load() the session first")
        found = self._items().get(item_id)
        if found is None:
            raise KeyError(f"item {item_id} is not in the {self.build} catalog")
        return found

    def _items(self) -> dict[int, CatalogItem]:
        if self.catalog is None:
            return {}
        if self._by_id is None or self._by_id[0] is not self.catalog:
            self._by_id = (self.catalog, self.catalog.by_id())
        return self._by_id[1]

    def camera_record(self, item_id: int) -> CameraRecord:
        """The camera row the preview uses. An item that borrows its model (``modelFrom``) is framed by
        the model owner's row: measured on kr-812 weapons, whose own rows for borrowed models are often
        the identity default (202318 -> 202315); inferred for other kinds."""
        item = self._items().get(item_id)
        owner = item.model.borrowed_from if item and item.model else None
        if owner and owner in self.cameras:
            return self.cameras[owner]
        return self.cameras.get(item_id) or self.profile.default_record(item_id)

    def wearers(self, item: CatalogItem) -> list[str]:
        return list(item.model.wearers) if item.model else []

    def palette(self) -> dict[int, tuple]:
        paint = self.profile.raw.get("paint", {})
        return {int(k): parse_argb(v) for k, v in paint.get("palette", {}).items()}

    def spray_tint(self, spray_index: int | None) -> tuple[tuple, str]:
        paint = self.profile.raw.get("paint", {})
        if spray_index is None:
            unsprayed = paint.get("unsprayed_tint")
            if unsprayed:
                return parse_argb(unsprayed["argb"]), f"unsprayed tint ({unsprayed['status']})"
            return (1.0, 1.0, 1.0), "no unsprayed tint known; white"
        palette = paint.get("palette", {})
        if str(spray_index) in palette:
            return parse_argb(palette[str(spray_index)]), f"palette index {spray_index} ({paint.get('status')})"
        return parse_argb(paint.get("default", "0xFFFFFFFF")), f"index {spray_index} has no case; palette default"

    def slot_table(self) -> SlotTable:
        if self._slot_table is None:
            sockets = {}
            wb = self.profile.raw["catalog"]["shop_workbook"]
            for row in read_sheets(self.index.read(wb), {"Socket"}).get("Socket", []):
                values = list(row.values())
                if len(values) >= 3 and values[0]:
                    sockets[values[0]] = values[2]
            path = "Models/SlotItem/SlotLocation.csv"
            self._slot_table = SlotTable(self.index.read(path) if self.index.exists(path) else None, sockets)
        return self._slot_table

    def render_preview(self, request: PreviewRequest) -> PreviewResult:
        """Render one item exactly as the build's preview control composes it."""
        item = self.item(request.item_id)
        record = self.camera_record(item.item_id)
        limit = self.profile.preview.get("weapon_zoom_limit")
        zoom_note = None
        if limit and item.kind == Kind.WEAPON and record.zoom > float(limit["zoom"]):
            zoom_note = f"zoom {record.zoom:g} limited to {float(limit['zoom']):g} ({limit['status']})"
            record = replace(record, zoom=float(limit["zoom"]))
        view = ViewSettings(tuple(request.user_quaternion))
        camera = build_camera(self.profile, record, request.width, request.height, view, request.canvas_scale)
        tint, tint_note = self.spray_tint(request.spray_index)
        dyed = {n.lower() for n in self.profile.raw.get("paint", {}).get("dyed_parts", {}).get("names", [])}
        with self._lock:
            scene, composition = self._composer.compose(item, request.wearer, tint)
            if item.kind == Kind.SLOT_ITEM:
                self._wear_slot_item(item, scene, composition)
            for part in scene.parts:
                if part.origin in ("default", "item") and item.kind in (Kind.COSTUME_PART, Kind.CHARACTER, Kind.SLOT_ITEM):
                    part.tint = tint if (not dyed or part.slot.lower() in dyed) else (1.0, 1.0, 1.0)
            result = render(scene, camera, self._resolver, request.supersample,
                            self.shading if request.shading else None)
        composition.tint = tint
        metadata = {
            "build": self.build,
            "item_id": item.item_id,
            "item_name": item.name,
            "kind": item.kind.value,
            "status": item.status.value,
            "width": request.width,
            "height": request.height,
            "camera": {
                "preset": "official" if view.official else "user-rotated",
                "record_source": record.source,
                "record_item": record.item_id,
                "position": list(record.position), "quaternion": list(record.quaternion), "zoom": record.zoom,
                "user_quaternion": list(view.user_quaternion),
                "canvas_scale": request.canvas_scale,
                "profile_status": {k: v.get("status") for k, v in self.profile.preview.items() if isinstance(v, dict)},
            },
            "composition": asdict(composition),
            "tint_rule": tint_note,
            "spray_index": request.spray_index,
            "sources": [self._prov(p) for p in scene.sources],
            "textures": result.textures,
            "missing_textures": result.missing_textures,
            "missing_bones": result.missing_bones,
            "warnings": list(item.warnings) + camera.notes + composition.notes + ([zoom_note] if zoom_note else []),
            "renderer_version": __version__,
        }
        return PreviewResult(result.rgba, metadata)

    def _wear_slot_item(self, item: CatalogItem, scene, composition) -> None:
        """Slot items are shown worn (measured: their camera records frame the wearer's body)."""
        from .rendering.scene import load_motion, load_part
        wearer = composition.wearer
        fighters = {f.model.lower(): f for f in (self.catalog.fighters if self.catalog else [])}
        if wearer is None:
            wearer = next((f.model for f in sorted(self.catalog.fighters, key=lambda f: f.field_id)
                           if f.selectable and self.index.list_prefix(f"Models/Characters/{f.model}/Default", ".msh")),
                          None)
        if wearer is None:
            composition.notes.append("no wearer available; slot item drawn alone")
            return
        body = self._composer._default_parts(wearer)
        motion, rule = self._composer._stand_motion(wearer)
        chr_type = fighters.get(wearer.lower()).chr_type if wearer.lower() in fighters else 0
        table = self.slot_table()
        for part in scene.parts:
            placement = table.placement(chr_type, part.slot, item.item_id)
            if placement:
                part.attach_bone = placement.bone
                part.attach_matrix = placement.matrix()
                composition.notes.append(f"{part.slot} -> {placement.bone} ({placement.source}, item row "
                                         f"{placement.item_id})")
            else:
                composition.notes.append(f"{part.slot}: no socket or SlotLocation row; drawn unattached")
        scene.parts[:0] = [load_part(self.index, p, "default") for p in body]
        scene.motion_path = motion
        scene.motion = load_motion(self.index, motion) if motion else None
        composition.wearer = wearer
        composition.default_parts = body
        composition.motion, composition.motion_rule = motion, rule
        composition.notes.append("slot item attachment rule is under investigation (graded inferred)")

    def _prov(self, path: str) -> dict:
        p = self.index.provenance(path)
        return {"path": path, "archive": p.source_file if p else None, "member": p.member if p else None}
