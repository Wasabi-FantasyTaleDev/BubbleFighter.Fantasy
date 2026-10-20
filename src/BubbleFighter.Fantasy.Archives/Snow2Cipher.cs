using System.Buffers.Binary;

namespace BubbleFighter.Fantasy.Archives;

/// <summary>
/// The SNOW 2.0 stream cipher (128-bit key, all-zero IV) as the client's PKN reader uses it.
/// The six lookup tables are built from the cipher's definition (the AES S-box and the
/// alpha multiplications over GF(2^8) with x^8+x^7+x^5+x^3+1) and equal the client's tables
/// at <c>0x154E2B8</c>..<c>0x154F6B8</c>. The keystream is applied word by word: each
/// little-endian <c>u32</c> of plaintext is added to a keystream word (<c>sub_115D9A0</c>) and
/// decryption subtracts it (<c>sub_115DA10</c>); a tail shorter than a word is not processed.
/// </summary>
public sealed class Snow2Cipher
{
    private static readonly uint[] T0 = new uint[256];
    private static readonly uint[] T1 = new uint[256];
    private static readonly uint[] T2 = new uint[256];
    private static readonly uint[] T3 = new uint[256];
    private static readonly uint[] AlphaMul = new uint[256];
    private static readonly uint[] AlphaDiv = new uint[256];

    private readonly uint[] _s = new uint[16];
    private readonly uint[] _block = new uint[16];
    private uint _r1;
    private uint _r2;
    private int _next = 16;

    static Snow2Cipher()
    {
        var sbox = AesSbox();
        for (var i = 0; i < 256; i++)
        {
            uint s = sbox[i];
            var t = (Mul(s, 3, 0x11B) << 24) | (s << 16) | (s << 8) | Mul(s, 2, 0x11B);
            T0[i] = t;
            T1[i] = uint.RotateLeft(t, 8);
            T2[i] = uint.RotateLeft(t, 16);
            T3[i] = uint.RotateLeft(t, 24);
        }

        const uint poly = 0x1A9;
        uint b23 = Power(23, poly), b245 = Power(245, poly), b48 = Power(48, poly), b239 = Power(239, poly);
        uint b16 = Power(16, poly), b39 = Power(39, poly), b6 = Power(6, poly), b64 = Power(64, poly);
        for (uint c = 0; c < 256; c++)
        {
            AlphaMul[c] = (Mul(c, b23, poly) << 24) | (Mul(c, b245, poly) << 16) | (Mul(c, b48, poly) << 8) | Mul(c, b239, poly);
            AlphaDiv[c] = (Mul(c, b16, poly) << 24) | (Mul(c, b39, poly) << 16) | (Mul(c, b6, poly) << 8) | Mul(c, b64, poly);
        }
    }

    /// <summary>
    /// Keys the cipher from the first 16 bytes of <paramref name="key"/>. The client's key
    /// schedule (<c>sub_115CA70</c>) reads the key through a signed <c>char*</c>, so every byte
    /// is sign-extended before the four big-endian words are assembled; for key bytes of
    /// <c>0x80</c> and above this differs from textbook SNOW 2.0, and the client's packs depend
    /// on it.
    /// </summary>
    /// <param name="key">At least 16 key bytes; only the first 16 are used.</param>
    public Snow2Cipher(ReadOnlySpan<byte> key)
        : this(key, signExtendKey: true)
    {
    }

    /// <summary>
    /// Keys the cipher, choosing between the client's sign-extending key load and the textbook
    /// one (bytes taken as unsigned), which the published SNOW 2.0 test vectors use.
    /// </summary>
    /// <param name="key">At least 16 key bytes; only the first 16 are used.</param>
    /// <param name="signExtendKey">Whether key bytes are sign-extended as the client does.</param>
    public Snow2Cipher(ReadOnlySpan<byte> key, bool signExtendKey)
    {
        if (key.Length < 16)
            throw new ArgumentException("SNOW 2.0 needs a 16-byte key.", nameof(key));
        Span<uint> k = stackalloc uint[4];
        for (var j = 0; j < 4; j++)
        {
            int b0 = Load(key[4 * j]), b1 = Load(key[4 * j + 1]), b2 = Load(key[4 * j + 2]), b3 = Load(key[4 * j + 3]);
            k[j] = unchecked((uint)(b3 | ((b2 | ((b1 | (b0 << 8)) << 8)) << 8)));
        }

        int Load(byte value) => signExtendKey ? (sbyte)value : value;

        _s[15] = k[0]; _s[14] = k[1]; _s[13] = k[2]; _s[12] = k[3];
        _s[11] = ~k[0]; _s[10] = ~k[1]; _s[9] = ~k[2]; _s[8] = ~k[3];
        _s[7] = k[0]; _s[6] = k[1]; _s[5] = k[2]; _s[4] = k[3];
        _s[3] = ~k[0]; _s[2] = ~k[1]; _s[1] = ~k[2]; _s[0] = ~k[3];
        for (var round = 0; round < 2; round++)
        {
            for (var i = 0; i < 16; i++)
            {
                var fsm = unchecked(_r1 + _s[(i + 15) & 15]) ^ _r2;
                _s[i] = MulAlpha(_s[i]) ^ _s[(i + 2) & 15] ^ DivAlpha(_s[(i + 11) & 15]) ^ fsm;
                var r1 = unchecked(_r2 + _s[(i + 5) & 15]);
                _r2 = T(_r1);
                _r1 = r1;
            }
        }
    }

    /// <summary>Returns the next keystream word.</summary>
    public uint NextWord()
    {
        if (_next == 16)
        {
            for (var i = 0; i < 16; i++)
            {
                _s[i] = MulAlpha(_s[i]) ^ _s[(i + 2) & 15] ^ DivAlpha(_s[(i + 11) & 15]);
                var r1 = unchecked(_r2 + _s[(i + 5) & 15]);
                _r2 = T(_r1);
                _r1 = r1;
                _block[i] = unchecked(_r1 + _s[i]) ^ _r2 ^ _s[(i + 1) & 15];
            }
            _next = 0;
        }
        return _block[_next++];
    }

    /// <summary>Decrypts the whole words of <paramref name="data"/> in place; a shorter tail is left as it is.</summary>
    /// <param name="data">Ciphertext, continuing this cipher's keystream position.</param>
    public void Decrypt(Span<byte> data)
    {
        for (var i = 0; i + 4 <= data.Length; i += 4)
        {
            var word = BinaryPrimitives.ReadUInt32LittleEndian(data[i..]);
            BinaryPrimitives.WriteUInt32LittleEndian(data[i..], unchecked(word - NextWord()));
        }
    }

    /// <summary>Encrypts the whole words of <paramref name="data"/> in place; a shorter tail is left as it is.</summary>
    /// <param name="data">Plaintext, continuing this cipher's keystream position.</param>
    public void Encrypt(Span<byte> data)
    {
        for (var i = 0; i + 4 <= data.Length; i += 4)
        {
            var word = BinaryPrimitives.ReadUInt32LittleEndian(data[i..]);
            BinaryPrimitives.WriteUInt32LittleEndian(data[i..], unchecked(word + NextWord()));
        }
    }

    private static uint MulAlpha(uint w) => (w << 8) ^ AlphaMul[w >> 24];

    private static uint DivAlpha(uint w) => (w >> 8) ^ AlphaDiv[w & 0xFF];

    private static uint T(uint w) => T0[w & 0xFF] ^ T1[(w >> 8) & 0xFF] ^ T2[(w >> 16) & 0xFF] ^ T3[w >> 24];

    private static uint Mul(uint a, uint b, uint poly)
    {
        uint r = 0;
        while (b != 0)
        {
            if ((b & 1) != 0) r ^= a;
            a <<= 1;
            if ((a & 0x100) != 0) a ^= poly;
            b >>= 1;
        }
        return r;
    }

    private static uint Power(int exponent, uint poly)
    {
        uint r = 1;
        for (var i = 0; i < exponent; i++) r = Mul(r, 2, poly);
        return r;
    }

    private static byte[] AesSbox()
    {
        var sbox = new byte[256];
        for (var x = 0; x < 256; x++)
        {
            uint inverse = 0;
            if (x != 0)
            {
                for (uint y = 1; y < 256; y++)
                {
                    if (Mul((uint)x, y, 0x11B) == 1) { inverse = y; break; }
                }
            }
            var s = inverse;
            for (var i = 1; i < 5; i++) s ^= ((inverse << i) | (inverse >> (8 - i))) & 0xFF;
            sbox[x] = (byte)(s ^ 0x63);
        }
        return sbox;
    }
}
