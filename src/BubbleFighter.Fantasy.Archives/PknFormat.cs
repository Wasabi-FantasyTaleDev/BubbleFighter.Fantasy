namespace BubbleFighter.Fantasy.Archives;

/// <summary>
/// The PKN pack layout of the current client, read by <c>kr-1256 sub_1026520</c> (pack open and
/// index) and <c>kr-1256 sub_1024B70</c>/<c>kr-1256 sub_1024D20</c> (members). Everything is keyed by the pack URI: the
/// pack's path relative to the client root, lower-cased, with <c>/</c> separators, for
/// example <c>packs/config_00000.pkn</c>. The cn-279 V1 generation
/// (<see cref="PknArchiveV1"/>) implements the same formulas at its own addresses; the two
/// never share one.
/// </summary>
/// <remarks>
/// <para>File layout: nothing marks the format. At <see cref="HeaderOffset"/> sits a 9-byte
/// header encrypted with <see cref="HeaderKey"/>: <c>u32 check</c>, <c>u8 version</c> (2),
/// <c>u32 count</c>, with <c>check == count + version</c>. At <see cref="IndexOffset"/> sits
/// the index encrypted with <see cref="IndexKey"/>: per member a prefixed UTF-16 path, then
/// <c>u32 checksum, u32 flags, u32 block, u32 size, u32 storedSize</c> and the 16-byte MD5 of
/// the unpacked member, with <c>checksum == flags + block + size + storedSize + sum(md5 bytes)</c>.
/// Member data starts at the index end rounded up to 1024 bytes; a member occupies whole
/// 1024-byte blocks from <c>dataStart + block * 1024</c>.</para>
/// <para>Member flags (<c>kr-1256 sub_1024D20</c>): bit 1 zlib, bit 2 whole member encrypted, bit 4
/// only its first 1024 bytes encrypted, each with <see cref="MemberKey"/>. Decryption comes
/// before inflation.</para>
/// </remarks>
public static class PknFormat
{
    /// <summary>The only pack version the client accepts.</summary>
    public const byte Version = 2;

    /// <summary>The allocation unit of member data.</summary>
    public const int BlockSize = 1024;

    /// <summary>Member flag: the stored bytes are a zlib stream.</summary>
    public const uint FlagZlib = 1;

    /// <summary>Member flag: all stored bytes are encrypted.</summary>
    public const uint FlagEncrypted = 2;

    /// <summary>Member flag: only the first <see cref="BlockSize"/> stored bytes are encrypted.</summary>
    public const uint FlagHeadEncrypted = 4;

    /// <summary>The pack URI for a pack file inside the client's <c>Packs</c> directory.</summary>
    /// <param name="packFileName">The pack's file name, for example <c>Config_00000.pkn</c>.</param>
    public static string UriForPack(string packFileName) => ("packs/" + packFileName).ToLowerInvariant();

    /// <summary>
    /// Header offset: the sum of the URI's UTF-16 code units modulo 312, plus 30
    /// (<c>kr-1256 sub_1026520</c>, <c>kr-1256 0x10265FD</c>..<c>kr-1256 0x102661C</c>).
    /// </summary>
    /// <param name="uri">The pack URI.</param>
    public static int HeaderOffset(string uri) => (int)(CodeUnitSum(uri) % 312) + 30;

    /// <summary>
    /// Index offset: the header offset plus 42 plus three times the code-unit sum modulo 212
    /// (<c>kr-1256 sub_1026520</c>, <c>kr-1256 0x1026680</c>..<c>kr-1256 0x102686B</c>).
    /// </summary>
    /// <param name="uri">The pack URI.</param>
    public static int IndexOffset(string uri) => HeaderOffset(uri) + 42 + (int)(3 * CodeUnitSum(uri) % 212);

    /// <summary>Header key: <c>key[i] = i + uri[i % len]</c> (low byte of each code unit).</summary>
    /// <param name="uri">The pack URI.</param>
    public static byte[] HeaderKey(string uri)
    {
        var key = new byte[128];
        for (var i = 0; i < key.Length; i++)
            key[i] = unchecked((byte)(i + (byte)uri[i % uri.Length]));
        return key;
    }

    /// <summary>
    /// Index key: <c>key[i] = i + (i % 3 + 2) * uri[len - 1 - i % len]</c>, the URI read
    /// backwards (<c>0x10268F0</c>..<c>0x1026944</c>).
    /// </summary>
    /// <param name="uri">The pack URI.</param>
    public static byte[] IndexKey(string uri)
    {
        var key = new byte[128];
        var length = uri.Length;
        for (var i = 0; i < key.Length; i++)
            key[i] = unchecked((byte)(i + (i % 3 + 2) * (sbyte)(byte)uri[length - 1 - i % length]));
        return key;
    }

    /// <summary>
    /// Member key: <c>key[i] = i + path[i % len] * (md5[i &amp; 15] + i % 5 + 2)</c> over the
    /// member path as the index stores it (<c>sub_1027350</c>).
    /// </summary>
    /// <param name="memberPath">The member path exactly as stored.</param>
    /// <param name="md5">The member's 16-byte MD5.</param>
    public static byte[] MemberKey(string memberPath, ReadOnlySpan<byte> md5)
    {
        var key = new byte[128];
        var length = memberPath.Length;
        for (var i = 0; i < key.Length; i++)
        {
            var multiplier = (sbyte)unchecked((byte)(md5[i & 15] + i % 5 + 2));
            key[i] = unchecked((byte)(i + (sbyte)(byte)memberPath[i % length] * multiplier));
        }
        return key;
    }

    private static uint CodeUnitSum(string uri)
    {
        uint sum = 0;
        foreach (var c in uri)
            sum += c;
        return sum;
    }
}
