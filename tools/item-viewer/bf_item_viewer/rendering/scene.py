"""A preview scene: posed model parts plus everything needed to explain the picture."""

from __future__ import annotations

import posixpath
from dataclasses import dataclass, field

import numpy as np

from ..resources.model import Mesh, Motion, parse_msh, parse_mtn
from ..resources.textures import TextureResolver
from .camera import PreviewCamera
from .pose import evaluate, skin
from .raster import DrawItem, Frame, draw, has_outline, is_alpha_blended, resolve

WHITE = (1.0, 1.0, 1.0)


@dataclass
class Part:
    slot: str                   # part name as the client registers it (msh basename)
    mesh_path: str
    mesh: Mesh
    origin: str                 # "item", "default", "weapon", ...
    tint: tuple = WHITE
    motion: Motion | None = None        # posed by its own motion instead of the scene's
    attach_bone: str | None = None      # attached to this bone of the scene pose ...
    attach_matrix: np.ndarray | None = None   # ... through this local placement
    rigid: bool = False                 # drawn by the mesh's root matrix, never skinned


@dataclass
class Scene:
    parts: list[Part]
    motion_path: str | None
    motion: Motion | None
    frame: int = 0
    warnings: list = field(default_factory=list)
    facts: dict = field(default_factory=dict)
    face_forward: tuple | None = None   # (bone, reference) for pose.evaluate

    @property
    def sources(self) -> list[str]:
        out = [p.mesh_path for p in self.parts]
        if self.motion_path:
            out.append(self.motion_path)
        return out


def load_part(index, path: str, origin: str, tint=WHITE) -> Part:
    slot = posixpath.splitext(posixpath.basename(path))[0]
    return Part(slot, path, parse_msh(index.read(path)), origin, tint)


def load_motion(index, path: str) -> Motion:
    return parse_mtn(index.read(path))


@dataclass
class RenderResult:
    rgba: np.ndarray            # (h, w, 4) uint8, straight alpha
    textures: dict              # part slot -> resolved texture path or None
    missing_textures: list
    missing_bones: list
    culled: int


def render(scene: Scene, camera: PreviewCamera, resolver: TextureResolver, supersample: int = 3,
           shading=None) -> RenderResult:
    """Rasterize ``scene`` through ``camera`` at the camera's size times ``supersample``."""
    ss = max(1, int(supersample))
    frame = Frame(camera.width * ss, camera.height * ss)
    pose = evaluate(scene.motion, scene.frame, scene.face_forward) if scene.motion else None
    own_poses: dict = {}
    inv_world = np.linalg.inv(camera.world)[:3, :3]
    light_model = view_model = None
    if shading is not None:
        light_model = shading.light_world @ inv_world
        light_model /= max(np.linalg.norm(light_model), 1e-12)
        view_model = np.array([0.0, 0.0, -1.0]) @ inv_world
        view_model /= max(np.linalg.norm(view_model), 1e-12)
    items: list[DrawItem] = []
    textures: dict = {}
    missing_textures: list = []
    missing_bones: set = set()
    for order, part in enumerate(scene.parts):
        part_pose = pose
        if part.motion is not None:
            key = id(part.motion)
            if key not in own_poses:
                own_poses[key] = evaluate(part.motion, 0)
            part_pose = own_poses[key]
        elif part.attach_bone is not None:
            part_pose = None
        geometry = skin(part.mesh, None if part.rigid else part_pose, rigid=part.rigid)
        missing_bones.update(geometry.missing_bones)
        positions = geometry.positions
        normals = geometry.normals
        if part.attach_bone is not None:
            bone = pose.worlds.get(part.attach_bone.lower()) if pose else None
            if bone is None:
                missing_bones.add(part.attach_bone)
                bone = np.eye(4)
            m = (part.attach_matrix if part.attach_matrix is not None else np.eye(4)) @ bone
            positions = np.concatenate([positions, np.ones((len(positions), 1))], axis=1) @ m
            positions = positions[:, :3]
            normals = normals @ m[:3, :3]
            normals = normals / np.maximum(np.linalg.norm(normals, axis=1, keepdims=True), 1e-12)
        screen = camera.to_screen(positions)
        toon_u = specular = None
        if shading is not None:
            if shading.toon:
                toon_u = (1.0 - normals @ (-light_model)) * 0.5
            # Weapons take no highlight: with it their brightest tenth reaches 232 against the kr-812
            # grid's 187, without it 191; characters match only with it.
            highlight = shading.highlight_for(part.tint) if shading.specular and part.origin != "weapon" else None
            if highlight is not None and highlight.brightness > 0:
                half = view_model - light_model
                half /= max(np.linalg.norm(half), 1e-12)
                term = np.maximum(normals @ half, 0.0) ** highlight.power
                specular = term[:, None] * (np.asarray(highlight.rgb, dtype=np.float64) / 255.0 * highlight.brightness)
            if shading.outline_colour is not None and has_outline(part.slot):
                hull = camera.to_screen(positions + normals * shading.outline_thickness)
                hull[:, :2] *= ss
                items.append(DrawItem(hull, part.mesh.uvs, None, part.mesh.indices, None, (0, 0, 0, 1), part.tint,
                                      False, order=-1, label=f"{part.slot}#outline", cull="front",
                                      flat=shading.outline_colour))
        screen[:, :2] *= ss
        blended = is_alpha_blended(part.slot)
        draw_order = 2 if part.slot.lower() == "eyebrow" else (1 if blended else 0)
        for index, (v0, vc, f0, fc) in enumerate(part.mesh.ranges):
            if index >= len(part.mesh.materials):
                break
            material = part.mesh.materials[index]
            texture = None
            if material.texture:
                texture, found, tried = resolver.resolve(part.mesh_path, material.texture)
                textures[f"{part.slot}#{index}"] = found
                if texture is None:
                    missing_textures.append({"part": part.mesh_path, "texture": material.texture,
                                             "tried": tried[:6]})
            items.append(DrawItem(screen, part.mesh.uvs, None, part.mesh.indices[f0:f0 + fc], texture,
                                  material.diffuse, part.tint, blended, order=draw_order * 1000 + order,
                                  label=f"{part.slot}#{index}",
                                  toon_u=toon_u, ramp=shading.ramp if shading is not None else None,
                                  specular=specular))
    culled = 0
    for item in sorted(items, key=lambda d: d.order):
        draw(frame, item)
    culled = frame.stats.get("culled", 0)
    return RenderResult(resolve(frame, ss), textures, missing_textures, sorted(missing_bones), culled)
