"""Small persistent settings: the recent client folders."""

from __future__ import annotations

import json
import os
from pathlib import Path

MAX_RECENT = 10


def config_path() -> Path:
    base = os.environ.get("LOCALAPPDATA") or str(Path.home() / ".config")
    return Path(base) / "BFItemViewer" / "config.json"


def load_config() -> dict:
    try:
        data = json.loads(config_path().read_text(encoding="utf-8"))
        return data if isinstance(data, dict) else {}
    except (OSError, ValueError):
        return {}


def save_config(data: dict) -> None:
    path = config_path()
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        tmp = path.with_suffix(".tmp")
        tmp.write_text(json.dumps(data, indent=2, ensure_ascii=False), encoding="utf-8")
        tmp.replace(path)
    except OSError:
        pass    # a read-only profile folder must not break the viewer


def recent_folders() -> list[str]:
    value = load_config().get("recent_folders", [])
    return [str(v) for v in value if isinstance(v, str)][:MAX_RECENT]


def add_recent(folder: str | Path) -> list[str]:
    folder = str(folder)
    config = load_config()
    items = [folder] + [r for r in recent_folders() if r.lower() != folder.lower()]
    config["recent_folders"] = items[:MAX_RECENT]
    save_config(config)
    return config["recent_folders"]


def forget_recent(folder: str) -> list[str]:
    config = load_config()
    config["recent_folders"] = [r for r in recent_folders() if r.lower() != folder.lower()]
    save_config(config)
    return config["recent_folders"]
