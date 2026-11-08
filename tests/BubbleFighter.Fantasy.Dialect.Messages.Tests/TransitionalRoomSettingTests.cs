using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the intermediate clients' complete game settings, including nonempty native option vectors.</summary>
public sealed class TransitionalRoomSettingTests
{
    // cn-735 sub_424D40, cn-738 sub_4288C0 and cn-742 sub_42C270 consume this field sequence.
    private const string Body = "010000000200000003000000010000000000000001000000"
        + "0400000005000000060000000700000008000000"
        + "01000000410001000000420001000000430001000000440009000000"
        + "010000004500010000004600010000004700010000004800010000004900010000004A00"
        + "0A0000000B00000001000000010000000C0000000D000000"
        + "00000000010000000E000000010000004B00"
        + "010000000000000001000000000000000100000000000000010000000F01000000"
        + "02000000010000004C00010000004D00"
        + "100000001100000012000000010000004E00010000004F0013000000";

    [Theory]
    [InlineData("cn-735")]
    [InlineData("cn-738")]
    [InlineData("cn-742")]
    public void NonemptyOptionVectorsRetainTheirNativeElementWidths(string build)
    {
        var body = Convert.FromHexString(Body);
        Assert.Equal(227, body.Length);
        Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, build, "gspRoomChangeGameSetting",
            new Dictionary<string, object?> { ["game_setting"] = PacketCodec.Read(TestBuilds.Packets, build, "GameSetting", body) }));
        var fields = PacketCodec.Read(TestBuilds.Packets, build, "GameSetting", body);
        Assert.Equal(1, fields["mode"]);
        Assert.Equal(3, fields["unknown_12"]);
        Assert.Equal("A", fields["unknown_40"]);
        Assert.Equal("D", fields["unknown_132"]);
        Assert.Equal("J", fields["unknown_324"]);
        var pair = Assert.IsType<Dictionary<string, object?>>(Assert.Single(Assert.IsType<object?[]>(fields["unknown_365"])));
        Assert.Equal(12, pair["unknown_0"]);
        Assert.Equal(13, pair["unknown_4"]);
        var keyed = Assert.IsType<Dictionary<string, object?>>(Assert.Single(Assert.IsType<object?[]>(fields["unknown_382"])));
        Assert.Equal(14, keyed["unknown_0"]);
        Assert.Equal("K", keyed["unknown_4"]);
        Assert.Equal((byte)15, fields["unknown_412"]);
        Assert.Equal(new object?[] { "L", "M" }, fields["unknown_420"]);
        Assert.Equal(19, fields["unknown_504"]);
        Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, build, "GameSetting", fields));
        var (schema, _) = TestBuilds.Of(build);
        var session = RoomCodec.WriteSessionEnvelope(TestBuilds.Packets, build, schema,
            new RoomValues(RoomCodec.PlayRoomKind, []) { Play = true, SessionState = 2, GameSetting = body });
        var sessionFields = PacketCodec.Read(TestBuilds.Packets, build, "SessionSetting", session);
        var nested = Assert.IsType<Dictionary<string, object?>>(sessionFields["game_setting"]);
        Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, build, "GameSetting", nested));
        // The native creator stops after its password and may retain nonempty option vectors.
        byte[] create = [.. session, 1, 0, 0, 0, (byte)'P', 0];
        var request = PlazaRoomWire.ReadCreateRoom(create, schema);
        Assert.Equal("P", request.Password);
        Assert.Equal(1, request.Mode);
        Assert.Equal(body, request.GameSetting);
        Assert.False(request.Unknown0);
        // cn-735 sub_424D40 and its successors accept any nonzero bool32; preserve those original bytes.
        create[28 + 12] = 7;
        var retained = body.ToArray();
        retained[12] = 7;
        Assert.Equal(retained, PlazaRoomWire.ReadCreateRoom(create, schema).GameSetting);
        for (var length = 0; length < create.Length; length++)
            Assert.Throws<PacketFormatException>(() => PlazaRoomWire.ReadCreateRoom(create[..length], schema));
        Assert.Throws<PacketFormatException>(() => PlazaRoomWire.ReadCreateRoom([.. create, 0, 0, 0, 0], schema));
        for (var length = 0; length < body.Length; length++)
            Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, build, "GameSetting", body[..length]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, build, "GameSetting", [.. body, 0]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, build, "GameSetting", body,
            new PacketReadLimits(MaxCollectionElements: 1)));
    }
}
