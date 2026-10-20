"""PKN packs of the modern clients.

Port of the layout the server archive library established from kr-1256 sub_1026520
(pack index) and kr-1256 sub_1024B70 / sub_1024D20 (members). Every key is derived from the
pack URI: the pack path relative to the client root, lower-cased, ``/`` separators, for
example ``packs/config_00000.pkn``.

Version-2 layout:
* at ``header_offset(uri)`` a 9-byte header encrypted with ``header_key``: ``u32 check,
  u8 version (2), u32 count`` with ``check == count + version``;
* at ``index_offset(uri)`` the index encrypted with ``index_key``: per member a
  length-prefixed UTF-16 path, ``u32 checksum, flags, block, size, stored_size`` and the
  16-byte MD5 of the unpacked member; ``checksum == flags+block+size+stored+sum(md5)``;
* member data starts at the index end rounded up to 1024 bytes; a member occupies whole
  1024-byte blocks.

Member flags: bit 1 zlib, bit 2 whole member encrypted, bit 4 only the first 1024 bytes
encrypted (``member_key``). Decryption precedes inflation and every member's MD5 is checked,
which makes a wrong key or a wrong layout impossible to miss.

The early layout (cn-279 0x61D8B0) keys its header with the case-sensitive URI and stores a
root path plus root-relative member names without a checksum word.
"""

from __future__ import annotations

import hashlib
import struct
import zlib
from dataclasses import dataclass
from pathlib import Path

from .base import ArchiveError, ArchiveReader, MemberInfo, normalize_member
from .snow2 import Snow2

VERSION = 2
BLOCK = 1024
FLAG_ZLIB = 1
FLAG_ENCRYPTED = 2
FLAG_HEAD_ENCRYPTED = 4
MAX_PATH_CHARS = 0x4000
MAX_ENTRIES = 1 << 20
MAX_MEMBER_BYTES = 512 * 1024 * 1024


def _code_unit_sum(uri: str) -> int:
    return sum(ord(c) for c in uri)


def header_offset(uri: str) -> int:
    return _code_unit_sum(uri) % 312 + 30


def index_offset(uri: str) -> int:
    return header_offset(uri) + 42 + (3 * _code_unit_sum(uri)) % 212


def header_key(uri: str) -> bytes:
    return bytes((i + (ord(uri[i % len(uri)]) & 0xFF)) & 0xFF for i in range(128))


def _sbyte(value: int) -> int:
    value &= 0xFF
    return value - 256 if value >= 128 else value


def index_key(uri: str) -> bytes:
    n = len(uri)
    return bytes((i + (i % 3 + 2) * _sbyte(ord(uri[n - 1 - i % n]))) & 0xFF for i in range(128))


def member_key(member_path: str, md5: bytes) -> bytes:
    n = len(member_path)
    out = bytearray(128)
    for i in range(128):
        multiplier = _sbyte(md5[i & 15] + i % 5 + 2)
        out[i] = (i + _sbyte(ord(member_path[i % n])) * multiplier) & 0xFF
    return bytes(out)


@dataclass(frozen=True)
class PknEntry:
    """One index record; ``path`` is exactly as stored because it keys the member cipher."""

    path: str
    flags: int
    block: int
    size: int
    stored_size: int
    md5: bytes


class _CipherReader:
    """Reads an encrypted region word by word, as the client's cipher stream does."""

    def __init__(self, handle, offset: int, key: bytes, limit: int):
        self._handle = handle
        self._cipher = Snow2(key)
        self._start = offset
        self._limit = limit
        self._buffer = bytearray()
        self._buffer_pos = 0
        self._next_file = offset
        self.position = offset

    def _fill(self, need: int) -> None:
        while len(self._buffer) - self._buffer_pos < need:
            if self._next_file - self._start > self._limit:
                raise ArchiveError("PKN index exceeds the decoded byte limit")
            self._handle.seek(self._next_file)
            chunk = self._handle.read(4096)
            whole = len(chunk) // 4 * 4
            if whole == 0:
                raise ArchiveError("PKN index runs past the end of the file")
            self._next_file += whole
            del self._buffer[:self._buffer_pos]
            self._buffer_pos = 0
            self._buffer += self._cipher.decrypt(chunk[:whole])

    def read(self, count: int) -> bytes:
        self._fill(count)
        data = bytes(self._buffer[self._buffer_pos:self._buffer_pos + count])
        self._buffer_pos += count
        self.position += count
        return data

    def u32(self) -> int:
        return struct.unpack("<I", self.read(4))[0]

    def wstring(self) -> str:
        length = self.u32()
        if length > MAX_PATH_CHARS:
            raise ArchiveError(f"PKN path of {length} characters; the pack URI is probably wrong")
        return self.read(length * 2).decode("utf-16-le")


class PknArchive(ArchiveReader):
    """Reads a PKN pack (version 2, or the early layout as a fallback)."""

    kind = "pkn"

    def __init__(self, file_path: Path, uri: str):
        self.file_path = Path(file_path)
        self.relative_path = uri
        self._handle = open(self.file_path, "rb")
        self._entries: dict[str, PknEntry] = {}
        self._order: list[PknEntry] = []
        try:
            try:
                self.layout = "v2"
                self._read_v2(uri.lower())
            except ArchiveError as modern:
                self._entries.clear()
                self._order.clear()
                try:
                    self.layout = "early"
                    self._read_early(uri)
                except ArchiveError as early:
                    raise ArchiveError(
                        f"{uri}: decodes neither as a version-2 pack ({modern}) nor as an early pack "
                        f"({early}); its path relative to the client root must be the client's own") from early
        except BaseException:
            self._handle.close()
            raise

    def _add(self, entry: PknEntry) -> None:
        if entry.size > MAX_MEMBER_BYTES or entry.stored_size > MAX_MEMBER_BYTES:
            raise ArchiveError(f"member {entry.path!r} exceeds the member byte limit")
        key = normalize_member(entry.path).lower()
        self._entries[key] = entry
        self._order.append(entry)

    def _read_v2(self, uri: str) -> None:
        self.relative_path = uri
        self._handle.seek(header_offset(uri))
        raw = self._handle.read(12)
        if len(raw) < 12:
            raise ArchiveError("file too short for a pack header")
        header = Snow2(header_key(uri)).decrypt(raw)
        check, version, count = struct.unpack_from("<IBI", header)
        if version != VERSION or check != (count + version) & 0xFFFFFFFF or count > MAX_ENTRIES:
            raise ArchiveError("header check failed")
        reader = _CipherReader(self._handle, index_offset(uri), index_key(uri), 256 * 1024 * 1024)
        for i in range(count):
            path = reader.wstring()
            checksum, flags, block, size, stored = struct.unpack("<5I", reader.read(20))
            md5 = reader.read(16)
            if checksum != (flags + block + size + stored + sum(md5)) & 0xFFFFFFFF:
                raise ArchiveError(f"index entry {i} ({path!r}) fails its checksum")
            self._add(PknEntry(path, flags, block, size, stored, md5))
        self._data_offset = (reader.position + BLOCK - 1) // BLOCK * BLOCK

    def _read_early(self, uri: str) -> None:
        self.relative_path = uri
        header = _CipherReader(self._handle, header_offset(uri), header_key(uri), 1 << 20)
        header.u32()
        header.u32()
        gap = header.u32()
        count = header.u32()
        if count > MAX_ENTRIES:
            raise ArchiveError(f"{count} entries; check the case-sensitive URI")
        root = header.wstring()
        reader = _CipherReader(self._handle, header.position + gap, index_key(uri), 64 * 1024 * 1024)
        for _ in range(count):
            relative = reader.wstring()
            flags, block, size, stored = struct.unpack("<4I", reader.read(16))
            md5 = reader.read(16)
            if flags & ~7:
                raise ArchiveError("unsupported member flags")
            self._add(PknEntry(f"{root}/{relative}" if root else relative, flags, block, size, stored, md5))
        self._data_offset = (reader.position + BLOCK - 1) // BLOCK * BLOCK

    def entries(self) -> list[PknEntry]:
        return list(self._order)

    def members(self) -> list[MemberInfo]:
        return [MemberInfo(normalize_member(e.path), e.size) for e in self._order]

    def contains(self, member: str) -> bool:
        return normalize_member(member).lower() in self._entries

    def read(self, member: str) -> bytes:
        entry = self._entries.get(normalize_member(member).lower())
        if entry is None:
            raise ArchiveError(f"{self.relative_path}: no member {member!r}")
        return self.read_entry(entry)

    def read_entry(self, entry: PknEntry) -> bytes:
        padded = (entry.stored_size + 3) & ~3
        self._handle.seek(self._data_offset + entry.block * BLOCK)
        payload = self._handle.read(padded)
        if len(payload) < padded:
            raise ArchiveError(f"{self.relative_path}: member {entry.path!r} runs past the end of the pack")
        if entry.flags & FLAG_HEAD_ENCRYPTED:
            head = Snow2(member_key(entry.path, entry.md5)).decrypt(payload[:min(BLOCK, padded)])
            payload = bytes(head) + payload[len(head):]
        if entry.flags & FLAG_ENCRYPTED:
            payload = bytes(Snow2(member_key(entry.path, entry.md5)).decrypt(payload))
        if entry.flags & FLAG_ZLIB:
            try:
                data = zlib.decompress(payload[:entry.stored_size], bufsize=max(entry.size, 1))
            except zlib.error as error:
                raise ArchiveError(f"{self.relative_path}: member {entry.path!r} does not inflate ({error})") from error
        else:
            data = payload[:entry.size]
        if len(data) != entry.size or hashlib.md5(data).digest() != entry.md5:
            raise ArchiveError(f"{self.relative_path}: member {entry.path!r} fails its MD5 check")
        return data

    def close(self) -> None:
        self._handle.close()
