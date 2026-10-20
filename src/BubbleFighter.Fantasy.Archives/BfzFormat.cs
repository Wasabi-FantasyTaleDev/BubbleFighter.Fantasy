using System.Text;

namespace BubbleFighter.Fantasy.Archives;

/// <summary>
/// The BFZ archives of the older client: ordinary zip files whose members are encrypted with
/// the traditional zip cipher. Each archive has its own password, derived from the archive's
/// path relative to the client root with <c>/</c> separators (for example
/// <c>Config/Config.bfz</c>).
/// </summary>
/// <remarks>
/// The derivation is <c>sub_9C8710</c> of the 2014 client: the path is upper-cased and, for
/// each of 64 positions <c>i</c>, the password takes character
/// <c>table[i % 6][path[i % pathLength] % 64]</c> from six 64-character tables
/// (<c>off_DDBA34</c>). The client builds the archive path from a directory name as
/// <c>&lt;dir&gt;/&lt;last component&gt;.bfz</c> (<c>sub_9C8EA0</c>, which opens it through
/// <c>sub_9CD310</c>).
/// </remarks>
public static class BfzFormat
{
    /// <summary>
    /// The code page of member names without the zip UTF-8 flag: Korean CP949. The client never decodes them; it
    /// compares the stored bytes with the requested ANSI path (kr-812 <c>sub_A4F4D0</c>, <c>lstrcmpiA</c>), and the
    /// Korean-authored archives spell non-ASCII names in CP949 (for example the NpcMode script list's file name).
    /// </summary>
    public static Encoding NameEncoding { get; } = CodePagesEncodingProvider.Instance.GetEncoding(949)
        ?? throw new InvalidOperationException("Code page 949 is not available.");

    // off_DDBA34 of the 2014 client, in table order.
    private static readonly string[] Tables =
    [
        "as89f7d6af98e7f6a9s87f6as98f76asdf8yasuefyae8f7as6ef9ase87fa6se9",
        "alkjdfLKDFJeofias4894ewfadfklDFJfsafjlasff89a4fhJLKDSfHdf98daf9s",
        "afoiefajsfiasdf87ysda987dsfvhsdkvhs8dr7vhsdkfghsie4g8hsdfukzDFDf",
        "faklsjdfiod9f8asdfjKLDFjds9f8jasdklfjsdf9asd8fuseijkasdkHDFKJDf8",
        "dflkjasdf98asfuyaiufhaos8efhLHFDJSFhasod8fsdfkLDfd9f8aslkejfDLkf",
        "dkfjapsdoifjas9d8fajsdfiashdJKFSHdfdfaskdljf9sd8fasd89f7asieufa9",
    ];

    /// <summary>Derives an archive's password bytes from its client-relative path.</summary>
    /// <param name="clientRelativePath">For example <c>Config/Config.bfz</c>.</param>
    public static byte[] Password(string clientRelativePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(clientRelativePath);
        var path = Encoding.Latin1.GetBytes(clientRelativePath.Replace('\\', '/').ToUpperInvariant());
        var length = Tables[0].Length;
        var password = new byte[length];
        for (var i = 0; i < length; i++)
            password[i] = (byte)Tables[i % 6][path[i % path.Length] % length];
        return password;
    }

    /// <summary>
    /// The client-relative path the client itself would use for an archive in a folder of
    /// its own name, for example <c>Config/Config.bfz</c> for <c>...\Config\Config.bfz</c>;
    /// nested archives need their full relative path passed explicitly.
    /// </summary>
    /// <param name="filePath">The archive file.</param>
    public static string InferRelativePath(string filePath)
    {
        var full = Path.GetFullPath(filePath);
        var folder = Path.GetFileName(Path.GetDirectoryName(full)) ?? string.Empty;
        return folder.Length == 0 ? Path.GetFileName(full) : folder + "/" + Path.GetFileName(full);
    }
}

/// <summary>The traditional zip stream cipher (PKWARE "ZipCrypto").</summary>
internal sealed class ZipCrypto
{
    private uint _k0 = 0x12345678;
    private uint _k1 = 0x23456789;
    private uint _k2 = 0x34567890;

    public ZipCrypto(ReadOnlySpan<byte> password)
    {
        foreach (var b in password)
            Update(b);
    }

    public void Decrypt(Span<byte> data)
    {
        for (var i = 0; i < data.Length; i++)
        {
            var plain = (byte)(data[i] ^ KeyByte());
            Update(plain);
            data[i] = plain;
        }
    }

    public void Encrypt(Span<byte> data)
    {
        for (var i = 0; i < data.Length; i++)
        {
            var plain = data[i];
            data[i] = (byte)(plain ^ KeyByte());
            Update(plain);
        }
    }

    private byte KeyByte()
    {
        var temp = (ushort)(_k2 | 2);
        return (byte)((temp * (temp ^ 1)) >> 8);
    }

    private void Update(byte b)
    {
        _k0 = Crc(_k0, b);
        _k1 = unchecked((_k1 + (_k0 & 0xFF)) * 134775813 + 1);
        _k2 = Crc(_k2, (byte)(_k1 >> 24));
    }

    private static uint Crc(uint crc, byte b) => CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
            crc = Crc(crc, b);
        return ~crc;
    }
}
