using System.Buffers;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins complete native room-list envelopes and host-setting round trips, including CN2013.</summary>
public sealed class RoomListTableTests
{
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("cn-324")]
    [InlineData("cn-279")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-706")]
    [InlineData("cn-406")]
    public void WaitingAndActiveRowsKeepEveryNativeField(string build)
    {
        var schema = TestBuilds.Of(build).Schema;
        var settings = RoomCodec.WriteGameSettingChange(schema, new RoomValues(2, [])
        {
            Play = true, Mode = 101, Difficulty = 3, CurrentMap = "rel_test",
            MapName = "test", MapDirectory = "display", NpcTableKey = "npc_test",
        });
        var decoded = RoomCodec.ReadGameSettingValues(schema, settings);
        Assert.Equal(settings.Length, PacketCodec.Write(TestBuilds.Packets, build, "GameSetting", decoded).Length);
        Assert.Equal(settings, PacketCodec.Write(TestBuilds.Packets, build, "GameSetting", decoded));
        var tableSettings = PacketCodec.Read(TestBuilds.Packets, build, "GameSetting", settings,
            requireCanonicalBooleans: true);
        Assert.Equal(settings, PacketCodec.Write(TestBuilds.Packets, build, "GameSetting", tableSettings));
        Assert.Throws<PacketFormatException>(() => RoomCodec.ReadGameSettingValues(schema, settings[..^1]));
        Assert.Throws<PacketFormatException>(() => RoomCodec.ReadGameSettingValues(schema, [.. settings, 0]));

        var buffer = new ArrayBufferWriter<byte>();
        var writer = new PacketWriter(buffer);
        writer.WriteInt32(3);
        writer.WriteBool32(true);
        writer.WriteInt32(2);
        var rows = new List<Dictionary<string, object?>>();
        foreach (var state in new[] { 2, 4 })
        {
            var room = new RoomValues(2, [])
            {
                Play = true, RoomId = state + 100, Title = "room", Mode = 101,
                SessionState = state, MasterNickname = "master", Occupants = 3,
                OpenSeats = 8, HasPassword = true, GameSetting = settings,
            };
            RoomCodec.WriteSessionSetting(writer, schema, room);
            rows.Add(RoomCodec.PlaySessionValues(schema, room));
        }
        var body = PacketCodec.Write(TestBuilds.Packets, build, "RoomListReply",
            new Dictionary<string, object?> { ["page"] = 3, ["more"] = true, ["rooms"] = rows });
        Assert.Equal(buffer.WrittenCount, body.Length);
        Assert.Equal(buffer.WrittenSpan.ToArray(), body);
        var tableList = PacketCodec.Read(TestBuilds.Packets, build, "RoomListReply", body,
            requireCanonicalBooleans: true);
        Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, build, "RoomListReply", tableList));
        Assert.Equal(2, Assert.IsType<object?[]>(tableList["rooms"]).Length);
        Assert.Equal(new byte[12], PlazaRoomWire.WriteRepListSessionEmpty(TestBuilds.Packets, build));
    }

    [Fact]
    public void SettingsRejectNonCanonicalBoolAndNonEmptyPlaceholderVector()
    {
        var schema = TestBuilds.Of("kr-812").Schema;
        var body = PacketCodec.Write(TestBuilds.Packets, "kr-812", "GameSetting", new Dictionary<string, object?>());
        var reader = new PacketReader(body);
        var firstBool = -1;
        var firstVector = -1;
        foreach (var field in schema.GameSetting)
        {
            if (field.Type == RoomFieldType.Bool && firstBool < 0) firstBool = reader.Offset;
            if (field.Type == RoomFieldType.EmptyVector && firstVector < 0) firstVector = reader.Offset;
            switch (field.Type)
            {
                case RoomFieldType.WStr: reader.ReadWideStringPrefixed(); break;
                case RoomFieldType.U8: reader.ReadByte(); break;
                case RoomFieldType.U16: reader.ReadUInt16(); break;
                default: reader.ReadInt32(); break;
            }
        }
        var invalidBool = body.ToArray();
        invalidBool[firstBool] = 2;
        Assert.Throws<PacketFormatException>(() => RoomCodec.ReadGameSettingValues(schema, invalidBool));
        var invalidVector = body.ToArray();
        invalidVector[firstVector] = 1;
        Assert.Throws<PacketFormatException>(() => RoomCodec.ReadGameSettingValues(schema, invalidVector));
    }
}
