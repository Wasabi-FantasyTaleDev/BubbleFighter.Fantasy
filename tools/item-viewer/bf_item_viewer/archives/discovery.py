"""Find a client's archives and loose resource files without leaving the client root."""

from __future__ import annotations

import os
from dataclasses import dataclass
from pathlib import Path

# Version-2 packs sit directly in Packs/ (kr-1256 sub_1026520 is handed packs/<name>.pkn);
# early packs in Pack/. Sub-folders such as Packs/old are backups the client never mounts.
PACK_FOLDERS = ("packs", "pack")

# Loose files that can hold renderable or catalog data. Everything else (executables,
# logs, dumps, videos) is ignored so the index stays small.
LOOSE_EXTENSIONS = frozenset({
    ".msh", ".mtn", ".png", ".dds", ".tga", ".bmp", ".jpg", ".jpeg", ".xml", ".ui", ".ini",
    ".txt", ".csv", ".xls", ".xlsx", ".face", ".x", ".efx2", ".lua", ".tbl", ".dat", ".bin",
})
SKIP_FOLDERS = frozenset({"log", "dump", "blackcipher", "hshield", "xigncode", "__pycache__",
                          "magicmida", "download", "voicechat"})


@dataclass(frozen=True)
class SourceFile:
    """An archive or loose file found under the client root."""

    kind: str               # "bfz", "pkn" or "loose"
    file_path: Path
    relative_path: str      # client-relative, "/" separators, original case
    size: int
    mtime_ns: int


@dataclass(frozen=True)
class ClientLayout:
    """What a client folder contains, decided from the files alone."""

    root: Path
    archives: list[SourceFile]
    loose: list[SourceFile]

    @property
    def has_bfz(self) -> bool:
        return any(a.kind == "bfz" for a in self.archives)

    @property
    def has_pkn(self) -> bool:
        return any(a.kind == "pkn" for a in self.archives)

    @property
    def family(self) -> str:
        if self.has_pkn and self.has_bfz:
            return "mixed"
        return "pkn" if self.has_pkn else "bfz" if self.has_bfz else "none"


def _inside(root: Path, candidate: Path) -> bool:
    try:
        candidate.resolve().relative_to(root)
        return True
    except ValueError:
        return False


def discover(client_root: Path | str, include_loose: bool = True, cancel=None) -> ClientLayout:
    """Walk ``client_root`` and classify its archives; symlinks leaving the root are skipped."""
    root = Path(client_root).resolve()
    if not root.is_dir():
        raise FileNotFoundError(f"{root} is not a folder")
    archives: list[SourceFile] = []
    loose: list[SourceFile] = []
    stack = [root]
    while stack:
        if cancel is not None and cancel.is_set():
            raise InterruptedError("scan cancelled")
        folder = stack.pop()
        try:
            entries = list(os.scandir(folder))
        except OSError:
            continue
        for entry in entries:
            path = Path(entry.path)
            if entry.is_symlink() and not _inside(root, path):
                continue
            if entry.is_dir(follow_symlinks=False):
                name = entry.name.lower()
                if name in SKIP_FOLDERS or name.endswith("_export_for_ai") or name.startswith("."):
                    continue
                # Only the pack folders' own level is mounted.
                if folder != root and folder.name.lower() in PACK_FOLDERS:
                    continue
                stack.append(path)
                continue
            if not entry.is_file(follow_symlinks=False):
                continue
            ext = os.path.splitext(entry.name)[1].lower()
            rel = path.relative_to(root).as_posix()
            stat = entry.stat(follow_symlinks=False)
            if ext == ".bfz":
                archives.append(SourceFile("bfz", path, rel, stat.st_size, stat.st_mtime_ns))
            elif ext == ".pkn":
                if folder.parent == root and folder.name.lower() in PACK_FOLDERS:
                    archives.append(SourceFile("pkn", path, rel, stat.st_size, stat.st_mtime_ns))
            elif include_loose and ext in LOOSE_EXTENSIONS and folder != root:
                loose.append(SourceFile("loose", path, rel, stat.st_size, stat.st_mtime_ns))
    archives.sort(key=lambda a: a.relative_path.lower())
    loose.sort(key=lambda a: a.relative_path.lower())
    return ClientLayout(root, archives, loose)
