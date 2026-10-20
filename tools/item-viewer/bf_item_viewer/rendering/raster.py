"""A deterministic software rasterizer for the preview's orthographic projection.

Why software: one code path renders grid thumbnails, the detail view and every export, so
they cannot drift apart, and the same input always produces the same bytes (no driver
differences). With an orthographic projection, screen-space interpolation of UVs is exact.

Draw rules reproduce the model draw (cn-324 sub_902A80, ModelPart::draw), whose kr-812 and kr-1256
equivalents are checked in research/:
* one draw per attribute range, with that range's material texture;
* dye blend: rgb = tex.rgb * a + tint * (1 - a), output alpha = material alpha (opaque);
* parts whose name starts with ``kEye`` or equals ``Gun``/``kLowerBR``/``Eyebrow`` use the texture
  alpha as opacity; ``Eyebrow`` is drawn last;
* back faces are culled with D3D's default (counter-clockwise on screen culled).
"""

from __future__ import annotations

from dataclasses import dataclass, field

import numpy as np

ALPHA_BLEND_NAMES = ("gun", "klowerbr", "eyebrow")
MAGENTA = np.array([1.0, 0.0, 1.0])


def is_alpha_blended(part_name: str) -> bool:
    n = part_name.lower()
    return n.startswith("keye") or n in ALPHA_BLEND_NAMES


def has_outline(part_name: str) -> bool:
    """Weapons (``Gun``) blend their texture alpha but still get the outline hull: every weapon in
    the kr-812 inventory grid is drawn with it. Eyes, eyebrows and kLowerBR stay without one."""
    return part_name.lower() == "gun" or not is_alpha_blended(part_name)


@dataclass
class Texture:
    """Straight-alpha RGBA float32 in [0, 1], origin top-left (D3D convention)."""

    rgba: np.ndarray
    name: str = ""

    @property
    def size(self) -> tuple[int, int]:
        return self.rgba.shape[1], self.rgba.shape[0]


@dataclass
class DrawItem:
    """One attribute range ready to rasterize."""

    screen: np.ndarray          # (n, 3) pixel x, y, depth
    uvs: np.ndarray             # (n, 2)
    shade: np.ndarray | None    # (n,) per-vertex light factor, None = unlit
    triangles: np.ndarray       # (m, 3)
    texture: Texture | None
    diffuse: tuple              # material diffuse RGBA, used when there is no texture
    tint: tuple                 # dye colour RGB in [0, 1]
    alpha_blend: bool
    colour_key: bool = True
    order: int = 0              # draws with a larger order are drawn later
    label: str = ""
    toon_u: np.ndarray | None = None      # (n,) toon ramp coordinate per vertex
    ramp: np.ndarray | None = None        # (k, 3) toon ramp colours, sampled with point filtering
    specular: np.ndarray | None = None    # (n, 3) additive specular per vertex
    cull: str = "back"                    # "back" (D3DCULL_CCW) or "front" (outline hull)
    flat: tuple | None = None             # flat RGBA colour (outline pass)


@dataclass
class Frame:
    width: int
    height: int
    color: np.ndarray = field(init=False)     # premultiplied RGBA float32
    depth: np.ndarray = field(init=False)
    stats: dict = field(default_factory=dict)

    def __post_init__(self):
        self.color = np.zeros((self.height, self.width, 4), dtype=np.float32)
        self.depth = np.full((self.height, self.width), np.inf, dtype=np.float32)


def _sample(texture: Texture, u: np.ndarray, v: np.ndarray) -> np.ndarray:
    """Bilinear sampling with wrap addressing (D3D defaults)."""
    tex = texture.rgba
    h, w = tex.shape[:2]
    x = u * w - 0.5
    y = v * h - 0.5
    x0 = np.floor(x)
    y0 = np.floor(y)
    fx = (x - x0)[:, None]
    fy = (y - y0)[:, None]
    x0 = x0.astype(np.int64) % w
    y0 = y0.astype(np.int64) % h
    x1 = (x0 + 1) % w
    y1 = (y0 + 1) % h
    top = tex[y0, x0] * (1 - fx) + tex[y0, x1] * fx
    bottom = tex[y1, x0] * (1 - fx) + tex[y1, x1] * fx
    return top * (1 - fy) + bottom * fy


def _nearest(texture: Texture, u: np.ndarray, v: np.ndarray) -> np.ndarray:
    tex = texture.rgba
    h, w = tex.shape[:2]
    return tex[np.floor(v * h).astype(np.int64) % h, np.floor(u * w).astype(np.int64) % w]


def draw(frame: Frame, item: DrawItem) -> None:
    s = item.screen
    tris = item.triangles
    if len(tris) == 0:
        return
    p0, p1, p2 = s[tris[:, 0]], s[tris[:, 1]], s[tris[:, 2]]
    area = (p1[:, 0] - p0[:, 0]) * (p2[:, 1] - p0[:, 1]) - (p2[:, 0] - p0[:, 0]) * (p1[:, 1] - p0[:, 1])
    # y points down on screen, so a positive area is clockwise: D3DCULL_CCW keeps it.
    keep = area > 1e-9 if item.cull == "back" else area < -1e-9
    frame.stats["culled"] = frame.stats.get("culled", 0) + int((~keep).sum())
    W, H = frame.width, frame.height
    tint = np.asarray(item.tint, dtype=np.float32)
    for t in np.nonzero(keep)[0]:
        i0, i1, i2 = tris[t]
        a, b, c = s[i0], s[i1], s[i2]
        xmin = max(int(np.floor(min(a[0], b[0], c[0]))), 0)
        xmax = min(int(np.ceil(max(a[0], b[0], c[0]))), W - 1)
        ymin = max(int(np.floor(min(a[1], b[1], c[1]))), 0)
        ymax = min(int(np.ceil(max(a[1], b[1], c[1]))), H - 1)
        if xmin > xmax or ymin > ymax:
            continue
        xs = np.arange(xmin, xmax + 1, dtype=np.float64) + 0.5
        ys = np.arange(ymin, ymax + 1, dtype=np.float64) + 0.5
        px, py = np.meshgrid(xs, ys)
        inv = 1.0 / area[t]
        w0 = ((b[0] - px) * (c[1] - py) - (c[0] - px) * (b[1] - py)) * inv
        w1 = ((c[0] - px) * (a[1] - py) - (a[0] - px) * (c[1] - py)) * inv
        w2 = 1.0 - w0 - w1
        # Fill rule: a pixel exactly on an edge belongs to one side only. Adjacent triangles of the same
        # winding traverse a shared edge in opposite directions, so the direction test picks exactly one.
        def owns(p, q):
            dx, dy = q[0] - p[0], q[1] - p[1]
            return dy > 0 or (dy == 0 and dx > 0)
        inside = ((w0 > 0) | ((w0 == 0) & owns(b, c))) & ((w1 > 0) | ((w1 == 0) & owns(c, a)))             & ((w2 > 0) | ((w2 == 0) & owns(a, b)))
        if not inside.any():
            continue
        w0, w1, w2 = w0[inside], w1[inside], w2[inside]
        z = w0 * a[2] + w1 * b[2] + w2 * c[2]
        yy = (py[inside] - 0.5).astype(np.int64)
        xx = (px[inside] - 0.5).astype(np.int64)
        current = frame.depth[yy, xx]
        visible = (z < current) & (z >= 0.0) & (z <= 1.0)
        if not visible.any():
            continue
        w0, w1, w2, z, yy, xx = w0[visible], w1[visible], w2[visible], z[visible], yy[visible], xx[visible]
        if item.flat is not None:
            frame.color[yy, xx] = np.asarray(item.flat, dtype=np.float32)
            frame.depth[yy, xx] = z
            continue
        if item.texture is not None:
            uv = item.uvs
            u = w0 * uv[i0, 0] + w1 * uv[i1, 0] + w2 * uv[i2, 0]
            v = w0 * uv[i0, 1] + w1 * uv[i1, 1] + w2 * uv[i2, 1]
            texel = _sample(item.texture, u, v)
            if item.colour_key:
                # Magenta texels are cut-outs (proven in the art, cn-324 research); test unfiltered texels.
                key = np.all(np.abs(_nearest(item.texture, u, v)[:, :3] - MAGENTA) < 1e-3, axis=1)
            else:
                key = np.zeros(len(u), dtype=bool)
            if item.alpha_blend:
                rgb = texel[:, :3]
                alpha = texel[:, 3] * float(item.diffuse[3])
            else:
                a_ = texel[:, 3:4]
                rgb = texel[:, :3] * a_ + tint[None, :] * (1.0 - a_)
                alpha = np.full(len(u), float(item.diffuse[3]), dtype=np.float32)
            alpha = np.where(key, 0.0, alpha)
        else:
            rgb = np.tile(np.asarray(item.diffuse[:3], dtype=np.float32), (len(z), 1))
            alpha = np.full(len(z), float(item.diffuse[3]), dtype=np.float32)
        if item.toon_u is not None and item.ramp is not None:
            u = w0 * item.toon_u[i0] + w1 * item.toon_u[i1] + w2 * item.toon_u[i2]
            k = len(item.ramp)
            rgb = rgb * item.ramp[np.clip(np.floor(u * k).astype(np.int64), 0, k - 1)]
        if item.specular is not None:
            sp = item.specular
            rgb = rgb + (w0[:, None] * sp[i0] + w1[:, None] * sp[i1] + w2[:, None] * sp[i2])
        if item.shade is not None:
            sh = w0 * item.shade[i0] + w1 * item.shade[i1] + w2 * item.shade[i2]
            rgb = rgb * sh[:, None]
        rgb = np.clip(rgb, 0.0, 1.0)
        alpha = np.clip(alpha, 0.0, 1.0)
        if item.alpha_blend:
            # Alpha-blended parts do not write depth for transparent texels.
            src = np.concatenate([rgb * alpha[:, None], alpha[:, None]], axis=1)
            dst = frame.color[yy, xx]
            frame.color[yy, xx] = src + dst * (1.0 - alpha[:, None])
            solid = alpha > 0.5
            frame.depth[yy[solid], xx[solid]] = z[solid]
        else:
            opaque = alpha > 0.0
            yy, xx, z, rgb, alpha = yy[opaque], xx[opaque], z[opaque], rgb[opaque], alpha[opaque]
            frame.color[yy, xx] = np.concatenate([rgb * alpha[:, None], alpha[:, None]], axis=1)
            frame.depth[yy, xx] = z


def resolve(frame: Frame, supersample: int) -> np.ndarray:
    """Box-filter a supersampled premultiplied frame down to straight-alpha RGBA uint8."""
    c = frame.color
    if supersample > 1:
        h, w = c.shape[0] // supersample, c.shape[1] // supersample
        c = c[:h * supersample, :w * supersample].reshape(h, supersample, w, supersample, 4).mean(axis=(1, 3))
    alpha = c[..., 3:4]
    rgb = np.where(alpha > 1e-6, c[..., :3] / np.maximum(alpha, 1e-6), 0.0)
    out = np.concatenate([rgb, alpha], axis=-1)
    return np.clip(np.round(out * 255.0), 0, 255).astype(np.uint8)
