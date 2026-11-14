"""The checked-in map from build ids to local client folders (``data/builds.json``)."""

from __future__ import annotations

import json
import os
from dataclasses import dataclass
from pathlib import Path

from .rendering.camera import PreviewProfile, load_profile

CONFIG_PATH = Path(__file__).resolve().parent / "data" / "builds.json"


@dataclass(frozen=True)
class BuildSpec:
    id: str
    release: str
    folder: str
    base: str
    # The column of StringTable/TranslatedXmlTable.csv this client shows over its ShopItem2 texts; None when the
    # client shows the sheet's own text.
    text_column: str | None = None

    def profile(self) -> PreviewProfile:
        """The base build's preview constants under this build's own id."""
        return PreviewProfile(self.id, load_profile(self.base).raw)


@dataclass(frozen=True)
class BuildMap:
    games_dir: Path
    builds: list[BuildSpec]
    unmapped: list[dict]

    def root(self, spec: BuildSpec) -> Path:
        return self.games_dir / spec.folder

    def get(self, build_id: str) -> BuildSpec:
        for spec in self.builds:
            if spec.id == build_id:
                return spec
        raise KeyError(f"unknown build {build_id!r}; known: {', '.join(b.id for b in self.builds)}")


def load_build_map(games_dir: str | Path | None = None, path: Path = CONFIG_PATH) -> BuildMap:
    """Read the config; the games folder is the argument, ``BFIV_GAMES_DIR``, then the config value."""
    data = json.loads(path.read_text(encoding="utf-8"))
    base = games_dir or os.environ.get("BFIV_GAMES_DIR") or data.get("games_dir")
    if not base:
        raise ValueError("provide --games-dir or set BFIV_GAMES_DIR to the directory containing the clients")
    builds = [BuildSpec(b["id"], b["release"], b["folder"], b["base"], b.get("text_column")) for b in data["builds"]]
    if len({b.id for b in builds}) != len(builds):
        raise ValueError("builds.json repeats a build id")
    return BuildMap(Path(base).resolve(), builds, list(data.get("unmapped", [])))
