"""The shop/inventory preview camera, built from a build profile and the item's camera record.

Every constant comes from the build profile (``data/profiles/<build>.json``), which names the
client function it was read from. The camera is orthographic: ``canvas_width`` world units span
the preview's width, so changing the output size changes resolution, never composition.
"""

from __future__ import annotations

import csv
import io
import json
from dataclasses import dataclass, field
from pathlib import Path

import numpy as np

from .pose import quat_matrix, scaling, translation, yaw_pitch_roll

PROFILE_DIR = Path(__file__).resolve().parent.parent / "data" / "profiles"


@dataclass(frozen=True)
class CameraRecord:
    """One row of the client's per-item camera table."""

    item_id: int
    position: tuple = (0.0, 0.0, 0.0)
    quaternion: tuple = (0.0, 0.0, 0.0, 1.0)
    zoom: float = 40.0
    stillcut: str = ""
    noiconize: str = ""
    source: str = "table"           # "table" or "default" (item absent from the table)


@dataclass
class PreviewProfile:
    build: str
    raw: dict

    @property
    def preview(self) -> dict:
        return self.raw["preview"]

    def default_record(self, item_id: int) -> CameraRecord:
        d = self.preview["missing_record"]
        return CameraRecord(item_id, tuple(d["position"]), tuple(d["quaternion"]), float(d["zoom"]),
                            source="default")


def load_profile(build: str) -> PreviewProfile:
    path = PROFILE_DIR / f"{build}.json"
    if not path.is_file():
        raise FileNotFoundError(f"no preview profile for build {build!r} ({path})")
    return PreviewProfile(build, json.loads(path.read_text(encoding="utf-8")))


def available_profiles() -> list[PreviewProfile]:
    return [load_profile(p.stem) for p in sorted(PROFILE_DIR.glob("*.json"))]


def _floats(text: str, n: int) -> tuple | None:
    parts = [p for p in (text or "").replace(" ", "").split(",") if p != ""]
    if len(parts) != n:
        return None
    try:
        return tuple(float(p) for p in parts)
    except ValueError:
        return None


def parse_camera_table(data: bytes) -> tuple[dict[int, CameraRecord], list[str]]:
    """Parse ShopItemCamera.csv. Rows the client would reject are reported, not guessed."""
    for encoding in ("utf-8-sig", "cp949"):
        try:
            text = data.decode(encoding)
            break
        except UnicodeDecodeError:
            continue
    else:
        raise ValueError("camera table is neither UTF-8 nor CP949")
    records: dict[int, CameraRecord] = {}
    problems: list[str] = []
    for line, row in enumerate(csv.DictReader(io.StringIO(text)), start=2):
        try:
            item_id = int(row["shopItemId"])
        except (KeyError, TypeError, ValueError):
            problems.append(f"line {line}: bad shopItemId")
            continue
        pos = _floats(row.get("position", ""), 3)
        quat = _floats(row.get("quaternion", ""), 4)
        try:
            zoom = float(row.get("zoom") or "nan")
        except ValueError:
            zoom = float("nan")
        if pos is None or quat is None or zoom != zoom:
            problems.append(f"line {line}: item {item_id} has a malformed position/quaternion/zoom")
            continue
        if item_id in records:
            problems.append(f"line {line}: item {item_id} repeated (the client reports DuplicatedItem)")
        records[item_id] = CameraRecord(item_id, pos, quat, zoom, row.get("stillcut") or "",
                                        row.get("noiconize") or "")
    return records, problems


@dataclass
class ViewSettings:
    """What the user may change without leaving the official composition."""

    user_quaternion: tuple = (0.0, 0.0, 0.0, 1.0)   # the control's drag rotation; identity = official
    preset: str = "official"

    @property
    def official(self) -> bool:
        return tuple(round(v, 9) for v in self.user_quaternion) == (0.0, 0.0, 0.0, 1.0)


@dataclass
class PreviewCamera:
    """World and projection for a ``width`` x ``height`` preview rectangle."""

    world: np.ndarray
    view: np.ndarray
    projection: np.ndarray
    width: int
    height: int
    record: CameraRecord
    notes: list = field(default_factory=list)

    def to_screen(self, points: np.ndarray) -> np.ndarray:
        """Model-space points -> (x_pixel, y_pixel, depth) in the output raster, y down."""
        p4 = np.concatenate([points, np.ones((len(points), 1))], axis=1)
        clip = p4 @ self.world @ self.view @ self.projection
        w = clip[:, 3:4]
        w[np.abs(w) < 1e-12] = 1e-12
        ndc = clip[:, :3] / w
        x = (ndc[:, 0] + 1.0) * 0.5 * self.width
        y = (1.0 - ndc[:, 1]) * 0.5 * self.height
        return np.stack([x, y, ndc[:, 2]], axis=1)

    def normal_matrix(self) -> np.ndarray:
        return (self.world @ self.view)[:3, :3]


def look_at_lh(eye, at, up) -> np.ndarray:
    eye, at, up = (np.asarray(v, dtype=np.float64) for v in (eye, at, up))
    z = at - eye
    z /= np.linalg.norm(z)
    x = np.cross(up, z)
    x /= np.linalg.norm(x)
    y = np.cross(z, x)
    m = np.eye(4)
    m[:3, 0], m[:3, 1], m[:3, 2] = x, y, z
    m[3, :3] = (-x @ eye, -y @ eye, -z @ eye)
    return m


def ortho_off_center_lh(l: float, r: float, b: float, t: float, zn: float, zf: float) -> np.ndarray:
    m = np.zeros((4, 4))
    m[0, 0] = 2.0 / (r - l)
    m[1, 1] = 2.0 / (t - b)
    m[2, 2] = 1.0 / (zf - zn)
    m[3, 0] = (l + r) / (l - r)
    m[3, 1] = (t + b) / (b - t)
    m[3, 2] = zn / (zn - zf)
    m[3, 3] = 1.0
    return m


def build_camera(profile: PreviewProfile, record: CameraRecord, width: int, height: int,
                 view_settings: ViewSettings | None = None, canvas_scale: float = 1.0) -> PreviewCamera:
    """Reproduce the preview control's transforms for an output of ``width`` x ``height`` pixels.

    The output rectangle plays the role of the control's viewport: the projection spans
    ``canvas_width`` units across it and ``canvas_width * h/w`` down it, exactly as the client
    computes from its control rectangle, so the composition is resolution independent.
    """
    if width <= 0 or height <= 0:
        raise ValueError("output size must be positive")
    p = profile.preview
    aspect = height / width
    if canvas_scale <= 0:
        raise ValueError("canvas_scale must be positive")
    canvas = float(p["projection"]["canvas_width"]) * canvas_scale
    proj = ortho_off_center_lh(0.0, canvas, -canvas * aspect, 0.0,
                               float(p["projection"]["near"]), float(p["projection"]["far"]))
    view = look_at_lh(p["view"]["eye"], p["view"]["at"], p["view"]["up"])
    user = (view_settings or ViewSettings()).user_quaternion
    centre = p["centre"]
    world = (quat_matrix(record.quaternion)
             @ quat_matrix(user)
             @ translation(*record.position)
             @ scaling(record.zoom, record.zoom, record.zoom)
             @ yaw_pitch_roll(float(p["model_yaw"]["radians"]), 0.0, 0.0)
             @ translation(float(centre["x"]) * canvas_scale, float(centre["y_per_aspect"]) * canvas_scale * aspect,
                           float(centre["z"])))
    notes = []
    if record.source == "default":
        notes.append("item has no camera record; the client's default record is used")
    return PreviewCamera(world, view, proj, width, height, record, notes)
