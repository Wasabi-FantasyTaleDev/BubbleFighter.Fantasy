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

    /// <summary>The later matching reply retains its string and the earlier replies omit it.</summary>
    [Theory]
    [InlineData("kr-812", 16)]
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
}
