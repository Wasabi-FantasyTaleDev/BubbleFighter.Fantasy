using System.Buffers;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins native room-list and join request layouts independently of their tables.</summary>
public sealed class RoomAdmissionTableTests
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
    public void MoveAndJoinReplyPreserveNativeScalarBodies(string build)
    {
        var moved = PlayRoomCodec.WritePlazaMoveInto(TestBuilds.Packets, build);
        Assert.Equal(12, moved.Length);
        Assert.Equal("010000003C00000001000000", Convert.ToHexString(moved));
        var refused = PacketCodec.Write(TestBuilds.Packets, build, "RoomJoinResult",
            new Dictionary<string, object?> { ["result"] = -1 });
        Assert.Equal(4, refused.Length);
        Assert.Equal("FFFFFFFF", Convert.ToHexString(refused));
    }

    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("id-1809")]
    [InlineData("cn-706")]
    public void OptionalNativeSessionInitializationPreservesSignedWordBits(string build)
    {
        Assert.Equal("1A010000", Convert.ToHexString(PlayRoomCodec.WritePlazaSessionInit(TestBuilds.Packets, build, 282)));
        Assert.Equal("FFFFFFFF", Convert.ToHexString(PlayRoomCodec.WritePlazaSessionInit(TestBuilds.Packets, build, -1)));
    }

    [Theory]
    [InlineData("kr-812", 4)]
    [InlineData("cn-717", 4)]
    [InlineData("cn-324", 0)]
    [InlineData("cn-279", 0)]
    [InlineData("id-400", 2)]
    [InlineData("id-1809", 4)]
    [InlineData("cn-706", 2)]
    [InlineData("cn-406", 0)]
    public void JoinRequestRetainsPasswordSignedWordsAndOwnTailWidth(string build, int tailWidth)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new PacketWriter(buffer);
        writer.WriteInt32(404);
        writer.WriteInt32(-1);
        writer.WriteInt32(int.MinValue);
        writer.WriteWideStringPrefixed("Ω가");
        if (tailWidth == 2) writer.WriteUInt16(ushort.MaxValue);
        if (tailWidth == 4) writer.WriteInt32(-1);
        var body = buffer.WrittenSpan.ToArray();
        Assert.Equal(20 + tailWidth, body.Length);
        var request = PlazaRoomWire.ReadJoinSession(TestBuilds.Packets, build, body);
        Assert.Equal(404, request.RoomId);
        Assert.Equal(-1, request.Unknown1);
        Assert.Equal(int.MinValue, request.Unknown2);
        Assert.Equal("Ω가", request.Password);
        Assert.Equal(tailWidth == 2 ? ushort.MaxValue : tailWidth == 4 ? -1 : 0, request.Unknown4);
        Assert.Throws<PacketFormatException>(() => PlazaRoomWire.ReadJoinSession(TestBuilds.Packets, build, body[..^1]));
        Assert.Throws<PacketFormatException>(() => PlazaRoomWire.ReadJoinSession(TestBuilds.Packets, build, [.. body, 0]));
    }

    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("cn-324")]
    [InlineData("cn-279")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-706")]
    [InlineData("cn-406")]
    public void ListRequestPreservesUnsignedWordBoundariesAndStrictLength(string build)
    {
        Assert.Equal(((ushort)0x8000, ushort.MaxValue),
            PlazaRoomWire.ReadListSession(TestBuilds.Packets, build, [0, 128, 255, 255]));
        Assert.Throws<PacketFormatException>(() => PlazaRoomWire.ReadListSession(TestBuilds.Packets, build, [0, 0, 0]));
        Assert.Throws<PacketFormatException>(() => PlazaRoomWire.ReadListSession(TestBuilds.Packets, build, [0, 0, 0, 0, 0]));
    }
}
