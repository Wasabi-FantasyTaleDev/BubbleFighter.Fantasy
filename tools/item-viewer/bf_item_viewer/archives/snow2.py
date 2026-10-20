"""SNOW 2.0 as the PKN reader of the modern client uses it.

Port of the server archive library's cipher, which was checked against kr-1256
sub_115CA70 (key schedule), sub_115D9A0/sub_115DA10 (word add/subtract) and the tables at
kr-1256 0x154E2B8..0x154F6B8. Two client details differ from textbook SNOW 2.0:

* key bytes are read through a signed ``char*`` and sign-extended before the four
  big-endian key words are assembled;
* each little-endian plaintext word is *added* to a keystream word (decryption subtracts);
  a tail shorter than a word is left untouched.
"""

from __future__ import annotations

import struct

_M32 = 0xFFFFFFFF


def _mul(a: int, b: int, poly: int) -> int:
    r = 0
    while b:
        if b & 1:
            r ^= a
        a <<= 1
        if a & 0x100:
            a ^= poly
        b >>= 1
    return r


def _power(exponent: int, poly: int) -> int:
    r = 1
    for _ in range(exponent):
        r = _mul(r, 2, poly)
    return r


def _aes_sbox() -> list[int]:
    sbox = []
    for x in range(256):
        inverse = 0
        if x:
            for y in range(1, 256):
                if _mul(x, y, 0x11B) == 1:
                    inverse = y
                    break
        s = inverse
        for i in range(1, 5):
            s ^= ((inverse << i) | (inverse >> (8 - i))) & 0xFF
        sbox.append(s ^ 0x63)
    return sbox


def _rotl(x: int, n: int) -> int:
    return ((x << n) | (x >> (32 - n))) & _M32


def _build_tables():
    sbox = _aes_sbox()
    t0, t1, t2, t3 = [], [], [], []
    for i in range(256):
        s = sbox[i]
        t = (_mul(s, 3, 0x11B) << 24) | (s << 16) | (s << 8) | _mul(s, 2, 0x11B)
        t0.append(t)
        t1.append(_rotl(t, 8))
        t2.append(_rotl(t, 16))
        t3.append(_rotl(t, 24))
    poly = 0x1A9
    b23, b245, b48, b239 = (_power(e, poly) for e in (23, 245, 48, 239))
    b16, b39, b6, b64 = (_power(e, poly) for e in (16, 39, 6, 64))
    amul = [(_mul(c, b23, poly) << 24) | (_mul(c, b245, poly) << 16) | (_mul(c, b48, poly) << 8)
            | _mul(c, b239, poly) for c in range(256)]
    adiv = [(_mul(c, b16, poly) << 24) | (_mul(c, b39, poly) << 16) | (_mul(c, b6, poly) << 8)
            | _mul(c, b64, poly) for c in range(256)]
    return t0, t1, t2, t3, amul, adiv


_T0, _T1, _T2, _T3, _AMUL, _ADIV = _build_tables()


def _t(w: int) -> int:
    return _T0[w & 0xFF] ^ _T1[(w >> 8) & 0xFF] ^ _T2[(w >> 16) & 0xFF] ^ _T3[w >> 24]


class Snow2:
    """A SNOW 2.0 keystream keyed the way the client keys it."""

    __slots__ = ("_s", "_r1", "_r2", "_block", "_next")

    def __init__(self, key: bytes, sign_extend_key: bool = True):
        if len(key) < 16:
            raise ValueError("SNOW 2.0 needs a 16-byte key")

        def load(value: int) -> int:
            return value - 256 if sign_extend_key and value >= 128 else value

        k = []
        for j in range(4):
            b0, b1, b2, b3 = (load(key[4 * j + n]) for n in range(4))
            k.append((b3 | ((b2 | ((b1 | (b0 << 8)) << 8)) << 8)) & _M32)
        nk = [(~x) & _M32 for x in k]
        s = [0] * 16
        s[15], s[14], s[13], s[12] = k
        s[11], s[10], s[9], s[8] = nk
        s[7], s[6], s[5], s[4] = k
        s[3], s[2], s[1], s[0] = nk
        r1 = r2 = 0
        amul, adiv = _AMUL, _ADIV
        for _ in range(2):
            for i in range(16):
                fsm = ((r1 + s[(i + 15) & 15]) & _M32) ^ r2
                a = s[i]
                d = s[(i + 11) & 15]
                s[i] = (((a << 8) & _M32) ^ amul[a >> 24]) ^ s[(i + 2) & 15] ^ ((d >> 8) ^ adiv[d & 0xFF]) ^ fsm
                nr1 = (r2 + s[(i + 5) & 15]) & _M32
                r2 = _t(r1)
                r1 = nr1
        self._s = s
        self._r1 = r1
        self._r2 = r2
        self._block = [0] * 16
        self._next = 16

    def _refill(self) -> None:
        s, r1, r2, block = self._s, self._r1, self._r2, self._block
        amul, adiv, t0, t1, t2, t3 = _AMUL, _ADIV, _T0, _T1, _T2, _T3
        for i in range(16):
            a = s[i]
            d = s[(i + 11) & 15]
            s[i] = (((a << 8) & _M32) ^ amul[a >> 24]) ^ s[(i + 2) & 15] ^ ((d >> 8) ^ adiv[d & 0xFF])
            nr1 = (r2 + s[(i + 5) & 15]) & _M32
            r2 = t0[r1 & 0xFF] ^ t1[(r1 >> 8) & 0xFF] ^ t2[(r1 >> 16) & 0xFF] ^ t3[r1 >> 24]
            r1 = nr1
            block[i] = ((r1 + s[i]) & _M32) ^ r2 ^ s[(i + 1) & 15]
        self._r1, self._r2 = r1, r2
        self._next = 0

    def next_word(self) -> int:
        if self._next == 16:
            self._refill()
        word = self._block[self._next]
        self._next += 1
        return word

    def keystream(self, words: int) -> list[int]:
        out = []
        while len(out) < words:
            if self._next == 16:
                self._refill()
            take = min(16 - self._next, words - len(out))
            out.extend(self._block[self._next:self._next + take])
            self._next += take
        return out

    def decrypt(self, data: bytes | bytearray) -> bytearray:
        """Return ``data`` with its whole words decrypted; a shorter tail is copied unchanged."""
        buffer = bytearray(data)
        count = len(buffer) // 4
        if count:
            words = struct.unpack_from(f"<{count}I", buffer)
            stream = self.keystream(count)
            struct.pack_into(f"<{count}I", buffer, 0, *[(w - k) & _M32 for w, k in zip(words, stream)])
        return buffer

    def encrypt(self, data: bytes | bytearray) -> bytearray:
        """Inverse of :meth:`decrypt`, used by tests to build synthetic packs."""
        buffer = bytearray(data)
        count = len(buffer) // 4
        if count:
            words = struct.unpack_from(f"<{count}I", buffer)
            stream = self.keystream(count)
            struct.pack_into(f"<{count}I", buffer, 0, *[(w + k) & _M32 for w, k in zip(words, stream)])
        return buffer
