"""Socket attachment of slot items, from the client's own tables.

* ``Socket`` sheet of ``Config/ShopItem2.xml``: socket name (``kHeadBC``) -> parent bone (``Bip01_Head``).
* ``Models/SlotItem/SlotLocation.csv``: ``chrTypeId, slotName, itemId, parentLinkBone, position,
  rotation, scale, scaleVector`` per character type; ``itemId`` 0 is the slot's default placement.
  Both builds load it in their slot-item code (kr-812 sub_9B77A0; kr-1256 literal at VA 0x15BF630).

The attachment matrix is ``S(scale * scaleVector) * R(rotation) * T(position) * BoneWorld`` in the
row-vector convention used everywhere else (graded *inferred*: the column meanings are the table's
own names, the multiplication order follows D3DXMatrixTransformation).
"""

from __future__ import annotations

import csv
import io
from dataclasses import dataclass

import numpy as np

from .pose import quat_matrix, scaling, translation


@dataclass(frozen=True)
class SlotPlacement:
    chr_type: int
    slot: str
    item_id: int
    bone: str
    position: tuple
    rotation: tuple
    scale: float
    scale_vector: tuple
    source: str

    def matrix(self) -> np.ndarray:
        sv = self.scale_vector
        return (scaling(self.scale * sv[0], self.scale * sv[1], self.scale * sv[2])
                @ quat_matrix(self.rotation) @ translation(*self.position))


def _floats(text: str, n: int, default: tuple) -> tuple:
    parts = [p for p in (text or "").replace(" ", "").split(",") if p]
    if len(parts) != n:
        return default
    try:
        return tuple(float(p) for p in parts)
    except ValueError:
        return default


class SlotTable:
    """Placement lookup with the fallbacks the table itself provides (item row, then slot default)."""

    def __init__(self, data: bytes | None, sockets: dict[str, str]):
        self.sockets = {k.lower(): v for k, v in sockets.items()}
        self.rows: dict[tuple, SlotPlacement] = {}
        if data is None:
            return
        text = data.decode("utf-8-sig", errors="replace")
        for row in csv.DictReader(io.StringIO(text)):
            try:
                chr_type = int(row["chrTypeId"])
                item_id = int(row["itemId"] or 0)
            except (KeyError, ValueError):
                continue
            slot = (row.get("slotName") or "").strip()
            if not slot:
                continue
            placement = SlotPlacement(
                chr_type, slot, item_id, (row.get("parentLinkBone") or "").strip(),
                _floats(row.get("position"), 3, (0.0, 0.0, 0.0)), _floats(row.get("rotation"), 4, (0.0, 0.0, 0.0, 1.0)),
                float(row.get("scale") or 1.0), _floats(row.get("scaleVector"), 3, (1.0, 1.0, 1.0)), "SlotLocation.csv")
            self.rows[(chr_type, slot.lower(), item_id)] = placement

    def placement(self, chr_type: int, slot: str, item_id: int) -> SlotPlacement | None:
        key = slot.lower()
        for candidate in ((chr_type, key, item_id), (chr_type, key, 0)):
            if candidate in self.rows:
                return self.rows[candidate]
        bone = self.sockets.get(key)
        if bone:
            return SlotPlacement(chr_type, slot, item_id, bone, (0.0, 0.0, 0.0), (0.0, 0.0, 0.0, 1.0), 1.0,
                                 (1.0, 1.0, 1.0), "Socket sheet only (no SlotLocation row)")
        return None
