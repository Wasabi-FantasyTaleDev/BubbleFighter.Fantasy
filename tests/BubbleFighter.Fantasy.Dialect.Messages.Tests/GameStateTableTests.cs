using System.Globalization;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the legacy game-state broadcasts and the opcode rows that state their numbers.</summary>
public sealed class GameStateTableTests
{
    private static readonly Dictionary<string, object?> Empty = new();

    /// <summary>The score body is one u32 count followed by nine-byte player rows.</summary>
    [Fact]
    public void ScoreBodyIsACountedListOfNineByteRows()
    {
        var values = new Dictionary<string, object?>
        {
            ["players"] = new object?[]
            {
                new Dictionary<string, object?>
                {
                    ["unknown_4"] = (byte)7,
                    ["unknown_6"] = (ushort)0x1234,
                    ["unknown_8"] = (ushort)0x5678,
                    ["unknown_10"] = (ushort)0x9ABC,
                    ["unknown_12"] = (ushort)0xDEF0,
                },
            },
        };
        var body = PacketCodec.Write(TestBuilds.Packets, "kr-812", "GameScoreData", values);
        Assert.Equal(Convert.FromHexString("010000000734127856BC9AF0DE"), body);

        var read = PacketCodec.Read(TestBuilds.Packets, "kr-812", "GameScoreData", body);
        var players = Assert.IsType<object?[]>(read["players"]);
        var row = Assert.IsType<Dictionary<string, object?>>(Assert.Single(players));
        Assert.Equal(7, Convert.ToInt32(row["unknown_4"], CultureInfo.InvariantCulture));
        Assert.Equal(0x1234, Convert.ToInt32(row["unknown_6"], CultureInfo.InvariantCulture));
        Assert.Equal(0x5678, Convert.ToInt32(row["unknown_8"], CultureInfo.InvariantCulture));
        Assert.Equal(0x9ABC, Convert.ToInt32(row["unknown_10"], CultureInfo.InvariantCulture));
        Assert.Equal(0xDEF0, Convert.ToInt32(row["unknown_12"], CultureInfo.InvariantCulture));
        RejectIncompleteAndTrailing("kr-812", "GameScoreData", body);
    }

    /// <summary>The item-spec body is one u32 count followed by eight-byte drop entries.</summary>
    /// <param name="build">A build whose item-spec reader was read.</param>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-406")]
    public void ItemSpecBodyIsACountedListOfEightByteDrops(string build)
    {
        var values = new Dictionary<string, object?>
        {
            ["entries"] = new object?[]
            {
                new Dictionary<string, object?>
                {
                    ["unknown_4"] = 202003,
                    ["unknown_8"] = 1,
                },
            },
        };
        var body = PacketCodec.Write(TestBuilds.Packets, build, "GameItemSpec", values);
        Assert.Equal(Convert.FromHexString("010000001315030001000000"), body);

        var read = PacketCodec.Read(TestBuilds.Packets, build, "GameItemSpec", body);
        var entries = Assert.IsType<object?[]>(read["entries"]);
        var entry = Assert.IsType<Dictionary<string, object?>>(Assert.Single(entries));
        Assert.Equal(202003, Convert.ToInt32(entry["unknown_4"], CultureInfo.InvariantCulture));
        Assert.Equal(1, Convert.ToInt32(entry["unknown_8"], CultureInfo.InvariantCulture));
        RejectIncompleteAndTrailing(build, "GameItemSpec", body);
    }

    /// <summary>Legacy builds inherit the shapes; unverified modern bodies remain excluded.</summary>
    [Fact]
    public void OnlyLegacyBuildsResolveTheLegacyShapes()
    {
        foreach (var build in new[] { "kr-812", "cn-406", "cn-717" })
        {
            Assert.Equal(4, PacketCodec.Write(TestBuilds.Packets, build, "GameScoreData", Empty).Length);
            Assert.Equal(9, PacketCodec.Write(TestBuilds.Packets, build, "PlayerGameScoreData", Empty).Length);
            Assert.Equal(4, PacketCodec.Write(TestBuilds.Packets, build, "GameItemSpec", Empty).Length);
            Assert.Equal(8, PacketCodec.Write(TestBuilds.Packets, build, "ItemDropInfo", Empty).Length);
        }
    }

    /// <summary>The item-spec mapping follows its dispatcher and class construction.</summary>
    [Fact]
    public void TheMissingRowsStateTheLegacyNames()
    {
        Assert.Equal((ushort)748, TestBuilds.Opcodes("cn-406").S2C("gspGameItemSpec"));
        Assert.Equal((ushort)748, TestBuilds.Opcodes("kr-812").S2C("gspGameItemSpec"));
        Assert.Equal((ushort)989, TestBuilds.Opcodes("kr-812").S2C("gspGameScoreData"));
    }

    private static void RejectIncompleteAndTrailing(string build, string structure, byte[] body)
    {
        for (var length = 0; length < body.Length; length++)
        {
            Assert.Throws<PacketFormatException>(() =>
                PacketCodec.Read(TestBuilds.Packets, build, structure, body[..length]));
        }
        Assert.Throws<PacketFormatException>(() =>
            PacketCodec.Read(TestBuilds.Packets, build, structure, [.. body, 0]));
    }
}
