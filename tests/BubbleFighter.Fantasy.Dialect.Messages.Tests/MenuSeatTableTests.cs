using System.Buffers;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Dialect.Rooms;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins menu-specific seat values independently of play-room defaults.</summary>
public sealed class MenuSeatTableTests
{
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("cn-324")]
    [InlineData("cn-279")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-706")]
    public void MenuEnvelopePreservesIdentityEquipmentAndExplicitReady(string build)
    {
        var schema = TestBuilds.Of(build).Schema;
        var player = new MainMenuRoomOpened(-1, "ab", 300003, [300003, 201001]);
        foreach (var ready in new[] { false, true })
        {
            var buffer = new ArrayBufferWriter<byte>();
            var writer = new PacketWriter(buffer);
            writer.WriteInt32(7);
            RoomCodec.WriteUserSlot(writer, player, schema, ready);
            RoomCodec.WriteEquippedItemList(writer, player, schema);
            var actual = PlazaRoomWire.WriteSetSlot(player, TestBuilds.Packets, build, schema, 7, ready);
            Assert.Equal(buffer.WrittenCount, actual.Length);
            Assert.Equal(buffer.WrittenSpan.ToArray(), actual);
            Assert.Equal(7, BitConverter.ToInt32(actual));
        }
        var idle = PlazaRoomWire.WriteSetSlot(player, TestBuilds.Packets, build, schema, 7, false);
        var active = PlazaRoomWire.WriteSetSlot(player, TestBuilds.Packets, build, schema, 7, true);
        Assert.Equal(idle.Length, active.Length);
        var changes = Enumerable.Range(0, idle.Length).Where(i => idle[i] != active[i]).ToArray();
        Assert.Single(changes);
        Assert.Equal(0, idle[changes[0]]);
        Assert.Equal(1, active[changes[0]]);
    }

    [Fact]
    public void ReadyOverrideFollowsItsNameWhenSchemaFieldsMove()
    {
        var original = TestBuilds.Of("kr-812").Schema;
        var fields = original.UserSlot.ToArray();
        (fields[10], fields[11]) = (fields[11], fields[10]);
        var schema = original with { UserSlot = fields };
        var player = new MainMenuRoomOpened(1, "", 300003, []);
        var buffer = new ArrayBufferWriter<byte>();
        RoomCodec.WriteUserSlot(new PacketWriter(buffer), player, schema, ready: true);
        var idle = new ArrayBufferWriter<byte>();
        RoomCodec.WriteUserSlot(new PacketWriter(idle), player, schema, ready: false);
        Assert.Single(Enumerable.Range(0, buffer.WrittenCount),
            i => buffer.WrittenSpan[i] != idle.WrittenSpan[i]);
    }

    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("cn-324")]
    [InlineData("cn-279")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-706")]
    public void StandaloneSessionRetainsMenuAndPlayDefaults(string build)
    {
        var schema = TestBuilds.Of(build).Schema;
        foreach (var play in new[] { false, true })
        {
            foreach (var kind in new[] { 1, 2, 5 })
            {
                var room = new RoomValues(kind, [])
                {
                    Play = play, RoomId = 42, Title = "ab", Mode = 101, Difficulty = 3,
                    CurrentMap = "map", MapName = "test", MasterNickname = "host",
                    Occupants = 2, OpenSeats = 4, HasPassword = true,
                };
                var buffer = new ArrayBufferWriter<byte>();
                RoomCodec.WriteSessionSetting(new PacketWriter(buffer), schema, room);
                var actual = RoomCodec.WriteSessionEnvelope(TestBuilds.Packets, build, schema, room);
                Assert.Equal(buffer.WrittenCount, actual.Length);
                Assert.Equal(buffer.WrittenSpan.ToArray(), actual);
            }
        }
    }
}
