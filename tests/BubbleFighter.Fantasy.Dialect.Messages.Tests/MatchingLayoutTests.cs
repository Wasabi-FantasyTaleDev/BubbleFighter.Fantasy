using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the native matching envelopes without assigning unproven gameplay meanings.</summary>
public sealed class MatchingLayoutTests
{
    /// <summary>Empty-string requests retain each native reader's word, flag and short widths.</summary>
    [Theory]
    [InlineData("kr-812", 40)]
    [InlineData("cn-717", 36)]
    [InlineData("id-400", 34)]
    [InlineData("id-1809", 36)]
    [InlineData("cn-706", 34)]
    [InlineData("cn-324", 12)]
    [InlineData("cn-279", 8)]
    [InlineData("cn-406", 24)]
    public void NativeRequestLengthIsExact(string build, int length)
    {
        var body = new byte[length];
        var values = PacketCodec.ReadScalars(TestBuilds.Packets, build, "RoomMatchRequest", body);
        Assert.NotEmpty(values);
        Assert.Throws<PacketFormatException>(() =>
            PacketCodec.ReadScalars(TestBuilds.Packets, build, "RoomMatchRequest", body[..^1]));
        Assert.Throws<PacketFormatException>(() =>
            PacketCodec.ReadScalars(TestBuilds.Packets, build, "RoomMatchRequest", new byte[length + 1]));
    }

    /// <summary>Notifications differ independently from requests, including the older two-word reply.</summary>
    [Theory]
    [InlineData("kr-812", 12)]
    [InlineData("kr-1256", 12)]
    [InlineData("cn-717", 12)]
    [InlineData("id-400", 8)]
    [InlineData("id-1809", 12)]
    [InlineData("cn-706", 12)]
    [InlineData("cn-324", 4)]
    [InlineData("cn-279", 4)]
    [InlineData("cn-406", 8)]
    public void NativeNotificationLengthIsExact(string build, int length)
    {
        var body = new byte[length];
        var values = PacketCodec.ReadScalars(TestBuilds.Packets, build, "RoomMatchNotification", body);
        Assert.NotEmpty(values);
        Assert.Throws<PacketFormatException>(() =>
            PacketCodec.ReadScalars(TestBuilds.Packets, build, "RoomMatchNotification", body[..^1]));
        Assert.Throws<PacketFormatException>(() =>
            PacketCodec.ReadScalars(TestBuilds.Packets, build, "RoomMatchNotification", new byte[length + 1]));
    }

    /// <summary>The kr-1256 mystery-store request carries a type byte, a u32 and a per-type tail through one
    /// serializer (kr-1256 sub_114E2E0), so each type has its own exact body length.</summary>
    [Theory]
    [InlineData("UserMysteryStoreRequest", 5, 1)]
    [InlineData("UserMysteryStoreRequestType2", 7, 2)]
    [InlineData("UserMysteryStoreRequestType3", 7, 3)]
    [InlineData("UserMysteryStoreRequestType4", 11, 4)]
    public void Kr1256MysteryStoreRequestTypeLengthsAreExact(string structure, int length, int type)
    {
        var body = new byte[length];
        body[0] = (byte)type;
        var values = PacketCodec.ReadScalars(TestBuilds.Packets, "kr-1256", structure, body);
        Assert.Equal((byte)type, Assert.IsType<byte>(values["type"]));
        Assert.Throws<PacketFormatException>(() =>
            PacketCodec.ReadScalars(TestBuilds.Packets, "kr-1256", structure, body[..^1]));
        Assert.Throws<PacketFormatException>(() =>
            PacketCodec.ReadScalars(TestBuilds.Packets, "kr-1256", structure, new byte[length + 1]));
    }

    /// <summary>The type-4 mystery-store body writes the type byte, the u32 words and the u16 unaligned,
    /// in the serializer's order.</summary>
    [Fact]
    public void Kr1256MysteryStoreType4WritesFieldsInWireOrder()
    {
        var body = PacketCodec.Write(TestBuilds.Packets, "kr-1256", "UserMysteryStoreRequestType4",
            new Dictionary<string, object?>
            {
                ["type"] = 4, ["token"] = 7, ["stock_or_stock_set_id"] = 0x1234, ["coin_price"] = 5,
            });
        Assert.Equal(11, body.Length);
        Assert.Equal(4, body[0]);
        Assert.Equal(7, BitConverter.ToInt32(body, 1));
        Assert.Equal(0x1234, BitConverter.ToInt32(body, 5));
        Assert.Equal(5, BitConverter.ToUInt16(body, 9));
    }

    /// <summary>The later matching reply retains its string and the earlier replies omit it.</summary>
    [Theory]
    [InlineData("kr-812", 16)]
    [InlineData("kr-1256", 16)]
    [InlineData("cn-717", 16)]
    [InlineData("id-1809", 16)]
    [InlineData("cn-706", 16)]
    [InlineData("id-400", 8)]
    [InlineData("cn-324", 4)]
    [InlineData("cn-279", 4)]
    [InlineData("cn-406", 8)]
    public void NotificationWriterUsesTheNativeTableWithoutASeparateShapeEnum(string build, int length)
    {
        var body = PacketCodec.Write(TestBuilds.Packets, build, "RoomMatchNotification",
            new Dictionary<string, object?>
            {
                ["unknown_8"] = 2, ["unknown_12"] = "ab", ["result"] = 1,
            });
        Assert.Equal(length, body.Length);
        Assert.Equal(1, BitConverter.ToInt32(body));
        if (length >= 8)
            Assert.Equal(2, BitConverter.ToInt32(body, 4));
        if (length == 16)
            Assert.Equal(Convert.FromHexString("0200000061006200"), body[8..]);
    }

    /// <summary>The cn-774 lobby-minigame request bodies keep their subcommand-first lengths;
    /// tw-454 follows the same shapes through builds.yaml.</summary>
    [Theory]
    [InlineData("LmgBasketballRequest", 12, 1)]
    [InlineData("LmgFishingStartRequest", 14, 10)]
    [InlineData("LmgRodLiftRequest", 18, 11)]
    [InlineData("LmgFortuneRequest", 16, 12)]
    [InlineData("LmgSyncAction", 6, 100)]
    public void ModernLmgRequestLengthsAreExact(string structure, int length, int subcmd)
    {
        var body = new byte[length];
        body[0] = (byte)subcmd;
        body[1] = (byte)(subcmd >> 8);
        foreach (var build in new[] { "kr-1256", "cn-774", "tw-454" })
        {
            var values = PacketCodec.ReadScalars(TestBuilds.Packets, build, structure, body);
            Assert.Equal(subcmd, Convert.ToInt32(values["subcmd"]));
            Assert.Throws<PacketFormatException>(() =>
                PacketCodec.ReadScalars(TestBuilds.Packets, build, structure, body[..^1]));
            Assert.Throws<PacketFormatException>(() =>
                PacketCodec.ReadScalars(TestBuilds.Packets, build, structure, new byte[length + 1]));
        }
    }

    /// <summary>The fishing-start request writes its words in the packed writer's order.</summary>
    [Theory]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void ModernLmgFishingStartWritesItsWordsInWireOrder(string build)
    {
        var body = PacketCodec.Write(TestBuilds.Packets, build, "LmgFishingStartRequest",
            new Dictionary<string, object?>
            {
                ["subcmd"] = 10, ["second_price"] = 1, ["unknown_10"] = 0, ["displayed_cost"] = 2, ["completed_count"] = 3,
            });
        Assert.Equal(14, body.Length);
        Assert.Equal(10, BitConverter.ToUInt16(body, 0));
        Assert.Equal(1, BitConverter.ToInt32(body, 2));
        Assert.Equal(2, BitConverter.ToUInt16(body, 10));
        Assert.Equal(3, BitConverter.ToUInt16(body, 12));
    }

    /// <summary>The minigame state body carries its word before its short.</summary>
    [Fact]
    public void Cn774LmgSyncStateWritesWordThenShort()
    {
        var body = PacketCodec.Write(TestBuilds.Packets, "cn-774", "LmgSyncState",
            new Dictionary<string, object?> { ["unknown_4"] = 0x1234, ["unknown_8"] = 5 });
        Assert.Equal(6, body.Length);
        Assert.Equal(0x1234, BitConverter.ToInt32(body, 0));
        Assert.Equal(5, BitConverter.ToUInt16(body, 4));
    }
}
