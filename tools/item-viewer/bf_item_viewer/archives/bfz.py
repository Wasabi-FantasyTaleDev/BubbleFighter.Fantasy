"""BFZ archives of the legacy clients.

A BFZ archive is an ordinary zip file whose members are encrypted with the traditional
PKWARE cipher. The password is derived from the archive's path relative to the client
root (upper-cased, ``/`` separators) through six 64-character tables. The derivation and
tables were established for the 2014 client (id-400 sub_9C8710, tables off_DDBA34) and
the server's archive library verifies them against kr-812 and cn-324 archives; this module
re-verifies every member it reads through the cipher check byte and the zip CRC-32.

Member names without the zip UTF-8 flag are stored in CP949; kr-812 sub_A4F4D0 compares
the raw bytes with the requested ANSI path through ``lstrcmpiA``.
"""

from __future__ import annotations

import os
import zipfile
from dataclasses import dataclass
from pathlib import Path

from .base import ArchiveError, ArchiveReader, MemberInfo, normalize_member

_TABLES = (
    "as89f7d6af98e7f6a9s87f6as98f76asdf8yasuefyae8f7as6ef9ase87fa6se9",
    "alkjdfLKDFJeofias4894ewfadfklDFJfsafjlasff89a4fhJLKDSfHdf98daf9s",
    "afoiefajsfiasdf87ysda987dsfvhsdkvhs8dr7vhsdkfghsie4g8hsdfukzDFDf",
    "faklsjdfiod9f8asdfjKLDFjds9f8jasdklfjsdf9asd8fuseijkasdkHDFKJDf8",
    "dflkjasdf98asfuyaiufhaos8efhLHFDJSFhasod8fsdfkLDfd9f8aslkejfDLkf",
    "dkfjapsdoifjas9d8fajsdfiashdJKFSHdfdfaskdljf9sd8fasd89f7asieufa9",
)

NAME_ENCODING = "cp949"


def password(client_relative_path: str) -> bytes:
    """Return the 64-byte password of the archive at ``client_relative_path``."""
    if not client_relative_path:
        raise ValueError("an archive path is required")
    path = client_relative_path.replace("\\", "/").upper().encode("latin-1", errors="replace")
    length = len(_TABLES[0])
    return bytes(ord(_TABLES[i % 6][path[i % len(path)] % length]) for i in range(length))


@dataclass(frozen=True)
class _Member:
    name: str
    info: zipfile.ZipInfo


class BfzArchive(ArchiveReader):
    """Reads one BFZ archive in place; members are decrypted and CRC-checked on demand."""

    kind = "bfz"

    def __init__(self, file_path: Path, client_relative_path: str):
        self.file_path = Path(file_path)
        self.relative_path = client_relative_path.replace("\\", "/")
        self._password = password(self.relative_path)
        try:
            self._zip = zipfile.ZipFile(self.file_path, metadata_encoding=NAME_ENCODING)
        except (zipfile.BadZipFile, OSError) as error:
            raise ArchiveError(f"{self.relative_path}: not a readable zip archive ({error})") from error
        self._members: dict[str, _Member] = {}
        for info in self._zip.infolist():
            if info.is_dir():
                continue
            name = normalize_member(info.filename)
            self._members[name.lower()] = _Member(name, info)

    def members(self) -> list[MemberInfo]:
        return [MemberInfo(m.name, m.info.file_size) for m in self._members.values()]

    def contains(self, member: str) -> bool:
        return normalize_member(member).lower() in self._members

    def read(self, member: str) -> bytes:
        entry = self._members.get(normalize_member(member).lower())
        if entry is None:
            raise ArchiveError(f"{self.relative_path}: no member {member!r}")
        try:
            # zipfile checks the cipher header byte and the CRC-32 itself.
            return self._zip.read(entry.info, pwd=self._password)
        except RuntimeError as error:
            raise ArchiveError(
                f"{self.relative_path}: member {entry.name!r} does not decrypt with this archive's "
                f"password; the client-relative path may be wrong ({error})") from error
        except (zipfile.BadZipFile, OSError, EOFError) as error:
            raise ArchiveError(f"{self.relative_path}: member {entry.name!r} is damaged ({error})") from error

    def close(self) -> None:
        self._zip.close()


def relative_path_for(client_root: Path, archive_file: Path) -> str:
    """The client-relative path that keys an archive's password: its full path under the root."""
    return Path(os.path.relpath(archive_file, client_root)).as_posix()
