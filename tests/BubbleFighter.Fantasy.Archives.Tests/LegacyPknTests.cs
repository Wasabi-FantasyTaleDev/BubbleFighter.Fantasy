using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using BubbleFighter.Fantasy.Archives;
using Xunit;

namespace BubbleFighter.Fantasy.Archives.Tests;

public sealed class LegacyPknTests
{
    private const string Uri = "Pack/Test0000.pkn";

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    [InlineData(4u)]
    [InlineData(5u)]
    [InlineData(6u)]
    [InlineData(7u)]
    public void RootedMembersDecodeAndVerifyAcrossStorageFlags(uint flags)
    {
        var plain = Encoding.UTF8.GetBytes(new string('a', 5003));
        var bytes = Fixture(plain, flags);
        using var pack = LegacyPknArchive.Open(new MemoryStream(bytes), Uri);
        Assert.Equal(Uri, pack.Uri);
        Assert.Equal(1024, pack.DataOffset);
        Assert.Equal("Config/Test.xml", Assert.Single(pack.Entries).Path);
        Assert.True(pack.Contains(@"config\test.XML"));
        Assert.Equal(plain, pack.Read("Config/Test.xml"));
        Assert.Throws<FileNotFoundException>(() => pack.Read("Test.xml"));
    }

    [Fact]
    public void WrongCaseAndWrongLayoutAreRejected()
    {
        var bytes = Fixture([1, 2, 3], 2);
        Assert.Throws<InvalidDataException>(() => LegacyPknArchive.Open(new MemoryStream(bytes), Uri.ToLowerInvariant()));
        Assert.Throws<InvalidDataException>(() => PknArchive.Open(new MemoryStream(bytes), Uri.ToLowerInvariant()));
    }

    [Fact]
    public void CorruptionAndTruncationCannotReturnUnverifiedBytes()
    {
        var bytes = Fixture([1, 2, 3, 4], 0);
        bytes[1024] ^= 1;
        using var pack = LegacyPknArchive.Open(new MemoryStream(bytes), Uri);
        Assert.Throws<InvalidDataException>(() => pack.Read("Config/Test.xml"));
        Assert.Throws<InvalidDataException>(() => LegacyPknArchive.Open(new MemoryStream(bytes[..1025]), Uri));
        Assert.ThrowsAny<IOException>(() => LegacyPknArchive.Open(new MemoryStream(bytes[..20]), Uri));
    }

    [Fact]
    public void MemberLimitAndExpandedLengthAreEnforced()
    {
        Assert.Throws<InvalidDataException>(() => LegacyPknArchive.Open(new MemoryStream(Fixture(new byte[32], 0)), Uri, 16));
        using var pack = LegacyPknArchive.Open(new MemoryStream(Fixture(new byte[32], 1, declaredSize: 31)), Uri);
        Assert.Throws<InvalidDataException>(() => pack.Read("Config/Test.xml"));
    }

    [Fact]
    public void ArchiveApiDetectsTheEarlyLayoutFromTheFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"legacy-{Guid.NewGuid():N}.pkn");
        try
        {
            File.WriteAllBytes(path, Fixture([1, 2, 3], 2));
            using var reader = Archive.Open(path, Uri);
            Assert.IsType<LegacyPknArchive>(reader);
            Assert.Equal(new byte[] { 1, 2, 3 }, reader.Read("Config/Test.xml"));
            // The early layout keys its URI case-sensitively, so a wrong case decodes as neither layout.
            Assert.Throws<InvalidDataException>(() => Archive.Open(path, Uri.ToUpperInvariant()));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ToolCopiesUnpacksAndExtractsLegacyPacksAtTheirOriginalPath()
    {
        var root = Path.Combine(Path.GetTempPath(), $"legacy-cli-{Guid.NewGuid():N}");
        try
        {
            var client = Path.Combine(root, "client");
            var source = Path.Combine(client, "Pack", "Test0000.pkn");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.WriteAllBytes(source, Fixture([1, 2, 3], 3));
            var build = Path.Combine(root, "build");
            Assert.Equal(0, Tool.Program.Main(["copy", client, build, "Config/Test.xml"]));
            var pack = Path.Combine(build, "data", "Pack", "Test0000.pkn");
            Assert.Equal(0, Tool.Program.Main(["verify", pack]));
            Assert.Equal(1, Tool.Program.Main(["verify", pack, "--pkn-layout", "legacy"]));
            Assert.Equal(0, Tool.Program.Main(["unpack", client, build, "config/test.xml"]));
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(build, "dataraw", "Config", "Test.xml")));
            var extracted = Path.Combine(root, "extract");
            Assert.Equal(0, Tool.Program.Main(["extract", pack, extracted]));
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(extracted, "Config", "Test.xml")));
            Assert.Equal(1, Tool.Program.Main(["copy", client, build, "Config/Missing.xml"]));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void InteractiveMenuRunsTheChosenCommandAndQuits()
    {
        var root = Path.Combine(Path.GetTempPath(), $"legacy-menu-{Guid.NewGuid():N}");
        try
        {
            var client = Path.Combine(root, "client");
            var source = Path.Combine(client, "Pack", "Test0000.pkn");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.WriteAllBytes(source, Fixture([4, 5], 0));
            var build = Path.Combine(root, "build");
            var output = new StringWriter();
            var answers = string.Join(Environment.NewLine, "6", client, build, "Config/Test.xml", "9", "0");
            var exit = Tool.Program.Interactive(new StringReader(answers), output);
            Assert.Equal(0, exit);
            Assert.True(File.Exists(Path.Combine(build, "data", "Pack", "Test0000.pkn")));
            Assert.Contains("Please type one of the numbers above.", output.ToString());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // 0x61D8B0: four header words/root; each index entry has path, four words and MD5.
    private static byte[] Fixture(byte[] plain, uint flags, uint? declaredSize = null)
    {
        var payload = plain.ToArray();
        if ((flags & 1) != 0)
        {
            using var buffer = new MemoryStream();
            using (var zlib = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(plain);
            payload = buffer.ToArray();
        }
        var size = payload.Length;
        Array.Resize(ref payload, (size + 3) & ~3);
        var md5 = MD5.HashData(plain);
        if ((flags & 2) != 0) new Snow2Cipher(PknFormat.MemberKey("Config/Test.xml", md5)).Encrypt(payload);
        if ((flags & 4) != 0) new Snow2Cipher(PknFormat.MemberKey("Config/Test.xml", md5)).Encrypt(payload.AsSpan(0, Math.Min(1024, payload.Length)));
        var header = new MemoryStream();
        var writer = new BinaryWriter(header, Encoding.Unicode, leaveOpen: true);
        writer.Write(0u); writer.Write(0xB42AE450u); writer.Write(42u); writer.Write(1u);
        writer.Write(6u); writer.Write(Encoding.Unicode.GetBytes("Config"));
        var headerBytes = header.ToArray();
        new Snow2Cipher(PknFormat.HeaderKey(Uri)).Encrypt(headerBytes);
        var index = new MemoryStream();
        writer = new BinaryWriter(index, Encoding.Unicode, leaveOpen: true);
        writer.Write(8u); writer.Write(Encoding.Unicode.GetBytes("Test.xml"));
        writer.Write(flags); writer.Write(0u); writer.Write(declaredSize ?? (uint)plain.Length); writer.Write((uint)size); writer.Write(md5);
        var indexBytes = index.ToArray();
        new Snow2Cipher(PknFormat.IndexKey(Uri)).Encrypt(indexBytes);
        var bytes = new byte[1024 + payload.Length];
        var offset = PknFormat.HeaderOffset(Uri);
        headerBytes.CopyTo(bytes, offset);
        indexBytes.CopyTo(bytes, offset + headerBytes.Length + 42);
        payload.CopyTo(bytes, 1024);
        return bytes;
    }
}
