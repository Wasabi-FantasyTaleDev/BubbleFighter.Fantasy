using System.Buffers;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Protocol.Tests;

/// <summary>Roundtrip tests for <see cref="PacketReader"/> and <see cref="PacketWriter"/>.</summary>
public sealed class PacketReaderWriterTests
{
    [Fact]
    public void EveryPrimitiveRoundTrips()
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new PacketWriter(buffer);
        writer.WriteByte(0xAB);
        writer.WriteUInt16(0x1234);
        writer.WriteInt32(-123456);
        writer.WriteUInt32(4000000000u);
        writer.WriteInt64(-1234567890123L);
        writer.WriteSingle(1.5f);
        writer.WriteDouble(-2.25);
        writer.WriteBool32(true);
        writer.WriteBool32(false);
        writer.WriteWideStringPrefixed("hello");
        writer.WriteWideCString("world");
        writer.WriteBytes([1, 2, 3, 4]);

        var reader = new PacketReader(buffer.WrittenSpan);
        Assert.Equal(0xAB, reader.ReadByte());
        Assert.Equal(0x1234, reader.ReadUInt16());
        Assert.Equal(-123456, reader.ReadInt32());
        Assert.Equal(4000000000u, reader.ReadUInt32());
        Assert.Equal(-1234567890123L, reader.ReadInt64());
        Assert.Equal(1.5f, reader.ReadSingle());
        Assert.Equal(-2.25, reader.ReadDouble());
        Assert.True(reader.ReadBool32());
        Assert.False(reader.ReadBool32());
        Assert.Equal("hello", reader.ReadWideStringPrefixed());
        Assert.Equal("world", reader.ReadWideCString());
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, reader.ReadBytes(4).ToArray());
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void TruncatedReadThrowsWithOffset()
    {
        var ex = Assert.Throws<PacketFormatException>(() =>
        {
            var reader = new PacketReader([0x01]);
            _ = reader.ReadByte();
            reader.ReadUInt32();
        });
        Assert.Equal(1, ex.Offset);
    }

    [Fact]
    public void UnterminatedWideCStringThrows()
    {
        Assert.Throws<PacketFormatException>(() =>
        {
            var reader = new PacketReader([0x61, 0x00, 0x62, 0x00]);
            reader.ReadWideCString();
        });
    }

    [Fact]
    public void PatchReplyPayloadForZeroAndEmptyIsEightZeroBytes()
    {
        // gspRepGamePatch(0, "") = i32 0 + u32-prefixed empty string = 8 zero bytes.
        // Evidence: handler sub_9A1250 / label 0x15B699C.
        var buffer = new ArrayBufferWriter<byte>(WirePins.RepGamePatchPayloadSize);
        var writer = new PacketWriter(buffer);
        writer.WriteInt32(0);
        writer.WriteWideStringPrefixed("");
        Assert.Equal(WirePins.RepGamePatchPayloadSize, buffer.WrittenCount);
        Assert.Equal(new byte[WirePins.RepGamePatchPayloadSize], buffer.WrittenSpan.ToArray());

        var reader = new PacketReader(buffer.WrittenSpan);
        Assert.Equal(0, reader.ReadInt32());
        Assert.Equal("", reader.ReadWideStringPrefixed());
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void GamePatchPayloadPinsVersionWordAndUnknownTail()
    {
        // gcpReqGamePatch: 96-byte payload; first u32 is ClientVersion_Major (sub_99DD00).
        var payload = WirePins.GamePatchPayload();
        Assert.Equal(WirePins.GamePatchPayloadSize, payload.Length);

        var reader = new PacketReader(payload);
        Assert.Equal(1256, reader.ReadInt32());
        Assert.Equal(92, reader.Remaining);
        Assert.All(reader.ReadBytes(92).ToArray(), b => Assert.Equal(0, b));
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void PrefixedEmptyWideStringIsFourZeroBytes()
    {
        var buffer = new ArrayBufferWriter<byte>(4);
        new PacketWriter(buffer).WriteWideStringPrefixed("");
        Assert.Equal(4, buffer.WrittenCount);
        Assert.Equal(new byte[4], buffer.WrittenSpan.ToArray());
    }
}
