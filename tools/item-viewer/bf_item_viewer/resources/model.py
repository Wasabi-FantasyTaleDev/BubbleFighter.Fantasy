""".msh meshes and .mtn motions of the Lambda engine used by every client build.

Layouts were first established for cn-324 (MeshData::load cn-324 sub_900550, MotionData::load
cn-324 sub_8FE0B0, bone tree cn-324 sub_8EECB0). Each build's loader is checked separately and
recorded in research/; this module only parses, it never decides how a model is drawn.

Primitive encodings (cn-324 sub_8A35E0 u32, sub_8A3600 f32, sub_8A3620 f64, sub_8A3800 wstring =
u32 char count + UTF-16LE without terminator). Matrices are D3D row-major with row vectors.
"""

from __future__ import annotations

import math
import struct
from dataclasses import dataclass, field

import numpy as np

MAX_COUNT = 4_000_000


class ModelFormatError(ValueError):
    """A model file does not match the layout its loader reads."""


class _Reader:
    __slots__ = ("data", "pos")

    def __init__(self, data: bytes):
        self.data = data
        self.pos = 0

    def take(self, n: int) -> bytes:
        if n < 0 or self.pos + n > len(self.data):
            raise ModelFormatError(f"wants {n} bytes at {self.pos} of {len(self.data)}")
        b = self.data[self.pos:self.pos + n]
        self.pos += n
        return b

    def u32(self) -> int:
        return struct.unpack("<I", self.take(4))[0]

    def count(self, what: str) -> int:
        n = self.u32()
        if n > MAX_COUNT:
            raise ModelFormatError(f"{what} count {n} is implausible")
        return n

    def f64(self) -> float:
        return struct.unpack("<d", self.take(8))[0]

    def floats(self, n: int) -> np.ndarray:
        return np.frombuffer(self.take(4 * n), dtype="<f4").astype(np.float32)

    def mat4(self) -> np.ndarray:
        return self.floats(16).reshape(4, 4)

    def wstr(self) -> str:
        n = self.count("string")
        return self.take(2 * n).decode("utf-16-le", errors="replace")


@dataclass
class Material:
    """68-byte record = D3DMATERIAL9: diffuse, ambient, specular, emissive (RGBA) and power."""

    diffuse: tuple
    ambient: tuple
    specular: tuple
    emissive: tuple
    power: float
    texture: str | None


@dataclass
class SkinBone:
    name: str
    vertex_indices: np.ndarray
    weights: np.ndarray
    offset: np.ndarray          # inverse bind matrix, row-major


@dataclass
class Mesh:
    version: int
    root: np.ndarray            # applied to unskinned meshes only (cn-324 sub_900550 +60 copy)
    atlas_cols: int
    atlas_rows: int
    atlas_ms: int
    props: list
    alpha_test: bool
    positions: np.ndarray       # (n, 3)
    normals: np.ndarray         # (n, 3)
    uvs: np.ndarray             # (n, 2)
    indices: np.ndarray         # (m, 3) triangle list
    materials: list
    ranges: list                # (vertex_start, vertex_count, face_start, face_count)
    bones: list
    consumed: int
    size: int

    @property
    def skinned(self) -> bool:
        return bool(self.bones)


def parse_msh(data: bytes) -> Mesh:
    r = _Reader(data)
    version = r.u32()
    if version < 3:
        raise ModelFormatError(f"mesh version {version} < 3 is rejected by the loader")
    root = r.mat4()
    packed = r.u32()
    props = [(r.wstr(), r.u32()) for _ in range(r.count("property"))]
    alpha_test = r.u32() != 0
    r.take(28)
    vcount = r.count("vertex")
    icount = r.count("index")
    if icount % 3:
        raise ModelFormatError(f"index count {icount} is not a triangle list")
    vertices = np.frombuffer(r.take(32 * vcount), dtype="<f4").reshape(vcount, 8).astype(np.float32)
    indices = np.frombuffer(r.take(2 * icount), dtype="<u2").astype(np.int32).reshape(-1, 3)
    if vcount and indices.size and int(indices.max()) >= vcount:
        raise ModelFormatError("an index points past the vertex array")
    materials = []
    for _ in range(r.count("material")):
        m = np.frombuffer(r.take(68), dtype="<f4")
        texture = None
        if r.u32():
            texture = r.wstr()
        materials.append(Material(tuple(m[0:4]), tuple(m[4:8]), tuple(m[8:12]), tuple(m[12:16]), float(m[16]),
                                  texture))
    ranges = [struct.unpack("<4I", r.take(16)) for _ in range(r.count("range"))]
    bones = []
    for _ in range(r.count("bone")):
        name = r.wstr()
        n = r.count("influence")
        idx = np.frombuffer(r.take(4 * n), dtype="<u4").astype(np.int64)
        w = r.floats(n)
        bones.append(SkinBone(name, idx, w, r.mat4()))
    return Mesh(version, root, packed & 0xFF, (packed >> 8) & 0xFF, packed >> 16, props, alpha_test,
                vertices[:, 0:3].copy(), vertices[:, 3:6].copy(), vertices[:, 6:8].copy(), indices,
                materials, ranges, bones, r.pos, len(data))


@dataclass
class Bone:
    name: str
    matrix: np.ndarray          # local transform used when no track drives the bone
    parent: int = -1


@dataclass
class Track:
    name: str
    rot: np.ndarray             # (k, 5) time, x, y, z, w
    pos: np.ndarray             # (k, 4) time, x, y, z
    scale: np.ndarray           # (k, 4) time, x, y, z


@dataclass
class Motion:
    version: int
    name: str
    ticks_per_second: float
    duration: float
    frame_count: int
    bones: list
    tracks: dict = field(default_factory=dict)
    consumed: int = 0
    size: int = 0

    TICKS_PER_FRAME = 120.0     # cn-324 dbl_B315B0; 3600 / 120 = 30 frames per second


def _bone_tree(r: _Reader, count: int) -> list[Bone]:
    """cn-324 sub_8EECB0: depth-first nodes, each preceded by a link opcode.

    0 = no parent (this level returns after the subtree), 1 = child of the node just read,
    2 = sibling of the node just read, 3 = end of this level.
    """
    bones: list[Bone] = []

    def walk(parent: int, prev: int, depth: int) -> None:
        if depth > 512:
            raise ModelFormatError("bone tree too deep")
        while True:
            link = r.u32()
            if link == 3:
                return
            if link > 3:
                raise ModelFormatError(f"bone link opcode {link}")
            name = r.wstr()
            matrix = r.mat4()
            index = len(bones)
            if index >= count:
                raise ModelFormatError("bone tree longer than its count")
            bones.append(Bone(name, matrix))
            if link == 1:
                bones[index].parent = prev
                walk(prev, index, depth + 1)
                continue
            if link == 2:
                bones[index].parent = parent
                walk(parent, index, depth + 1)
                continue
            walk(parent, index, depth + 1)
            return

    walk(-1, -1, 0)
    if len(bones) != count:
        raise ModelFormatError(f"bone tree has {len(bones)} nodes, header says {count}")
    return bones


def parse_mtn(data: bytes) -> Motion:
    r = _Reader(data)
    version = r.u32()
    if version < 3:
        raise ModelFormatError(f"motion version {version} < 3 is rejected by the loader")
    name = r.wstr()
    tps = r.f64()
    duration = r.f64()
    if not (0 < tps < 1e6) or not (0 <= duration < 1e5):
        raise ModelFormatError("implausible motion timing")
    frames = int(math.ceil(tps * duration / Motion.TICKS_PER_FRAME)) + 1
    bones = _bone_tree(r, r.count("bone"))
    tracks = {}
    for _ in range(r.count("track")):
        tname = r.wstr()
        rot = r.floats(5 * r.count("rotation key")).reshape(-1, 5)
        pos = r.floats(4 * r.count("position key")).reshape(-1, 4)
        scale = r.floats(4 * r.count("scale key")).reshape(-1, 4)
        tracks[tname] = Track(tname, rot, pos, scale)
    return Motion(version, name, tps, duration, frames, bones, tracks, r.pos, len(data))
