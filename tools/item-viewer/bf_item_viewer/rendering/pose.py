"""Skeleton evaluation and skinning for .msh/.mtn models.

Conventions (cn-324 MotionData::load, re-checked in kr-812 and kr-1256 by parsing their assets):
D3D row-major matrices with row vectors, a child's world matrix is ``local @ parent_world``, a
frame's local transform is ``S @ R @ T`` (D3DXMatrixTransformation with no centres), key times
are ticks at 3600 per second and frames advance by 120 ticks. Bones that no track drives keep
their hierarchy node matrix. Skinning is ``v' = sum(w_i * v @ offset_i @ world_i)``.
"""

from __future__ import annotations

from dataclasses import dataclass

import numpy as np

from ..resources.model import Mesh, Motion


def quat_matrix(q) -> np.ndarray:
    """D3DXMatrixRotationQuaternion for (x, y, z, w), row-vector convention."""
    x, y, z, w = (float(v) for v in q)
    n = x * x + y * y + z * z + w * w
    if n < 1e-12:
        return np.eye(4, dtype=np.float64)
    s = 2.0 / n
    xx, yy, zz = x * x * s, y * y * s, z * z * s
    xy, xz, yz = x * y * s, x * z * s, y * z * s
    wx, wy, wz = w * x * s, w * y * s, w * z * s
    return np.array([
        [1 - (yy + zz), xy + wz, xz - wy, 0],
        [xy - wz, 1 - (xx + zz), yz + wx, 0],
        [xz + wy, yz - wx, 1 - (xx + yy), 0],
        [0, 0, 0, 1]], dtype=np.float64)


def translation(x: float, y: float, z: float) -> np.ndarray:
    m = np.eye(4, dtype=np.float64)
    m[3, :3] = (x, y, z)
    return m


def scaling(x: float, y: float, z: float) -> np.ndarray:
    return np.diag([x, y, z, 1.0]).astype(np.float64)


def yaw_pitch_roll(yaw: float, pitch: float, roll: float) -> np.ndarray:
    """D3DXMatrixRotationYawPitchRoll: roll about Z, then pitch about X, then yaw about Y."""
    cy, sy = np.cos(yaw), np.sin(yaw)
    cp, sp = np.cos(pitch), np.sin(pitch)
    cr, sr = np.cos(roll), np.sin(roll)
    rz = np.array([[cr, sr, 0, 0], [-sr, cr, 0, 0], [0, 0, 1, 0], [0, 0, 0, 1]])
    rx = np.array([[1, 0, 0, 0], [0, cp, sp, 0], [0, -sp, cp, 0], [0, 0, 0, 1]])
    ry = np.array([[cy, 0, -sy, 0], [0, 1, 0, 0], [sy, 0, cy, 0], [0, 0, 0, 1]])
    return rz @ rx @ ry


def _sample_linear(keys: np.ndarray, t: float) -> np.ndarray | None:
    if keys.size == 0:
        return None
    times = keys[:, 0]
    if t <= times[0]:
        return keys[0, 1:].astype(np.float64)
    if t >= times[-1]:
        return keys[-1, 1:].astype(np.float64)
    i = int(np.searchsorted(times, t, side="right")) - 1
    a, b = keys[i], keys[i + 1]
    f = (t - a[0]) / max(b[0] - a[0], 1e-9)
    return a[1:] + (b[1:] - a[1:]) * f


def _slerp(q0: np.ndarray, q1: np.ndarray, f: float) -> np.ndarray:
    d = float(np.dot(q0, q1))
    if d < 0:
        q1, d = -q1, -d
    if d > 0.9995:
        q = q0 + (q1 - q0) * f
        return q / np.linalg.norm(q)
    theta = np.arccos(min(d, 1.0))
    s = np.sin(theta)
    return (np.sin((1 - f) * theta) * q0 + np.sin(f * theta) * q1) / s


def _sample_rotation(keys: np.ndarray, t: float) -> np.ndarray | None:
    if keys.size == 0:
        return None
    times = keys[:, 0]
    if t <= times[0]:
        return keys[0, 1:].astype(np.float64)
    if t >= times[-1]:
        return keys[-1, 1:].astype(np.float64)
    i = int(np.searchsorted(times, t, side="right")) - 1
    a, b = keys[i], keys[i + 1]
    f = (t - a[0]) / max(b[0] - a[0], 1e-9)
    return _slerp(a[1:].astype(np.float64), b[1:].astype(np.float64), f)


@dataclass
class Pose:
    """World matrices per bone name for one frame of one motion."""

    worlds: dict
    motion_name: str
    frame: int


def evaluate(motion: Motion, frame: int = 0, face_forward: tuple | None = None) -> Pose:
    """Bone worlds at ``frame``. ``face_forward = (bone, reference, rel)`` turns ``bone`` (and so its
    children) about its own origin to orientation ``rel`` relative to the reference bone (identity:
    the reference's own orientation), so the head looks where the body faces."""
    t = frame * Motion.TICKS_PER_FRAME
    turn = (face_forward[0].lower(), face_forward[1].lower(), face_forward[2]) if face_forward else None
    worlds: list[np.ndarray] = []
    by_name: dict[str, np.ndarray] = {}
    for bone in motion.bones:
        track = motion.tracks.get(bone.name)
        local = bone.matrix.astype(np.float64)
        if track is not None:
            rot = _sample_rotation(track.rot, t)
            pos = _sample_linear(track.pos, t)
            scl = _sample_linear(track.scale, t)
            if rot is not None or pos is not None or scl is not None:
                m = scaling(*(scl if scl is not None else (1, 1, 1)))
                m = m @ (quat_matrix(rot) if rot is not None else np.eye(4))
                if pos is not None:
                    m = m @ translation(*pos)
                else:
                    m[3, :3] = local[3, :3]
                local = m
        world = local if bone.parent < 0 else local @ worlds[bone.parent]
        if turn and bone.name.lower() == turn[0] and turn[1] in by_name:
            # rel is a row-vector 3x3 rotation applied in the reference bone's frame.
            ref = by_name[turn[1]][:3, :3]
            ref = ref / np.linalg.norm(ref, axis=1, keepdims=True)
            scale = np.linalg.norm(world[:3, :3], axis=1, keepdims=True)
            world = world.copy()
            world[:3, :3] = (turn[2] @ ref) * scale
        worlds.append(world)
        by_name[bone.name.lower()] = world
    return Pose(by_name, motion.name, frame)


@dataclass
class SkinnedGeometry:
    positions: np.ndarray   # (n, 3) float64, model space after skinning / root transform
    normals: np.ndarray     # (n, 3)
    missing_bones: list


def skin(mesh: Mesh, pose: Pose | None, rigid: bool = False) -> SkinnedGeometry:
    """Pose a mesh. Unskinned meshes get their root matrix (cn-324 sub_900550 keeps a root-applied copy
    for them); skinned meshes are expanded from the untransformed vertices. ``rigid`` applies the root
    matrix to a skinned mesh too."""
    pos4 = np.concatenate([mesh.positions.astype(np.float64), np.ones((len(mesh.positions), 1))], axis=1)
    nrm = mesh.normals.astype(np.float64)
    if rigid or not mesh.skinned:
        out = pos4 @ mesh.root.astype(np.float64)
        n = nrm @ mesh.root[:3, :3].astype(np.float64)
        return SkinnedGeometry(out[:, :3], _normalize(n), [])
    acc = np.zeros((len(pos4), 4))
    nacc = np.zeros((len(pos4), 3))
    wsum = np.zeros(len(pos4))
    missing = []
    for bone in mesh.bones:
        world = pose.worlds.get(bone.name.lower()) if pose else None
        if world is None:
            # No driving bone: hold the bind pose (offset is the inverse bind matrix).
            missing.append(bone.name)
            m = np.eye(4)
        else:
            m = bone.offset.astype(np.float64) @ world
        idx = bone.vertex_indices
        w = bone.weights.astype(np.float64)
        valid = idx < len(pos4)
        idx, w = idx[valid], w[valid]
        acc[idx] += (pos4[idx] @ m) * w[:, None]
        nacc[idx] += (nrm[idx] @ m[:3, :3]) * w[:, None]
        wsum[idx] += w
    unweighted = wsum < 1e-6
    acc[unweighted] = pos4[unweighted]
    nacc[unweighted] = nrm[unweighted]
    return SkinnedGeometry(acc[:, :3], _normalize(nacc), missing)


def _normalize(v: np.ndarray) -> np.ndarray:
    n = np.linalg.norm(v, axis=1, keepdims=True)
    n[n < 1e-12] = 1.0
    return v / n
