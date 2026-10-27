using System.Buffers;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Wire.Tests;

/// <summary>Checks the cn-279 header against its native finalizer and fragmented receive path.</summary>
public sealed class PacketFrame_10BytesTests
{
    [Fact]
    public void EncodedHeaderPinsEveryFieldAndExcludesItselfFromLength()
    {
        var bytes = PacketFrame_10Bytes.Encode(2, new byte[8]);
        Assert.Equal(18, bytes.Length);
        Assert.Equal(Convert.FromHexString("FF0008000000000002000000000000000000"), bytes);
    }

    [Fact]
    public void SegmentedInputConsumesExactlyOneFrameAndLeavesIncompleteDataUntouched()
    {
        var bytes = Convert.FromHexString("FF000300000000003412102030FF000000000000000200");
        for (var length = 0; length < 13; length++)
        {
            var incomplete = new ReadOnlySequence<byte>(bytes, 0, length);
            Assert.False(PacketFrame_10Bytes.TryReadFrame(ref incomplete, 3, out _, out _));
            Assert.Equal(length, incomplete.Length);
        }
        var first = new Segment(bytes.AsMemory(0, 5));
        var last = first.Append(bytes.AsMemory(5));
        var input = new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
        Assert.True(PacketFrame_10Bytes.TryReadFrame(ref input, 3, out var opcode, out var payload));
        Assert.Equal(0x1234, opcode);
        Assert.Equal(new byte[] { 0x10, 0x20, 0x30 }, payload);
        Assert.Equal(10, input.Length);
        Assert.True(PacketFrame_10Bytes.TryReadFrame(ref input, 0, out opcode, out payload));
        Assert.Equal(2, opcode);
        Assert.Empty(payload);
        Assert.True(input.IsEmpty);
    }

    [Fact]
    public void InvalidMarkerAndOversizeFailBeforeConsumingInput()
    {
        var marker = new ReadOnlySequence<byte>(Convert.FromHexString("FE000000000000000100"));
        Assert.Throws<PacketFormatException>(() => PacketFrame_10Bytes.TryReadFrame(ref marker, 100, out _, out _));
        Assert.Equal(10, marker.Length);
        var oversized = new ReadOnlySequence<byte>(Convert.FromHexString("FF000200000000000100"));
        Assert.Throws<PacketFormatException>(() => PacketFrame_10Bytes.TryReadFrame(ref oversized, 1, out _, out _));
        Assert.Equal(10, oversized.Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => PacketFrame_10Bytes.Encode(1, new byte[65536]));
    }

    [Fact]
    public void MaximumPayloadDoesNotWrapAndUnknownFieldsAreNotInventedAsIntegrityChecks()
    {
        var bytes = PacketFrame_10Bytes.Encode(ushort.MaxValue, new byte[ushort.MaxValue]);
        Assert.Equal(65545, bytes.Length);
        bytes[1] = 0x42;
        bytes[4] = 0xA1;
        bytes[6] = 0xB2;
        bytes[^1] = 0xCC;
        var input = new ReadOnlySequence<byte>(bytes);
        Assert.True(PacketFrame_10Bytes.TryReadFrame(ref input, 65535, out var opcode, out var payload));
        Assert.Equal(ushort.MaxValue, opcode);
        Assert.Equal(65535, payload.Length);
        Assert.Equal(0xCC, payload[^1]);
        Assert.True(input.IsEmpty);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public Segment Append(ReadOnlyMemory<byte> nextMemory)
        {
            var next = new Segment(nextMemory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }

    }
}
