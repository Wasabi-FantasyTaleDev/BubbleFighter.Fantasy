"""A cached, incremental index of every resource a client folder exposes.

The index maps each client path (case-insensitive, ``/`` separators) to the archive member
or loose file that provides it. It lives in a SQLite file under the tool's cache folder,
never in the client folder, and is rebuilt only for archives whose size or modification
time changed. Archive handles are opened on demand and kept in a bounded LRU.
"""

from __future__ import annotations

import hashlib
import sqlite3
import threading
from collections import OrderedDict
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass
from pathlib import Path
from typing import Callable, Iterable

from .base import ArchiveError, ArchiveReader, UnsafePathError, normalize_member
from .bfz import BfzArchive
from .discovery import ClientLayout, SourceFile, discover
from .pkn import PknArchive

SCHEMA_VERSION = 2
MAX_OPEN_ARCHIVES = 48
MAX_READ_CACHE_BYTES = 96 * 1024 * 1024

ProgressFn = Callable[[int, int, str], None]


@dataclass(frozen=True)
class Provenance:
    """Where a resource came from: enough to explain any preview."""

    client_path: str
    source_kind: str        # bfz / pkn / loose
    source_file: str        # client-relative archive or loose file
    member: str             # member path inside the archive ('' for loose files)
    size: int


@dataclass(frozen=True)
class ArchiveStatus:
    relative_path: str
    kind: str
    members: int
    error: str | None


def cache_dir_for(cache_root: Path, client_root: Path) -> Path:
    digest = hashlib.sha1(str(client_root.resolve()).lower().encode("utf-8")).hexdigest()[:16]
    return cache_root / f"{client_root.name}-{digest}"


def _open_archive(source: SourceFile) -> ArchiveReader:
    if source.kind == "bfz":
        return BfzArchive(source.file_path, source.relative_path)
    if source.kind == "pkn":
        return PknArchive(source.file_path, source.relative_path)
    raise ArchiveError(f"{source.relative_path}: unsupported archive kind {source.kind}")


def _list_members(source: SourceFile) -> tuple[list[tuple[str, int]], str | None]:
    try:
        with _open_archive(source) as archive:
            return [(m.path, m.size) for m in archive.members()], None
    except (ArchiveError, OSError, ValueError) as error:
        return [], str(error)


class ResourceIndex:
    """Lookup and read client resources by client path."""

    def __init__(self, client_root: Path | str, cache_root: Path | str):
        self.client_root = Path(client_root).resolve()
        cache_root = Path(cache_root).resolve()
        try:
            cache_root.relative_to(self.client_root)
            raise ValueError("the cache folder must not be inside the client folder")
        except ValueError as error:
            if "must not" in str(error):
                raise
        self.cache_dir = cache_dir_for(cache_root, self.client_root)
        self.cache_dir.mkdir(parents=True, exist_ok=True)
        self._db_path = self.cache_dir / "index.sqlite"
        self._lock = threading.RLock()
        self._db = sqlite3.connect(self._db_path, check_same_thread=False)
        self._init_schema()
        self._open: OrderedDict[int, ArchiveReader] = OrderedDict()
        self._sources: dict[int, SourceFile] = {}
        self._read_cache: OrderedDict[str, bytes] = OrderedDict()
        self._read_cache_bytes = 0
        self.layout: ClientLayout | None = None

    # -- schema -----------------------------------------------------------------------
    def _init_schema(self) -> None:
        with self._lock:
            cur = self._db.cursor()
            cur.execute("CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT)")
            row = cur.execute("SELECT value FROM meta WHERE key='schema'").fetchone()
            if row is None or int(row[0]) != SCHEMA_VERSION:
                cur.executescript("""
                    DROP TABLE IF EXISTS sources; DROP TABLE IF EXISTS members;
                    CREATE TABLE sources (id INTEGER PRIMARY KEY, kind TEXT, rel TEXT UNIQUE,
                        size INTEGER, mtime INTEGER, members INTEGER, error TEXT);
                    CREATE TABLE members (path_lower TEXT, path TEXT, source INTEGER, size INTEGER);
                    CREATE INDEX members_path ON members(path_lower);
                    CREATE INDEX members_source ON members(source);
                """)
                cur.execute("INSERT OR REPLACE INTO meta VALUES ('schema', ?)", (str(SCHEMA_VERSION),))
            self._db.commit()

    # -- build ------------------------------------------------------------------------
    def build(self, progress: ProgressFn | None = None, cancel: threading.Event | None = None,
              workers: int = 8) -> dict:
        """Scan the client folder and (re)index changed archives. Returns a summary."""
        layout = discover(self.client_root, cancel=cancel)
        self.layout = layout
        sources = layout.archives + layout.loose
        with self._lock:
            known = {rel: (sid, size, mtime) for sid, rel, size, mtime in
                     self._db.execute("SELECT id, rel, size, mtime FROM sources")}
        current = {s.relative_path for s in sources}
        stale = [rel for rel in known if rel not in current]
        todo = [s for s in layout.archives
                if s.relative_path not in known or known[s.relative_path][1:] != (s.size, s.mtime_ns)]
        with self._lock:
            for rel in stale + [s.relative_path for s in todo]:
                if rel in known:
                    self._db.execute("DELETE FROM members WHERE source=?", (known[rel][0],))
                    self._db.execute("DELETE FROM sources WHERE id=?", (known[rel][0],))
            self._db.commit()
        done = 0
        total = len(todo)
        with ThreadPoolExecutor(max_workers=workers) as pool:
            for start in range(0, total, 256):
                if cancel is not None and cancel.is_set():
                    raise InterruptedError("indexing cancelled")
                batch = todo[start:start + 256]
                for source, (members, error) in zip(batch, pool.map(_list_members, batch)):
                    self._store(source, members, error)
                    done += 1
                with self._lock:
                    self._db.commit()
                if progress:
                    progress(done, total, batch[-1].relative_path)
        self._store_loose(layout.loose)
        self._load_sources()
        return self.summary()

    def _store(self, source: SourceFile, members: Iterable[tuple[str, int]], error: str | None) -> None:
        rows = list(members)
        with self._lock:
            cur = self._db.execute(
                "INSERT INTO sources (kind, rel, size, mtime, members, error) VALUES (?,?,?,?,?,?)",
                (source.kind, source.relative_path, source.size, source.mtime_ns, len(rows), error))
            sid = cur.lastrowid
            self._db.executemany("INSERT INTO members VALUES (?,?,?,?)",
                                 [(p.lower(), p, sid, size) for p, size in rows])

    def _store_loose(self, loose: list[SourceFile]) -> None:
        with self._lock:
            known = {rel: (sid, size, mtime) for sid, rel, size, mtime in
                     self._db.execute("SELECT id, rel, size, mtime FROM sources WHERE kind='loose'")}
            for source in loose:
                old = known.get(source.relative_path)
                if old and old[1:] == (source.size, source.mtime_ns):
                    continue
                if old:
                    self._db.execute("DELETE FROM members WHERE source=?", (old[0],))
                    self._db.execute("DELETE FROM sources WHERE id=?", (old[0],))
                self._store(source, [(source.relative_path, source.size)], None)
            self._db.commit()

    def _load_sources(self) -> None:
        layout = self.layout or discover(self.client_root)
        by_rel = {s.relative_path: s for s in layout.archives + layout.loose}
        with self._lock:
            self._sources = {sid: by_rel[rel] for sid, rel in self._db.execute("SELECT id, rel FROM sources")
                             if rel in by_rel}

    def open_cached(self) -> bool:
        """Use an existing index without rescanning; returns False when none exists."""
        with self._lock:
            count = self._db.execute("SELECT COUNT(*) FROM sources").fetchone()[0]
        if not count:
            return False
        self.layout = discover(self.client_root)
        self._load_sources()
        return True

    # -- queries ----------------------------------------------------------------------
    def summary(self) -> dict:
        with self._lock:
            by_kind = {k: (n, m) for k, n, m in self._db.execute(
                "SELECT kind, COUNT(*), SUM(members) FROM sources GROUP BY kind")}
            errors = self._db.execute("SELECT COUNT(*) FROM sources WHERE error IS NOT NULL").fetchone()[0]
            members = self._db.execute("SELECT COUNT(*) FROM members").fetchone()[0]
        return {
            "client_root": str(self.client_root),
            "family": self.layout.family if self.layout else "unknown",
            "sources": {k: {"files": n, "members": m or 0} for k, (n, m) in by_kind.items()},
            "members": members,
            "archive_errors": errors,
        }

    def archive_statuses(self) -> list[ArchiveStatus]:
        with self._lock:
            return [ArchiveStatus(rel, kind, n, err) for rel, kind, n, err in self._db.execute(
                "SELECT rel, kind, members, error FROM sources WHERE kind != 'loose' ORDER BY rel")]

    def _locate(self, client_path: str) -> tuple[int, str, int] | None:
        key = normalize_member(client_path).lower()
        with self._lock:
            rows = self._db.execute(
                "SELECT m.source, m.path, m.size, s.kind FROM members m JOIN sources s ON s.id=m.source "
                "WHERE m.path_lower=?", (key,)).fetchall()
        if not rows:
            return None
        # Loose files are read before archives, the order the server's client-data layer uses.
        rows.sort(key=lambda r: (r[3] != "loose", r[0]))
        return rows[0][0], rows[0][1], rows[0][2]

    def exists(self, client_path: str) -> bool:
        try:
            return self._locate(client_path) is not None
        except UnsafePathError:
            return False

    def provenance(self, client_path: str) -> Provenance | None:
        hit = self._locate(client_path)
        if hit is None:
            return None
        sid, member, size = hit
        source = self._sources[sid]
        return Provenance(member, source.kind, source.relative_path,
                          "" if source.kind == "loose" else member, size)

    def list_prefix(self, prefix: str, suffix: str | None = None, limit: int = 200000) -> list[str]:
        """Client paths under ``prefix`` (case-insensitive), optionally filtered by extension."""
        key = normalize_member(prefix).lower().rstrip("/") + "/"
        sql = "SELECT DISTINCT path FROM members WHERE path_lower >= ? AND path_lower < ?"
        args: list = [key, key + "￿"]
        if suffix:
            sql += " AND path_lower LIKE ?"
            args.append("%" + suffix.lower())
        sql += " LIMIT ?"
        args.append(limit)
        with self._lock:
            return [r[0] for r in self._db.execute(sql, args)]

    def glob_suffix(self, suffix: str, limit: int = 500000) -> list[str]:
        with self._lock:
            return [r[0] for r in self._db.execute(
                "SELECT DISTINCT path FROM members WHERE path_lower LIKE ? LIMIT ?", ("%" + suffix.lower(), limit))]

    def read(self, client_path: str) -> bytes:
        key = normalize_member(client_path).lower()
        with self._lock:
            cached = self._read_cache.get(key)
            if cached is not None:
                self._read_cache.move_to_end(key)
                return cached
        hit = self._locate(client_path)
        if hit is None:
            raise FileNotFoundError(f"{client_path} is not in this client")
        sid, member, _ = hit
        source = self._sources.get(sid)
        if source is None:
            raise FileNotFoundError(f"{client_path}: its source changed; rescan the client")
        if source.kind == "loose":
            data = source.file_path.read_bytes()
        else:
            with self._lock:
                archive = self._open.get(sid)
                if archive is None:
                    archive = _open_archive(source)
                    self._open[sid] = archive
                    while len(self._open) > MAX_OPEN_ARCHIVES:
                        _, old = self._open.popitem(last=False)
                        old.close()
                else:
                    self._open.move_to_end(sid)
                data = archive.read(member)
        with self._lock:
            if len(data) <= MAX_READ_CACHE_BYTES // 8:
                self._read_cache[key] = data
                self._read_cache_bytes += len(data)
                while self._read_cache_bytes > MAX_READ_CACHE_BYTES:
                    _, old = self._read_cache.popitem(last=False)
                    self._read_cache_bytes -= len(old)
        return data

    def close(self) -> None:
        with self._lock:
            for archive in self._open.values():
                archive.close()
            self._open.clear()
            self._db.close()
