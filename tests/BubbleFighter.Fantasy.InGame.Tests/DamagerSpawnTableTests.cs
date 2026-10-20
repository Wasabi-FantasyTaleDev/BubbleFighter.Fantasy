using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>
/// Pins the seven in-round damager spawn bodies (32772, 32877, 32881, 32898, 32899, 32910, 33278) per build: the fixed
/// part has the width the client's reader consumes, and kr-1256 alone appends a counted word list to four of them
/// (kr-1256 sub_1137C30 reads it; the writers call kr-1256 sub_10F4640 or write the count inline).
/// </summary>
public sealed class DamagerSpawnTableTests
{
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

    /// <summary>Structure, wire widths in order, index of the damager counter field (-1 when the body has none), list field name (null when no build has a list).</summary>
    public static TheoryData<string, int[], int, string?> Spawns => new()
    {
        { "SplashDamager", [4, 2, 1, .. Repeat(4, 11)], 12, "unknown_56" },
        { "MobDropBombOnDead", [4, 4, 1, .. Repeat(4, 8)], 9, null },
        { "DropHumilPrevItem", [4, 4, 4], -1, "unknown_16" },
        { "FireBullet3", [4, 2, 1, .. Repeat(4, 7), 1, 4, 4], 11, "unknown_52" },
        { "StormDamager", [4, 2, 1, 4, 4, 4], 4, null },
        { "StormDamagerItemId", [4, 2, 1, 4, 4, 4], 5, null },
        { "NearDamager", [4, 2, 1, 4, 4], 3, "unknown_20" },
    };

    private static int[] Repeat(int width, int count) => [.. Enumerable.Repeat(width, count)];

    // Field i carries the value 0x10 + i, so a shifted or swapped field shows up as a wrong value.
    private static byte[] Body(int[] widths)
    {
        var bytes = new List<byte>();
        for (var i = 0; i < widths.Length; i++)
            bytes.AddRange(BitConverter.GetBytes(0x10 + i)[..widths[i]]);
        return [.. bytes];
    }

    private static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(part => part)];

    private static byte[] Words(int count, int first = 0x7000) =>
        [.. Enumerable.Range(0, count).SelectMany(i => BitConverter.GetBytes(first + i))];

    /// <summary>The fixed part reads exactly the audited width in every build that has no list, and writes back byte for byte.</summary>
    [Theory]
    [MemberData(nameof(Spawns))]
    public void FixedPartIsExactInBuildsWithoutAList(string structure, int[] widths, int counter, string? listField)
    {
        var body = Body(widths);
        string[] builds = listField is null ? ["kr-812", "kr-1256", "cn-774", "tw-454"] : ["kr-812", "cn-774", "tw-454"];
        foreach (var build in builds)
        {
            var decoded = PacketCodec.Read(Tables.Value, build, structure, body);
            Assert.Equal(0x10, decoded["character_key"]);
            if (counter >= 0)
                Assert.Equal(0x10 + counter, decoded["damager_key"]);
            Assert.Equal(body, PacketCodec.Write(Tables.Value, build, structure, decoded));
            Assert.Throws<PacketFormatException>(() => PacketCodec.Read(Tables.Value, build, structure, body[..^1]));
            Assert.Throws<PacketFormatException>(() => PacketCodec.Read(Tables.Value, build, structure, Concat(body, Words(1))));
        }
    }

    /// <summary>The storm damager pair shares one width but not one meaning: only the item variant names the looked-up word.</summary>
    [Fact]
    public void StormDamagerItemIdNamesTheItemWord()
    {
        var body = Body([4, 2, 1, 4, 4, 4]);
        var decoded = PacketCodec.Read(Tables.Value, "kr-1256", "StormDamagerItemId", body);
        Assert.Equal(0x14, decoded["item_id"]);
        Assert.Equal(0x15, decoded["damager_key"]);
        Assert.False(PacketCodec.Read(Tables.Value, "kr-1256", "StormDamager", body).ContainsKey("item_id"));
    }

    /// <summary>kr-1256 reads an empty and a nonempty list after the fixed part and writes both back byte for byte.</summary>
    [Theory]
    [MemberData(nameof(Spawns))]
    public void Kr1256ReadsTheCountedList(string structure, int[] widths, int counter, string? listField)
    {
        if (listField is null)
            return;
        foreach (var entries in new[] { 0, 3 })
        {
            var body = Concat(Body(widths), BitConverter.GetBytes(entries), Words(entries));
            Assert.Equal(widths.Sum() + 4 + 4 * entries, body.Length);
            var decoded = PacketCodec.Read(Tables.Value, "kr-1256", structure, body);
            Assert.Equal(0x10, decoded["character_key"]);
            if (counter >= 0)
                Assert.Equal(0x10 + counter, decoded["damager_key"]);
            Assert.Equal(entries, Assert.IsAssignableFrom<System.Collections.ICollection>(decoded[listField]).Count);
            Assert.Equal(body, PacketCodec.Write(Tables.Value, "kr-1256", structure, decoded));
        }
    }

    /// <summary>A kr-1256 body without its count, with fewer words than counted, with a huge count or with trailing bytes is refused.</summary>
    [Theory]
    [MemberData(nameof(Spawns))]
    public void Kr1256RefusesMissingTruncatedOversizedAndTrailingLists(string structure, int[] widths, int counter, string? listField)
    {
        if (listField is null)
            return;
        var fixedPart = Body(widths);
        Assert.InRange(counter, -1, widths.Length - 1);
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(Tables.Value, "kr-1256", structure, fixedPart));
        Assert.Throws<PacketFormatException>(() =>
            PacketCodec.Read(Tables.Value, "kr-1256", structure, Concat(fixedPart, BitConverter.GetBytes(2), Words(1))));
        Assert.Throws<PacketFormatException>(() =>
            PacketCodec.Read(Tables.Value, "kr-1256", structure, Concat(fixedPart, BitConverter.GetBytes(0x3FFFFFFF))));
        Assert.Throws<PacketFormatException>(() =>
            PacketCodec.Read(Tables.Value, "kr-1256", structure, Concat(fixedPart, BitConverter.GetBytes(0), Words(1))));
    }
}
