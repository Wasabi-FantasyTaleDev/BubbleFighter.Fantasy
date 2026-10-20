using System.Buffers;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Dialect.Rooms;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Checks that random-map presentation preserves opaque bytes even when map fields move.</summary>
public sealed class UnchosenMapPreservationTests
{
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("cn-324")]
    [InlineData("cn-279")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-706")]
    public void ReorderedMapRolesLeaveNoncanonicalFlagsAndFloatingPointBitsUntouched(string build)
    {
        var schema = TestBuilds.Of(build).Schema;
        schema = schema with { GameSetting = schema.GameSetting.Reverse().ToArray() };
        var hasTheme = schema.GameSetting.Any(field => field.Value == RoomFieldValue.MapTheme);
        var original = Body(schema, hasTheme, present: false);
        var expected = Body(schema, hasTheme, present: true);

        var actual = PlazaRoomWire.PresentUnchosenMap(schema, original, "Random", "All");

        Assert.Equal(expected, actual.ToArray());
        Assert.Equal(expected, PlazaRoomWire.PresentUnchosenMap(schema, actual.Span, "Random", "All").ToArray());
    }

    private static byte[] Body(RoomSchema schema, bool hasTheme, bool present)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new PacketWriter(buffer);
        foreach (var field in schema.GameSetting)
        {
            if (field.Type == RoomFieldType.WStr)
            {
                var text = field.Value == RoomFieldValue.MapName ? "" : "opaque_" + field.Name;
                if (present)
                    text = field.Value switch
                    {
                        RoomFieldValue.MapName => "Random",
                        RoomFieldValue.MapTheme or RoomFieldValue.NpcTableKey => "All",
                        RoomFieldValue.MapDirectory or RoomFieldValue.CurrentMap => "Random",
                        _ => text,
                    };
                writer.WriteWideStringPrefixed(text);
            }
            else if (field.Type == RoomFieldType.U8)
                writer.WriteByte(0xA5);
            else if (field.Type == RoomFieldType.U16)
                writer.WriteUInt16(0xA55A);
            else if (field.Type == RoomFieldType.F64)
                writer.WriteDouble(BitConverter.Int64BitsToDouble(unchecked((long)0xFFF0000000000001UL)));
            else if (field.Type is RoomFieldType.EmptyVector or RoomFieldType.Equipped)
                writer.WriteInt32(0);
            else
                writer.WriteUInt32(field.Type == RoomFieldType.Bool ? 0x80000002U : 0x7FA12345U);
        }
        return buffer.WrittenSpan.ToArray();
    }
}
