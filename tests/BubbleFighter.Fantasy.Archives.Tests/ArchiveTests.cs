using System.Text;
using BubbleFighter.Fantasy.Archives;
using Xunit;

namespace BubbleFighter.Fantasy.Archives.Tests;

public sealed class Snow2CipherTests
{
    [Fact]
    public void TextbookKeyLoadReproducesThePublishedSnow2Vector()
    {
        // SNOW 2.0 specification test vector: 128-bit key 80 00.. 00, IV 0.
        var key = new byte[16];
        key[0] = 0x80;
        var cipher = new Snow2Cipher(key, signExtendKey: false);
        uint[] expected = [0x8D590AE9, 0xA74A7D05, 0x6DC9CA74, 0xB72D1A45, 0x99B0A083];
        Assert.Equal(expected, Enumerable.Range(0, 5).Select(_ => cipher.NextWord()).ToArray());
    }

    [Fact]
    public void ClientKeyLoadSignExtendsHighKeyBytes()
    {
        var key = new byte[16];
        key[1] = 0x80;
        // The client's key schedule reads the key through a signed char, so a high byte after
        // the first of a word spills ones into the bytes above it; keys below 0x80 are unaffected.
        Assert.NotEqual(new Snow2Cipher(key, signExtendKey: false).NextWord(), new Snow2Cipher(key).NextWord());
        var low = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        Assert.Equal(new Snow2Cipher(low, signExtendKey: false).NextWord(), new Snow2Cipher(low).NextWord());
    }

    [Fact]
    public void EncryptThenDecryptRestoresWholeWordsAndLeavesTheTail()
    {
        var key = Enumerable.Range(0, 16).Select(i => (byte)(i * 17)).ToArray();
        var plain = Enumerable.Range(0, 11).Select(i => (byte)i).ToArray();
        var data = plain.ToArray();
        new Snow2Cipher(key).Encrypt(data);
        Assert.NotEqual(plain[..8], data[..8]);
        Assert.Equal(plain[8..], data[8..]);
        new Snow2Cipher(key).Decrypt(data);
        Assert.Equal(plain, data);
    }
}

public sealed class PknTests
{
    [Fact]
    public void OffsetsMatchTheClientsConfigPack()
    {
        // Observed on the client's Packs/Config_00000.pkn: header at 75, index at 144.
        Assert.Equal("packs/config_00000.pkn", PknFormat.UriForPack("Config_00000.pkn"));
        Assert.Equal(75, PknFormat.HeaderOffset("packs/config_00000.pkn"));
        Assert.Equal(144, PknFormat.IndexOffset("packs/config_00000.pkn"));
    }

    [Fact]
    public void WrittenPackReadsBackForEveryLayerCombination()
    {
        var members = new List<(string, byte[])>
        {
            ("Config/A.xml", Encoding.UTF8.GetBytes("<a>" + new string('x', 5000) + "</a>")),
            ("Config/Sub/B.csv", Encoding.UTF8.GetBytes("1,2,3")),
            ("Config/Empty.ini", []),
            ("Config/Odd.bin", Enumerable.Range(0, 2051).Select(i => (byte)i).ToArray()),
        };
        foreach (var (compress, encrypt) in new[] { (true, true), (true, false), (false, true), (false, false) })
        {
            var buffer = new MemoryStream();
            PknWriter.Write(buffer, "packs/test_00000.pkn", members, compress, encrypt);
            buffer.Position = 0;
            using var pack = PknArchive.Open(buffer, "packs/test_00000.pkn");
            Assert.Equal(members.Select(m => m.Item1), pack.Entries.Select(e => e.Path));
            foreach (var (path, data) in members)
                Assert.Equal(data, pack.Read(path.ToUpperInvariant().Replace('/', '\\')));
            Assert.All(pack.Members, m => Assert.Equal((compress ? 1u : 0) | (encrypt ? 2u : 0), m.Flags));
        }
    }

    [Fact]
    public void AWrongPackUriIsRefused()
    {
        var buffer = new MemoryStream();
        PknWriter.Write(buffer, "packs/test_00000.pkn", [("a.txt", [1, 2, 3])]);
        buffer.Position = 0;
        Assert.Throws<InvalidDataException>(() => PknArchive.Open(buffer, "packs/other_00000.pkn"));
    }

    [Fact]
    public void ACorruptedMemberFailsItsMd5()
    {
        var buffer = new MemoryStream();
        PknWriter.Write(buffer, "packs/test_00000.pkn", [("a.txt", Encoding.UTF8.GetBytes("hello pack"))], compress: false, encrypt: false);
        var bytes = buffer.ToArray();
        using (var probe = PknArchive.Open(new MemoryStream(bytes), "packs/test_00000.pkn"))
            bytes[probe.DataOffset] ^= 0xFF;
        using var pack = PknArchive.Open(new MemoryStream(bytes), "packs/test_00000.pkn");
        Assert.Throws<InvalidDataException>(() => pack.Read("a.txt"));
    }
}

public sealed class BfzTests
{
    [Fact]
    public void PasswordMatchesTheClientDerivationForConfig()
    {
        Assert.Equal("9afd9daj8asi6fodfj6iaod89afd9d7ka9kdfLffDj6iaodd8Lfly9dFjskdfLff",
            Encoding.Latin1.GetString(BfzFormat.Password("Config/Config.bfz")));
        // The client upper-cases the path, so case does not matter; separators are normalized.
        Assert.Equal(BfzFormat.Password("Config/Config.bfz"), BfzFormat.Password(@"config\config.BFZ"));
    }

    [Fact]
    public void WrittenArchiveReadsBackAndRejectsTheWrongPassword()
    {
        var members = new List<(string, byte[])>
        {
            ("Config/A.xml", Encoding.UTF8.GetBytes("<a>" + new string('y', 3000) + "</a>")),
            ("Config/Empty.ini", []),
        };
        var buffer = new MemoryStream();
        BfzArchive.Write(buffer, "Config/Config.bfz", members);
        var bytes = buffer.ToArray();

        using var archive = BfzArchive.Open(new MemoryStream(bytes), "Config/Config.bfz");
        Assert.Equal(members.Select(m => m.Item1), archive.Entries.Select(e => e.Path));
        foreach (var (path, data) in members)
            Assert.Equal(data, archive.Read(path));

        using var wrong = BfzArchive.Open(new MemoryStream(bytes), "Other/Other.bfz");
        Assert.Throws<InvalidDataException>(() => wrong.Read("Config/A.xml"));
    }

    [Fact]
    public void InferredPathIsTheFolderAndFileName()
    {
        Assert.Equal("Config/Config.bfz", BfzFormat.InferRelativePath(Path.Combine("client", "Config", "Config.bfz")));
    }
}
