using BubbleFighter.Fantasy.Wire;
using System.Buffers;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Protocol.Tests;

/// <summary>Tests for <see cref="PacketFrame_8Bytes"/>.</summary>
public sealed class PacketFrame_8BytesTests
{
    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }

    private static ReadOnlySequence<byte> Sequence(params byte[][] chunks)
    {
        var first = new Segment(chunks[0]);
        var last = first;
        foreach (var chunk in chunks.Skip(1))
            last = last.Append(chunk);
        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }
    [Fact]
    public void EncodePinsByteLengthAndIntegrityBytes()
    {
        // gcpReqGamePatch-shaped payload: first u32 is the client version 1256, rest zeros.
        var payload = WirePins.GamePatchPayload();
        var frame = PacketFrame_8Bytes.Encode(WirePins.PatchRequestOpcode, payload);

        Assert.Equal(WirePins.GamePatchFrameSize, frame.Length);
        Assert.Equal(WirePins.FrameHeaderSize, PacketFrame_8Bytes.HeaderSize);
        // Header: u32 length=96, u16 opcode=1, payload_xor=0xEC (0xE8^0x04), header_xor=0x8D.
        Assert.Equal(WirePins.GamePatchPlainHeaderHex, Convert.ToHexString(frame[..PacketFrame_8Bytes.HeaderSize]));
        Assert.Equal(payload, frame[PacketFrame_8Bytes.HeaderSize..]);
    }

    [Fact]
    public void EncodeDecodeRoundTrips()
    {
        var payload = new byte[200];
        new Random(42).NextBytes(payload);
        var frame = PacketFrame_8Bytes.Encode(521, payload);
        Assert.True(PacketFrame_8Bytes.TryReadHeader(frame, out var header));
        Assert.Equal(200, header.PayloadLength);
        Assert.Equal((ushort)521, header.Opcode);
        Assert.True(PacketFrame_8Bytes.ValidatePayload(header, frame.AsSpan(8)));
    }

    [Fact]
    public void CorruptHeaderIntegrityFails()
    {
        var frame = PacketFrame_8Bytes.Encode(1, [1, 2, 3]);
        frame[7] ^= 0xFF;
        Assert.False(PacketFrame_8Bytes.TryReadHeader(frame, out _));
    }

    [Fact]
    public void CorruptPayloadIntegrityFails()
    {
        var frame = PacketFrame_8Bytes.Encode(1, [1, 2, 3]);
        var payload = (byte[])frame[8..].Clone();
        payload[0] ^= 0xFF;
        Assert.True(PacketFrame_8Bytes.TryReadHeader(frame, out var header));
        Assert.False(PacketFrame_8Bytes.ValidatePayload(header, payload));
    }

    [Fact]
    public void EmptyPayloadHasZeroXor()
    {
        var frame = PacketFrame_8Bytes.Encode(WirePins.MoveChannelOpcode, []);
        Assert.Equal(WirePins.FrameHeaderSize, frame.Length);
        Assert.Equal(WirePins.NotifyFirstMoveChannelHeaderHex, Convert.ToHexString(frame));
        Assert.Equal(0, frame[6]);
        Assert.True(PacketFrame_8Bytes.TryReadHeader(frame, out var header));
        Assert.Equal(0, header.PayloadLength);
        Assert.Equal(WirePins.MoveChannelOpcode, header.Opcode);
        Assert.True(PacketFrame_8Bytes.ValidatePayload(header, []));
    }

    [Fact]
    public void TryReadHeaderRejectsShortBuffer()
    {
        Assert.False(PacketFrame_8Bytes.TryReadHeader(new byte[PacketFrame_8Bytes.HeaderSize - 1], out _));
        var empty = ReadOnlySequence<byte>.Empty;
        Assert.False(PacketFrame_8Bytes.TryReadFrame(ref empty, 1024, out _, out _));
    }

    [Fact]
    public void TryReadFrameThrowsOnCorruptPayload()
    {
        var frame = PacketFrame_8Bytes.Encode(1, [1, 2, 3]);
        frame[8] ^= 0xFF;
        var buffer = Sequence(frame);
        var ex = Assert.Throws<PacketFormatException>(() => PacketFrame_8Bytes.TryReadFrame(ref buffer, 1024, out _, out _));
        Assert.Contains("payload integrity", ex.Message);
        Assert.Equal(PacketFrame_8Bytes.HeaderSize, ex.Offset);
    }

    [Fact]
    public void TryReadFrameThrowsOnNegativeDeclaredLength()
    {
        var header = new byte[PacketFrame_8Bytes.HeaderSize];
        header[0] = 0xFF;
        header[1] = 0xFF;
        header[2] = 0xFF;
        header[3] = 0xFF;
        header[4] = 1;
        header[7] = Xor(header.AsSpan(0, 7));
        var buffer = Sequence(header);
        var ex = Assert.Throws<PacketFormatException>(() => PacketFrame_8Bytes.TryReadFrame(ref buffer, 1024, out _, out _));
        Assert.Contains("exceeds max_payload_bytes", ex.Message);
    }

    [Fact]
    public void TryReadFrameLeavesUnreadTail()
    {
        var first = PacketFrame_8Bytes.Encode(WirePins.MoveChannelOpcode, []);
        var tail = new byte[] { 0xAA };
        var buffer = Sequence([.. first, .. tail]);
        Assert.True(PacketFrame_8Bytes.TryReadFrame(ref buffer, 1024, out var header, out var payload));
        Assert.Equal(WirePins.MoveChannelOpcode, header.Opcode);
        Assert.Empty(payload);
        Assert.Equal(1, buffer.Length);
    }

    private static byte Xor(ReadOnlySpan<byte> data)
    {
        byte value = 0;
        foreach (var b in data)
            value ^= b;
        return value;
    }

    [Fact]
    public void TryReadFrameParsesHeaderSplitAcrossSegments()
    {
        var frame = PacketFrame_8Bytes.Encode(521, [9, 8, 7]);
        var buffer = Sequence(frame[..3], frame[3..]);
        Assert.True(PacketFrame_8Bytes.TryReadFrame(ref buffer, 1024, out var header, out var payload));
        Assert.Equal((ushort)521, header.Opcode);
        Assert.Equal([9, 8, 7], payload);
        Assert.Equal(0, buffer.Length);
    }

    [Fact]
    public void TryReadFrameParsesPayloadSplitAcrossSegments()
    {
        var frame = PacketFrame_8Bytes.Encode(2, [1, 2, 3, 4, 5, 6]);
        var buffer = Sequence(frame[..10], frame[10..]);
        Assert.True(PacketFrame_8Bytes.TryReadFrame(ref buffer, 1024, out var header, out var payload));
        Assert.Equal(6, header.PayloadLength);
        Assert.Equal([1, 2, 3, 4, 5, 6], payload);
        Assert.Equal(0, buffer.Length);
    }

    [Fact]
    public void TryReadFrameWaitsForAnIncompleteFrame()
    {
        var frame = PacketFrame_8Bytes.Encode(1, [1, 2, 3, 4]);
        var buffer = Sequence(frame[..6]);
        Assert.False(PacketFrame_8Bytes.TryReadFrame(ref buffer, 1024, out _, out _));
        Assert.Equal(6, buffer.Length);

        var complete = Sequence(frame[..6], frame[6..]);
        Assert.True(PacketFrame_8Bytes.TryReadFrame(ref complete, 1024, out var header, out var payload));
        Assert.Equal(4, header.PayloadLength);
        Assert.Equal([1, 2, 3, 4], payload);
    }

    [Fact]
    public void TryReadFrameConsumesTwoFramesFromOneSegment()
    {
        var first = PacketFrame_8Bytes.Encode(163, []);
        var second = PacketFrame_8Bytes.Encode(2, [0xAA, 0xBB]);
        var buffer = Sequence([.. first, .. second]);

        Assert.True(PacketFrame_8Bytes.TryReadFrame(ref buffer, 1024, out var header1, out _));
        Assert.Equal((ushort)163, header1.Opcode);
        Assert.True(PacketFrame_8Bytes.TryReadFrame(ref buffer, 1024, out var header2, out var payload2));
        Assert.Equal((ushort)2, header2.Opcode);
        Assert.Equal([0xAA, 0xBB], payload2);
        Assert.False(PacketFrame_8Bytes.TryReadFrame(ref buffer, 1024, out _, out _));
    }

    [Fact]
    public void TryReadFrameThrowsOnCorruptHeader()
    {
        var frame = PacketFrame_8Bytes.Encode(1, [1, 2, 3]);
        frame[7] ^= 0xFF;
        var buffer = Sequence(frame);
        Assert.Throws<PacketFormatException>(() => PacketFrame_8Bytes.TryReadFrame(ref buffer, 1024, out _, out _));
    }

    [Fact]
    public void TryReadFrameThrowsOnOversizedPayload()
    {
        var frame = PacketFrame_8Bytes.Encode(1, new byte[64]);
        var buffer = Sequence(frame);
        var ex = Assert.Throws<PacketFormatException>(() => PacketFrame_8Bytes.TryReadFrame(ref buffer, 16, out _, out _));
        Assert.Contains("exceeds max_payload_bytes", ex.Message);
    }
}
