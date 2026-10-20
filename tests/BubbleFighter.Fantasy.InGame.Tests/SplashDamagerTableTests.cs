using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>
/// Pins the splash damager body (in-round 32900) per build: twelve words everywhere, and in kr-1256 a counted word
/// list after them (kr-1256 sub_708820 / sub_10F4640 write it, sub_6CAB40 / sub_1137C30 read it).
/// </summary>
public sealed class SplashDamagerTableTests
{
    private const string Structure = "SplashDamagerByDamager";
    private static readonly Lazy<PacketTables> Tables = new(() => PacketTables.Load(PacketsDirectory()));

    private static string PacketsDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "packets");
            if (File.Exists(Path.Combine(candidate, "builds.yaml")))
                return candidate;
        }
        throw new InvalidOperationException("The repository's packets directory was not found above the test output folder.");
    }

    // Twelve distinct words in wire order, so a shifted field shows up as a wrong value.
    private static byte[] Words(int count, int first = 0x01000001)
    {
        var bytes = new byte[count * 4];
        for (var i = 0; i < count; i++)
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), first + i);
        return bytes;
    }

    private static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(part => part)];

    /// <summary>The legacy baseline and the two later modern builds carry exactly twelve words.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void TwelveWordBuildsReadExactlyFortyEightBytes(string build)
    {
        var body = Words(12);
        var decoded = PacketCodec.Read(Tables.Value, build, Structure, body);
        Assert.Equal(0x01000001, decoded["source_damager_key"]);
        Assert.Equal(0x0100000B, decoded["damager_key"]);
        Assert.Equal(body, PacketCodec.Write(Tables.Value, build, Structure, decoded));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(Tables.Value, build, Structure, body[..^1]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(Tables.Value, build, Structure, Concat(body, Words(1))));
    }

    /// <summary>kr-1256 accepts an empty and a nonempty list and writes both back byte for byte.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Kr1256ReadsTheCountedListAfterTheTwelveWords(int entries)
    {
        var count = BitConverter.GetBytes(entries);
        var body = Concat(Words(12), count, Words(entries, 0x7000));
        Assert.Equal(52 + 4 * entries, body.Length);
        var decoded = PacketCodec.Read(Tables.Value, "kr-1256", Structure, body);
        Assert.Equal(0x01000001, decoded["source_damager_key"]);
        Assert.Equal(0x0100000B, decoded["damager_key"]);
        Assert.Equal(entries, Assert.IsAssignableFrom<System.Collections.ICollection>(decoded["unknown_48"]).Count);
        Assert.Equal(body, PacketCodec.Write(Tables.Value, "kr-1256", Structure, decoded));
    }

    /// <summary>A kr-1256 body without its count, with fewer words than counted, or with a huge count is refused.</summary>
    [Fact]
    public void Kr1256RefusesMissingTruncatedAndOversizedLists()
    {
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(Tables.Value, "kr-1256", Structure, Words(12)));
        var truncated = Concat(Words(12), BitConverter.GetBytes(2), Words(1, 0x7000));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(Tables.Value, "kr-1256", Structure, truncated));
        var oversized = Concat(Words(12), BitConverter.GetBytes(0x3FFFFFFF));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(Tables.Value, "kr-1256", Structure, oversized));
        var trailing = Concat(Words(12), BitConverter.GetBytes(0), Words(1));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(Tables.Value, "kr-1256", Structure, trailing));
    }
}
