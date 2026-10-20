"""Format-independent archive types and member-path safety."""

from __future__ import annotations

import abc
from dataclasses import dataclass

MAX_MEMBER_PATH = 1024


class ArchiveError(Exception):
    """An archive cannot be opened, or one of its members cannot be read or verified."""


class UnsafePathError(ArchiveError):
    """A member path would escape its virtual root (absolute, drive-qualified or containing ``..``)."""


@dataclass(frozen=True)
class MemberInfo:
    """One member of an archive: its path with ``/`` separators and its unpacked size."""

    path: str
    size: int


def normalize_member(path: str) -> str:
    """Normalize a stored member path to ``/`` separators and reject unsafe forms.

    Archive member names are untrusted input: a name such as ``../../x`` or ``C:/x`` must
    never become a file-system path, so it is refused here, before anything is indexed.
    """
    text = path.replace("\\", "/")
    while text.startswith("./"):
        text = text[2:]
    if not text or len(text) > MAX_MEMBER_PATH:
        raise UnsafePathError(f"member path {path!r} is empty or too long")
    if text.startswith("/") or (len(text) > 1 and text[1] == ":") or "\x00" in text:
        raise UnsafePathError(f"member path {path!r} is absolute")
    if any(part == ".." for part in text.split("/")):
        raise UnsafePathError(f"member path {path!r} leaves its root")
    return text


class ArchiveReader(abc.ABC):
    """A read-only view over one archive file."""

    kind: str = "archive"
    relative_path: str

    @abc.abstractmethod
    def members(self) -> list[MemberInfo]:
        """Return every file member in stored order."""

    @abc.abstractmethod
    def contains(self, member: str) -> bool:
        """Return whether a member exists (case-insensitive, either separator)."""

    @abc.abstractmethod
    def read(self, member: str) -> bytes:
        """Read, decrypt, unpack and verify one member."""

    def close(self) -> None:
        """Release the file handle."""

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        self.close()
