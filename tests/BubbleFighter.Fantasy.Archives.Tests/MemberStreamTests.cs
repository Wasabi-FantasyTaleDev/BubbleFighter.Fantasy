using System.Text;
using BubbleFighter.Fantasy.Archives;
using Xunit;

namespace BubbleFighter.Fantasy.Archives.Tests;

/// <summary>Members read as a stream: the same bytes as a whole read, the same checks, and nothing unverified.</summary>
public sealed class MemberStreamTests
{
    private const string PackUri = "packs/test_00000.pkn";
    private const string ArchivePath = "Config/Config.bfz";

    // Larger than one pack chunk and one inflate buffer, partly compressible and partly not.
    private static byte[] Large()
    {
        var data = new byte[300_007];
        new Random(20261111).NextBytes(data.AsSpan(0, 150_000));
        return data;
    }

    private static List<(string Path, byte[] Data)> Members() =>
    [
        ("Config/A.xml", Encoding.UTF8.GetBytes("<a>" + new string('x', 5000) + "</a>")),
        ("Config/Empty.ini", []),
        ("Config/Odd.bin", Enumerable.Range(0, 2051).Select(i => (byte)i).ToArray()),
        ("Config/Large.bin", Large()),
    ];

    private static byte[] Pack(bool compress, bool encrypt, List<(string Path, byte[] Data)>? members = null)
    {
        var buffer = new MemoryStream();
        PknWriter.Write(buffer, PackUri, members ?? Members(), compress, encrypt);
        return buffer.ToArray();
    }

    private static byte[] Zip(List<(string Path, byte[] Data)>? members = null)
    {
        var buffer = new MemoryStream();
        BfzArchive.Write(buffer, ArchivePath, members ?? Members());
        return buffer.ToArray();
    }

    // Reads in pieces of changing size, so chunk and buffer borders fall everywhere.
    private static byte[] Drain(Stream stream)
    {
        var result = new MemoryStream();
        var buffer = new byte[70_001];
        int[] sizes = [1, 7, 4096, 70_001, 333];
        for (var turn = 0; ; turn++)
        {
            var got = stream.Read(buffer, 0, sizes[turn % sizes.Length]);
            if (got == 0)
                return result.ToArray();
            result.Write(buffer, 0, got);
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void APackMemberStreamsToTheBytesItReadsWhole(bool compress, bool encrypt)
    {
        using var pack = PknArchive.Open(new MemoryStream(Pack(compress, encrypt)), PackUri);
        foreach (var (path, data) in Members())
        {
            Assert.Equal(data.LongLength, pack.Length(path.ToUpperInvariant().Replace('/', '\\')));
            using var stream = pack.OpenRead(path);
            Assert.Equal(data.LongLength, stream.Length);
            Assert.True(data.AsSpan().SequenceEqual(Drain(stream)), path);
            Assert.True(data.AsSpan().SequenceEqual(pack.Read(path)), path);
        }
    }

    [Fact]
    public void AnArchiveMemberStreamsToTheBytesItReadsWhole()
    {
        using var archive = BfzArchive.Open(new MemoryStream(Zip()), ArchivePath);
        foreach (var (path, data) in Members())
        {
            Assert.Equal(data.LongLength, archive.Length(path));
            using var stream = archive.OpenRead(path.ToLowerInvariant());
            Assert.True(data.AsSpan().SequenceEqual(Drain(stream)), path);
            Assert.True(data.AsSpan().SequenceEqual(archive.Read(path)), path);
        }
    }

    [Fact]
    public void TwoStreamsOfOneArchiveCanBeReadInTurns()
    {
        using var archive = BfzArchive.Open(new MemoryStream(Zip()), ArchivePath);
        using var first = archive.OpenRead("Config/Large.bin");
        using var second = archive.OpenRead("Config/A.xml");
        var a = new MemoryStream();
        var b = new MemoryStream();
        var buffer = new byte[1000];
        for (int gotA = 1, gotB = 1; gotA > 0 || gotB > 0;)
        {
            gotA = first.Read(buffer);
            a.Write(buffer, 0, gotA);
            gotB = second.Read(buffer);
            b.Write(buffer, 0, gotB);
        }
        Assert.True(Large().AsSpan().SequenceEqual(a.ToArray()));
        Assert.Equal(Members()[0].Data, b.ToArray());
    }

    [Fact]
    public void AMissingMemberIsReportedByLengthAndOpen()
    {
        using var pack = PknArchive.Open(new MemoryStream(Pack(true, true)), PackUri);
        using var archive = BfzArchive.Open(new MemoryStream(Zip()), ArchivePath);
        Assert.Throws<FileNotFoundException>(() => pack.Length("Config/Missing.xml"));
        Assert.Throws<FileNotFoundException>(() => pack.OpenRead("Config/Missing.xml"));
        Assert.Throws<FileNotFoundException>(() => archive.Length("Config/Missing.xml"));
        Assert.Throws<FileNotFoundException>(() => archive.OpenRead("Config/Missing.xml"));
    }

    /// <summary>The read that would deliver the last byte throws, so a reader never sees a damaged member end normally.</summary>
    [Fact]
    public void ADamagedPackMemberFailsBeforeItsLastByteIsAccepted()
    {
        List<(string Path, byte[] Data)> members = [("a.bin", Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray())];
        var bytes = Pack(compress: false, encrypt: false, members);
        using (var probe = PknArchive.Open(new MemoryStream(bytes), PackUri))
            bytes[probe.DataOffset + 4990] ^= 0xFF;
        using var pack = PknArchive.Open(new MemoryStream(bytes), PackUri);

        var stream = pack.OpenRead("a.bin");
        var buffer = new byte[4000];
        Assert.Equal(4000, stream.Read(buffer));
        var error = Assert.Throws<InvalidDataException>(() => stream.Read(buffer));
        Assert.Contains("MD5", error.Message);
        // The failure was reported by the read; disposal does not report it a second time.
        stream.Dispose();
    }

    [Fact]
    public void ADamagedArchiveMemberCannotBeReadToItsEnd()
    {
        List<(string Path, byte[] Data)> members = [("Config/A.bin", Large())];
        var bytes = Zip(members);
        // Inside the stored data, well past the local header and the cipher header.
        bytes[2000] ^= 0x55;
        using var archive = BfzArchive.Open(new MemoryStream(bytes), ArchivePath);
        Assert.Throws<InvalidDataException>(() =>
        {
            using var stream = archive.OpenRead("Config/A.bin");
            Drain(stream);
        });
        Assert.Throws<InvalidDataException>(() => archive.Read("Config/A.bin"));
    }

    /// <summary>A reader that stops early has not seen the checksum; disposal reads the rest and reports damage.</summary>
    [Fact]
    public void AStreamLeftEarlyIsCheckedWhenItIsDisposed()
    {
        List<(string Path, byte[] Data)> members = [("a.bin", Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray())];
        var good = Pack(compress: false, encrypt: false, members);
        var damaged = good.ToArray();
        using (var probe = PknArchive.Open(new MemoryStream(good), PackUri))
            damaged[probe.DataOffset + 4990] ^= 0xFF;

        using (var pack = PknArchive.Open(new MemoryStream(good), PackUri))
        {
            var stream = pack.OpenRead("a.bin");
            Assert.Equal(10, stream.Read(new byte[10]));
            stream.Dispose();
        }
        using (var pack = PknArchive.Open(new MemoryStream(damaged), PackUri))
        {
            var stream = pack.OpenRead("a.bin");
            Assert.Equal(10, stream.Read(new byte[10]));
            Assert.Throws<InvalidDataException>(stream.Dispose);
            // A second disposal is quiet.
            stream.Dispose();
        }
    }

    [Theory]
    [InlineData(3, "ends after")]
    [InlineData(5, "expands beyond")]
    public void AMemberOfTheWrongLengthIsRefused(int actual, string message)
    {
        var data = new byte[actual];
        var declared = new byte[4];
        using var stream = new VerifiedMemberStream(new MemoryStream(data), declared.Length,
            new Crc32Checksum(ZipCrypto.Crc32(declared)), "member");
        var error = Assert.Throws<InvalidDataException>(() => Drain(stream));
        Assert.Contains(message, error.Message);
    }

    [Fact]
    public void AnEmptyMemberIsVerifiedByItsFirstRead()
    {
        using var wrong = new VerifiedMemberStream(new MemoryStream(), 0, new Crc32Checksum(1), "member");
        Assert.Throws<InvalidDataException>(() => wrong.Read(new byte[8]));
        using var right = new VerifiedMemberStream(new MemoryStream(), 0, new Crc32Checksum(ZipCrypto.Crc32([])), "member");
        Assert.Equal(0, right.Read(new byte[8]));
        Assert.Equal(0, right.Read(new byte[8]));
    }

    [Fact]
    public void ClosingTheArchiveEndsItsOpenStreams()
    {
        var pack = PknArchive.Open(new MemoryStream(Pack(true, true)), PackUri);
        var stream = pack.OpenRead("Config/Large.bin");
        Assert.Equal(100, stream.Read(new byte[100]));
        pack.Dispose();
        Assert.Throws<ObjectDisposedException>(() => Drain(stream));
    }

    /// <summary>The index of a pack with many members reads back whole across the reader's block borders.</summary>
    [Fact]
    public void AnIndexLargerThanOneReadBlockIsReadCompletely()
    {
        var members = Enumerable.Range(0, 2500)
            .Select(i => ($"Config/Folder{i % 37}/Some.Longer.Member.Name.{i:D5}.xml", Encoding.UTF8.GetBytes("m" + i)))
            .ToList();
        using var pack = PknArchive.Open(new MemoryStream(Pack(true, true, members)), PackUri);
        Assert.Equal(members.Select(m => m.Item1), pack.Entries.Select(e => e.Path));
        Assert.Equal("m2499", Encoding.UTF8.GetString(pack.Read(members[^1].Item1)));
        Assert.Equal("m0", Encoding.UTF8.GetString(pack.Read(members[0].Item1)));
    }
}
