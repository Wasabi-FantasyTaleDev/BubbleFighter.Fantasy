using System.Buffers;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Dialect.Rooms;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins native room-start request widths independently of the packet-table reader.</summary>
public sealed class RoomStartRequestTests
{
    [Theory]
    [InlineData("kr-812", true)]
    [InlineData("cn-717", true)]
    [InlineData("cn-324", false)]
    [InlineData("cn-279", false)]
    [InlineData("id-400", false)]
    [InlineData("id-1809", true)]
    [InlineData("cn-706", false)]
    [InlineData("cn-406", false)]
    public void NativeMapChangePreservesItsFieldsAndRequiresTheBuildTail(string build, bool trailingFlag)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new PacketWriter(buffer);
        writer.WriteWideStringPrefixed("Map label");
        writer.WriteWideStringPrefixed("rel_zombie02_Twobridge");
        writer.WriteWideStringPrefixed("All");
        writer.WriteWideStringPrefixed("Random");
        writer.WriteBool32(true);
        writer.WriteBool32(false);
        for (var i = 0; i < 8; i++)
            writer.WriteInt32(1000 + i);
        writer.WriteInt32(16);
        writer.WriteInt32(3);
        if (trailingFlag)
            writer.WriteBool32(true);
        var body = buffer.WrittenSpan.ToArray();
        Assert.Equal(64 + (trailingFlag ? 4 : 0) + 2 * (9 + 22 + 3 + 6), body.Length);

        Assert.Equal(new GameSettingChange("Map label", "rel_zombie02_Twobridge", "All", "Random",
            true, false, 16, 3), PlazaRoomWire.ReadChangeGameSetting(TestBuilds.Packets, build, body));
        var typed = PlayRoomCodec.ReadSettingsChange(TestBuilds.Packets, build, body);
        Assert.Equal(16, typed.ModeId);
        Assert.Equal("Random", typed.MapKey);
        Assert.Equal(build, typed.BuildSettings!.Lineage);
        Assert.True(typed.BuildSettings.Bytes.IsEmpty);
        Assert.Equal(body, typed.BuildSettings.Change.ToArray());
        Assert.Throws<PacketFormatException>(() =>
            PlazaRoomWire.ReadChangeGameSetting(TestBuilds.Packets, build, body.AsSpan(0, body.Length - 1)));
        Assert.Throws<PacketFormatException>(() =>
            PlazaRoomWire.ReadChangeGameSetting(TestBuilds.Packets, build, [.. body, 0, 0, 0, 0]));
    }

    [Theory]
    [InlineData("kr-812", 6)]
    [InlineData("cn-717", 6)]
    [InlineData("cn-324", 4)]
    [InlineData("cn-279", 4)]
    [InlineData("id-400", 5)]
    [InlineData("cn-406", 5)]
    [InlineData("id-1809", 6)]
    [InlineData("cn-706", 6)]
    public void NativeStartBodyPreservesMapAndWordAndRequiresTheWholeMetaBlock(string build, int boolCount)
    {
        const string key = "rel_Village01_Byway";
        var body = StartBody(key, boolCount);
        Assert.Equal(72 + 4 * boolCount + 2 * key.Length, body.Length);

        var request = PlazaRoomWire.ReadRoomReqEnterGame(TestBuilds.Packets, build, body);

        Assert.Equal(key, request.MapKey);
        Assert.Equal(7, request.Unknown1);
        Assert.Throws<PacketFormatException>(() =>
            PlazaRoomWire.ReadRoomReqEnterGame(TestBuilds.Packets, build, body.AsSpan(0, body.Length - 1)));
        Assert.Throws<PacketFormatException>(() =>
            PlazaRoomWire.ReadRoomReqEnterGame(TestBuilds.Packets, build, [.. body, 0, 0, 0, 0]));
    }

    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("cn-324")]
    [InlineData("cn-279")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-706")]
    public void ReadyRequestIsExactlyThreeBool32Fields(string build)
    {
        var body = Convert.FromHexString("010000000000000001000000");

        Assert.Equal((true, false, true), PlazaRoomWire.ReadReadyStatus(TestBuilds.Packets, build, body));
        Assert.Throws<PacketFormatException>(() =>
            PlazaRoomWire.ReadReadyStatus(TestBuilds.Packets, build, body.AsSpan(0, 8)));
        Assert.Throws<PacketFormatException>(() =>
            PlazaRoomWire.ReadReadyStatus(TestBuilds.Packets, build, [.. body, 0]));
    }

    [Fact]
    public void Cn706RequiresItsOwnSixthMetaFlagInsteadOfInheritingId400Width()
    {
        var body = StartBody("", 6);
        Assert.Equal(96, body.Length);
        Assert.Equal("", PlazaRoomWire.ReadRoomReqEnterGame(TestBuilds.Packets, "cn-706", body).MapKey);
        Assert.Throws<PacketFormatException>(() =>
            PlazaRoomWire.ReadRoomReqEnterGame(TestBuilds.Packets, "id-400", body));
        Assert.Throws<PacketFormatException>(() =>
            PlazaRoomWire.ReadRoomReqEnterGame(TestBuilds.Packets, "cn-706", StartBody("", 5)));
    }

    private static byte[] StartBody(string mapKey, int metaBools)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new PacketWriter(buffer);
        writer.WriteBool32(true);
        writer.WriteWideStringPrefixed(mapKey);
        writer.WriteWideStringPrefixed("");
        writer.WriteWideStringPrefixed("");
        writer.WriteInt32(7);
        for (var i = 0; i < 13; i++)
            writer.WriteInt32(100 + i);
        for (var i = 0; i < metaBools; i++)
            writer.WriteBool32(i % 2 == 0);
        return buffer.WrittenSpan.ToArray();
    }
}
