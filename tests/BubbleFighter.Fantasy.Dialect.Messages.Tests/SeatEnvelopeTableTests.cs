using System.Buffers;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Dialect.Rooms;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Checks nested seat composition against the previously separate record writers.</summary>
public sealed class SeatEnvelopeTableTests
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
    public void OccupiedVacantAndDepartedSeatsRetainTheirNativeRecords(string build)
    {
        var schema = TestBuilds.Of(build).Schema;
        var occupied = new RoomSlotValues(true, false, -1, "ab", 300003, [300003, 201001], true)
        {
            Team = 2, SpawnIndex = 7, Level = 42,
        };
        foreach (var slot in new[]
        {
            occupied,
            RoomSlotValues.Empty(closed: true),
            occupied with { Occupied = false, Departed = true, Ready = false, EquippedItemIds = [] },
        })
        {
            foreach (var leaving in new[] { false, true })
            {
                var buffer = new ArrayBufferWriter<byte>();
                var writer = new PacketWriter(buffer);
                writer.WriteInt32(7);
                if (leaving)
                    RoomCodec.WritePlayUserSlot(writer, schema, slot);
                else
                    RoomCodec.WritePlaySlotAndItems(writer, schema, slot);
                var actual = RoomCodec.WriteSeatEnvelope(TestBuilds.Packets, build, schema, 7, slot, leaving);
                Assert.Equal(buffer.WrittenCount, actual.Length);
                Assert.Equal(buffer.WrittenSpan.ToArray(), actual);
                Assert.Equal(7, BitConverter.ToInt32(actual));
            }
        }
    }

    [Theory]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    public void IndonesianStartSeatCarriesTheNativeSpawnIndex(string build)
    {
        var schema = TestBuilds.Of(build).Schema;
        var occupied = new RoomSlotValues(true, false, 7, "ab", 300003, [300003], false)
        {
            SpawnIndex = 7,
        };
        var body = RoomCodec.WriteSeatEnvelope(TestBuilds.Packets, build, schema, 7, occupied);

        // RoomSeatUpdate: slot u32, three bool32 values, then UserSlot +128 as one byte.
        Assert.Equal(7, BitConverter.ToInt32(body, 0));
        Assert.Equal(7, body[16]);
    }

    [Fact]
    public void OversizedSeatIdentityCannotExceedNativeFrameBudget()
    {
        var slot = new RoomSlotValues(true, false, 1, new string('a', 32768), 300003, [], false);
        Assert.Throws<ArgumentOutOfRangeException>(() => RoomCodec.WriteSeatEnvelope(
            TestBuilds.Packets, "kr-812", TestBuilds.Of("kr-812").Schema, 0, slot));
    }
}
