"""Read the two tables used to attach slot items to a posed character."""

from __future__ import annotations

import csv
import io
from dataclasses import dataclass
from typing import TYPE_CHECKING

import numpy as np

from .pose import evaluate, quat_matrix, scaling, skin, translation

if TYPE_CHECKING:
    from .camera import PreviewCamera
    from .scene import Scene

SLOT_LOCATION = "Models/SlotItem/SlotLocation.csv"
SLOT_ITEM_LOCATION = "Models/SlotItem/SlotItemLocation.csv"


@dataclass(frozen=True)
class SlotPlacement:
    bone: str
    position: tuple[float, ...]
    rotation: tuple[float, ...]
    scale: float
    scale_vector: tuple[float, ...]

    def matrix(self) -> np.ndarray:
        size = tuple(self.scale * value for value in self.scale_vector)
        return scaling(*size) @ quat_matrix(self.rotation) @ translation(*self.position)


def _floats(text: str, default: tuple[float, ...]) -> tuple[float, ...]:
    values = tuple(float(value) for value in text.split(",")) if text else default
    if len(values) != len(default) or not np.isfinite(values).all():
        raise ValueError(f"invalid slot transform {text!r}")
    return values


def _read_placements(data: bytes | None, path: str) -> dict[tuple[int, str, int], SlotPlacement]:
    rows: dict[tuple[int, str, int], SlotPlacement] = {}
    if data is None:
        return rows
    text = data.decode("utf-8-sig")
    for line, row in enumerate(csv.DictReader(io.StringIO(text)), start=2):
        if not row.get("chrTypeId"):
            continue
        try:
            key = (int(row["chrTypeId"]), (row["slotName"] or "").strip().lower(), int(row["itemId"] or 0))
            scale = float(row.get("scale") or 1.0)
            if not np.isfinite(scale):
                raise ValueError("non-finite scale")
            rows[key] = SlotPlacement(
                (row["parentLinkBone"] or "").strip(),
                _floats(row.get("position"), (0.0, 0.0, 0.0)),
                _floats(row.get("rotation"), (0.0, 0.0, 0.0, 1.0)),
                scale, _floats(row.get("scaleVector"), (1.0, 1.0, 1.0)),
            )
        except (KeyError, ValueError) as error:
            raise ValueError(f"{path}:{line}: {error}") from error
    return rows


class SlotTable:
    """Resolve the unequipped socket and the separate adjustment for the displayed model."""

    def __init__(self, sockets: bytes | None, items: bytes | None):
        self.sockets = _read_placements(sockets, SLOT_LOCATION)
        self.items = _read_placements(items, SLOT_ITEM_LOCATION)

    def preview_mount(self, chr_type: int, slot: str, model_item_id: int) -> tuple[str, np.ndarray] | None:
        """Return the authored item/socket transform; the session replaces the child's motion root."""
        # kr-812 sub_9B8940 keys sockets by the equipped body item. An inventory card starts empty.
        socket = self.sockets.get((chr_type, slot.lower(), 0))
        if socket is None or socket.bone in ("", "0"):
            return None
        # kr-812 sub_58F0D0 and cn-717 sub_5CBAB0 apply the item transform before the socket.
        item = self.items.get((chr_type, "", model_item_id))
        adjustment = item.matrix() if item is not None else np.eye(4)
        return socket.bone, adjustment @ socket.matrix()


def accessory_screen_points(scene: Scene, camera: PreviewCamera) -> np.ndarray:
    """Project the accessory meshes in their posed positions for frame recovery."""
    pose = evaluate(scene.motion, scene.frame) if scene.motion else None
    points = []
    for part in scene.parts:
        own_pose = evaluate(part.motion, 0) if part.motion else None
        vertices = skin(part.mesh, own_pose, rigid=part.rigid).positions
        bone = pose.worlds.get(part.attach_bone.lower()) if pose and part.attach_bone else None
        if part.attach_matrix is not None:
            mount = part.attach_matrix @ bone if bone is not None else part.attach_matrix
            vertices = vertices @ mount[:3, :3] + mount[3, :3]
        points.append(camera.to_screen(vertices))
    return np.concatenate(points) if points else np.empty((0, 3))
