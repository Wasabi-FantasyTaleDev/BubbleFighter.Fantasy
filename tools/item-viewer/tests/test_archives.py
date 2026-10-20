"""Archive indexing and path safety, on synthetic archives built with the same keys the readers use."""

import hashlib
import struct
import zipfile
import zlib
from pathlib import Path

import pytest

from bf_item_viewer.archives import pkn
from bf_item_viewer.archives.base import ArchiveError, UnsafePathError, normalize_member
from bf_item_viewer.archives.bfz import BfzArchive, password
from bf_item_viewer.archives.index import ResourceIndex
from bf_item_viewer.archives.snow2 import Snow2


def test_snow2_roundtrip_and_tail():
    key = bytes(range(200, 216))          # bytes >= 0x80 exercise the sign-extending key load
    data = bytes(range(37))
    enc = Snow2(key).encrypt(data)
    assert enc[-1:] == data[-1:]           # a tail shorter than a word is left untouched
    assert bytes(Snow2(key).decrypt(enc)) == data
    assert Snow2(key, sign_extend_key=True).keystream(4) != Snow2(key, sign_extend_key=False).keystream(4)


def build_pkn(path: Path, uri: str, members: dict[str, bytes]) -> None:
    """Write a version-2 pack exactly as the reader expects it."""
    index = bytearray()
    blobs = []
    block = 0
    for name, data in members.items():
        stored = zlib.compress(data)
        md5 = hashlib.md5(data).digest()
        flags = pkn.FLAG_ZLIB | pkn.FLAG_ENCRYPTED
        padded = stored + b"\0" * ((-len(stored)) % 4)
        enc = bytes(Snow2(pkn.member_key(name, md5)).encrypt(padded))
        checksum = (flags + block + len(data) + len(stored) + sum(md5)) & 0xFFFFFFFF
        index += struct.pack("<I", len(name)) + name.encode("utf-16-le")
        index += struct.pack("<5I", checksum, flags, block, len(data), len(stored)) + md5
        blobs.append((block, enc))
        block += (len(enc) + pkn.BLOCK - 1) // pkn.BLOCK
    index += b"\0" * ((-len(index)) % 4)
    h_off, i_off = pkn.header_offset(uri), pkn.index_offset(uri)
    header = struct.pack("<IBI", len(members) + 2, 2, len(members)) + b"\0\0\0"
    out = bytearray(i_off + len(index))
    out[h_off:h_off + 12] = Snow2(pkn.header_key(uri)).encrypt(header)
    out[i_off:i_off + len(index)] = Snow2(pkn.index_key(uri)).encrypt(bytes(index))
    data_start = (len(out) + pkn.BLOCK - 1) // pkn.BLOCK * pkn.BLOCK
    out += b"\0" * (data_start - len(out))
    for blk, enc in blobs:
        need = data_start + blk * pkn.BLOCK + len(enc)
        if len(out) < need:
            out += b"\0" * (need - len(out))
        out[data_start + blk * pkn.BLOCK:need] = enc
    path.write_bytes(bytes(out))


def build_bfz(path: Path, rel: str, members: dict[str, bytes]) -> None:
    """Python cannot write ZipCrypto, so encrypt members by hand into a stored zip."""
    from bf_item_viewer.archives import bfz as bfz_module  # noqa: F401  (keys only)
    pw = password(rel)

    def crc_table():
        t = []
        for n in range(256):
            c = n
            for _ in range(8):
                c = 0xEDB88320 ^ (c >> 1) if c & 1 else c >> 1
            t.append(c)
        return t
    table = crc_table()

    class Crypt:
        def __init__(self):
            self.k = [0x12345678, 0x23456789, 0x34567890]
            for b in pw:
                self.update(b)

        def crc(self, c, b):
            return table[(c ^ b) & 0xFF] ^ (c >> 8)

        def update(self, b):
            k = self.k
            k[0] = self.crc(k[0], b)
            k[1] = ((k[1] + (k[0] & 0xFF)) * 134775813 + 1) & 0xFFFFFFFF
            k[2] = self.crc(k[2], k[1] >> 24)

        def enc(self, data):
            out = bytearray()
            for b in data:
                t = (self.k[2] | 2) & 0xFFFF
                out.append(b ^ ((t * (t ^ 1)) >> 8) & 0xFF)
                self.update(b)
            return bytes(out)

    with open(path, "wb") as f:
        central = bytearray()
        for name, data in members.items():
            crc = zlib.crc32(data)
            body = Crypt().enc(bytes(11) + bytes([crc >> 24]) + data)
            off = f.tell()
            nb = name.encode("cp949")
            f.write(struct.pack("<IHHHHHIIIHH", 0x04034B50, 20, 1, 0, 0, 0, crc, len(body), len(data), len(nb), 0) + nb)
            f.write(body)
            central += struct.pack("<IHHHHHHIIIHHHHHII", 0x02014B50, 20, 20, 1, 0, 0, 0, crc, len(body), len(data),
                                   len(nb), 0, 0, 0, 0, 0, off) + nb
        cd = f.tell()
        f.write(central)
        f.write(struct.pack("<IHHHHIIH", 0x06054B50, 0, 0, len(members), len(members), len(central), cd, 0))


def test_pkn_roundtrip(tmp_path):
    (tmp_path / "Packs").mkdir()
    members = {"Config/a.xml": b"<x/>" * 300, "Models/Weapon/W/Gun.msh": bytes(range(256)) * 9}
    build_pkn(tmp_path / "Packs" / "Test_00000.pkn", "packs/test_00000.pkn", members)
    archive = pkn.PknArchive(tmp_path / "Packs" / "Test_00000.pkn", "packs/test_00000.pkn")
    assert {m.path for m in archive.members()} == set(members)
    assert archive.read("config/A.XML") == members["Config/a.xml"]   # case-insensitive lookup
    with pytest.raises(ArchiveError):
        pkn.PknArchive(tmp_path / "Packs" / "Test_00000.pkn", "packs/wrong.pkn")


def test_bfz_roundtrip_and_wrong_password(tmp_path):
    folder = tmp_path / "Config"
    folder.mkdir()
    build_bfz(folder / "Config.bfz", "Config/Config.bfz", {"Config/한글.txt": "가나다".encode("cp949")})
    archive = BfzArchive(folder / "Config.bfz", "Config/Config.bfz")
    assert archive.members()[0].path == "Config/한글.txt"
    assert archive.read("Config/한글.txt").decode("cp949") == "가나다"
    wrong = BfzArchive(folder / "Config.bfz", "Other/Other.bfz")
    with pytest.raises(ArchiveError):
        wrong.read("Config/한글.txt")


@pytest.mark.parametrize("bad", ["../x", "a/../../b", "/etc/passwd", "C:/x", "a\\..\\b", "", "a\0b"])
def test_unsafe_member_paths_are_refused(bad):
    with pytest.raises(UnsafePathError):
        normalize_member(bad)


def test_index_is_incremental_and_never_writes_into_client(tmp_path):
    client = tmp_path / "client"
    (client / "Packs").mkdir(parents=True)
    build_pkn(client / "Packs" / "Data_00000.pkn", "packs/data_00000.pkn", {"Config/a.txt": b"one"})
    (client / "Packs" / "old").mkdir()
    build_pkn(client / "Packs" / "old" / "Data_00000.pkn", "packs/data_00000.pkn", {"Config/a.txt": b"old"})
    before = sorted(p.relative_to(client) for p in client.rglob("*"))
    with pytest.raises(ValueError):
        ResourceIndex(client, client / "cache")
    index = ResourceIndex(client, tmp_path / "cache")
    summary = index.build()
    assert summary["sources"]["pkn"]["files"] == 1          # Packs/old is a backup, never mounted
    assert index.read("Config/a.txt") == b"one"
    assert index.provenance("config/A.txt").source_file == "Packs/Data_00000.pkn"
    assert sorted(p.relative_to(client) for p in client.rglob("*")) == before
    index.close()
    again = ResourceIndex(client, tmp_path / "cache")
    assert again.open_cached()
    assert again.read("Config/a.txt") == b"one"
    again.close()
