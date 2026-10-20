"""Texture decoding and the client's texture search path."""

from __future__ import annotations

import io
import posixpath
from collections import OrderedDict

import numpy as np
from PIL import Image

from ..rendering.raster import Texture

MAX_TEXTURE_CACHE = 256


def decode_image(data: bytes, name: str = "") -> Texture:
    """Decode PNG/DDS/TGA/BMP/JPG with Pillow into straight-alpha float RGBA."""
    try:
        image = Image.open(io.BytesIO(data))
        image.load()
    except Exception as error:  # Pillow raises many types for damaged or unsupported files
        raise ValueError(f"{name}: cannot decode ({error})") from error
    rgba = np.asarray(image.convert("RGBA"), dtype=np.float32) / 255.0
    return Texture(rgba, name)


class TextureResolver:
    """Find a material's texture the way the client's TextureManager does.

    cn-324 sub_8D3110 tries ``dds/<name>.dds`` and the name itself, then the same under each
    registered search directory. For model parts the effective search order is the mesh's own
    folder, each ancestor folder, then the shared character texture folder; the per-build list
    of shared folders comes from the profile (``texture_pools``).
    """

    def __init__(self, index, shared_folders: list[str]):
        self.index = index
        self.shared = shared_folders
        self._cache: OrderedDict[str, Texture] = OrderedDict()

    def candidates(self, mesh_path: str, texture_name: str) -> list[str]:
        name = texture_name.replace("\\", "/").split("/")[-1]
        folder = posixpath.dirname(mesh_path)
        out = []
        while folder and folder.count("/") >= 1:
            out.append(f"{folder}/{name}")
            folder = posixpath.dirname(folder)
        out += [f"{pool.rstrip('/')}/{name}" for pool in self.shared]
        out.append(f"dds/{posixpath.splitext(name)[0]}.dds")
        return out

    def resolve(self, mesh_path: str, texture_name: str) -> tuple[Texture | None, str | None, list[str]]:
        tried = self.candidates(mesh_path, texture_name)
        for path in tried:
            key = path.lower()
            if key in self._cache:
                self._cache.move_to_end(key)
                return self._cache[key], path, tried
            if self.index.exists(path):
                texture = decode_image(self.index.read(path), path)
                self._cache[key] = texture
                while len(self._cache) > MAX_TEXTURE_CACHE:
                    self._cache.popitem(last=False)
                return texture, path, tried
        return None, None, tried
