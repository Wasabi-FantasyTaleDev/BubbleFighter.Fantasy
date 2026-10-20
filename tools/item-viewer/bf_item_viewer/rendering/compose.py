"""Assemble the preview scene for a catalog item.

Rules and their grades (details and evidence in research/composition.md):
* weapon: ``Gun.msh`` drawn by its own root matrix, without ``Gun.mtn``. Graded *measured* against the
  kr-812 inventory grid: it matches every rifle, sniper, shotgun and hammer within 1% and also places the
  pistols whose ``Gun.mtn`` biped carries a large root offset (Pistol27, Pistol16), which the posed
  skin drops off the canvas. For the other weapons both rules give the same geometry.
* slot item: the item's own meshes posed by its own ``.mtn`` frame 0 (same reasoning, *inferred*).
* costume part / character: the wearer's ``Default`` parts restricted to the PartLocation sheet's
  part names (ShopItem2.xml; ``Body`` is not among them), with the item's parts replacing the slot of
  the same name, posed by a standing motion of the wearer (*inferred*; the client's preview motion
  is still being traced).
"""

from __future__ import annotations

import posixpath
from dataclasses import dataclass, field

import numpy as np

from ..catalog.model import CatalogItem, Kind
from .scene import Part, Scene, load_motion, load_part

DEFAULT_PART_NAMES = ("Head", "Face", "ArmL", "ArmR", "HandL", "HandR", "BodyU", "BodyD", "Legs", "Feet",
                      "Eyebrow")
# The basic/stand row of Models/Characters/AniList.xml sets EyePitchFollowBone=Bip01_Neck and
# IsEyeFollowCharacter=1: the client turns the head to the character's own facing, so the motion's
# head yaw (up to 65 degrees in some stand motions) never shows. Giving the head the upper spine's
# orientation reproduces that for Dao, Dizni, Bazzi, Uni, Mos, Kephi and Ethi (measured against kr-812
# inventory screenshots); Marid's head still tips forward, so the exact rule is open.
FACE_FORWARD = ("Bip01_Head", "Bip01_Spine2")
STAND_MOTIONS = ("pis-basic-stand.mtn", "lobby-stand.mtn", "basic-stand.mtn", "stand.mtn")


@dataclass
class Composition:
    """What was put in the scene and why; written into every export manifest."""

    kind: str
    wearer: str | None = None
    item_parts: list = field(default_factory=list)
    default_parts: list = field(default_factory=list)
    replaced_slots: list = field(default_factory=list)
    motion: str | None = None
    motion_rule: str = ""
    tint: tuple | None = None
    spray_item: int | None = None
    notes: list = field(default_factory=list)


class Composer:
    def __init__(self, index, part_names: tuple = DEFAULT_PART_NAMES):
        self.index = index
        self.part_names = {n.lower() for n in part_names}

    def wearer_options(self, item: CatalogItem) -> list[str]:
        if item.model is None:
            return []
        return list(item.model.wearers)

    def _stand_motion(self, character: str) -> tuple[str | None, str]:
        folder = f"Models/Characters/{character}/common"
        motions = {posixpath.basename(p).lower(): p for p in self.index.list_prefix(folder, ".mtn")}
        for name in STAND_MOTIONS:
            if name in motions:
                return motions[name], f"first of {STAND_MOTIONS} in {folder} (inferred)"
        stands = sorted(p for n, p in motions.items() if "stand" in n)
        if stands:
            return stands[0], f"first '*stand*' motion in {folder} (fallback)"
        return None, f"no motion in {folder}; bind pose"

    def _default_parts(self, character: str) -> list[str]:
        folder = f"Models/Characters/{character}/Default"
        meshes = [p for p in self.index.list_prefix(folder, ".msh") if p.count("/") == 4]
        return [p for p in meshes if posixpath.splitext(posixpath.basename(p))[0].lower() in self.part_names]

    def compose(self, item: CatalogItem, wearer: str | None = None, tint=(1.0, 1.0, 1.0)) -> tuple[Scene, Composition]:
        if item.model is None or not item.model.meshes:
            raise ValueError(f"item {item.item_id} has no model ({item.status.value})")
        model = item.model
        if item.kind == Kind.WEAPON:
            guns = [p for p in model.meshes if posixpath.basename(p).lower() == "gun.msh"] or model.meshes[:1]
            comp = Composition("weapon", item_parts=guns, motion=None,
                               motion_rule="Gun.msh by its root matrix, unposed (measured)")
            parts = [load_part(self.index, p, "weapon") for p in guns]
            for part in parts:
                part.rigid = True
            return Scene(parts, None, None), comp
        if item.kind == Kind.SLOT_ITEM:
            comp = Composition("slot item", item_parts=list(model.meshes), motion=model.motion,
                               motion_rule="the item's own .mtn, frame 0 (inferred)")
            parts = [load_part(self.index, p, "item") for p in model.meshes]
            return Scene(parts, model.motion, load_motion(self.index, model.motion) if model.motion else None), comp
        if item.kind in (Kind.COSTUME_PART, Kind.CHARACTER):
            choices = model.wearers or []
            chosen = wearer if wearer in choices else (choices[0] if choices else None)
            comp = Composition(item.kind.value, wearer=chosen)
            if wearer and wearer not in choices:
                comp.notes.append(f"wearer {wearer!r} has no version of this item; using {chosen!r}")
            if chosen is None:
                raise ValueError(f"item {item.item_id} has no wearer")
            if item.kind == Kind.CHARACTER:
                item_meshes = self._default_parts(chosen)
            else:
                prefix = "/".join(model.meshes[0].split("/")[:2])
                group = None
                item_meshes = []
                for path in self.index.list_prefix(f"{prefix}/{chosen}", ".msh"):
                    bits = path.split("/")
                    if len(bits) == 6 and bits[4] == str(model.borrowed_from or item.item_id):
                        item_meshes.append(path)
                        group = bits[3]
                comp.notes.append(f"item folder group {group}")
            defaults = self._default_parts(chosen)
            replaced = {posixpath.splitext(posixpath.basename(p))[0].lower() for p in item_meshes}
            kept = [p for p in defaults if posixpath.splitext(posixpath.basename(p))[0].lower() not in replaced]
            comp.item_parts = item_meshes
            comp.default_parts = kept if item.kind != Kind.CHARACTER else []
            comp.replaced_slots = sorted(replaced) if item.kind != Kind.CHARACTER else []
            motion, rule = self._stand_motion(chosen)
            comp.motion, comp.motion_rule = motion, rule
            comp.tint = tuple(tint)
            parts = ([load_part(self.index, p, "default", tint) for p in comp.default_parts]
                     + [load_part(self.index, p, "item", tint) for p in item_meshes])
            scene = Scene(parts, motion, load_motion(self.index, motion) if motion else None)
            scene.face_forward = (*FACE_FORWARD, np.eye(3))
            comp.notes.append(f"{FACE_FORWARD[0]} takes {FACE_FORWARD[1]}'s orientation (eye follows the character)")
            return scene, comp
        raise ValueError(f"item {item.item_id}: no composition rule for kind {item.kind.value}")
