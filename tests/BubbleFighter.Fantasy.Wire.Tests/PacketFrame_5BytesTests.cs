using BubbleFighter.Fantasy.Tests;
using System.Buffers;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Wire;
using Xunit;

namespace BubbleFighter.Fantasy.Wire.Tests;

/// <summary>The kr-812 5-byte frame, pinned byte for byte.</summary>
public sealed class PacketFrame_5BytesTests
{
    [Fact]
    public void PatchReplyFrameKeepsItsRecordedLayout()
    {
        // part01 s2c#1: gspRepGamePatch, 8 zero payload bytes.
        var frame = PacketFrame_5Bytes.Encode(2, new byte[8]);
        Assert.Equal(PacketFrame_5Bytes.HeaderSize + 8, frame.Length);
        Assert.Equal(Convert.FromHexString("08000200000000000000000000"), frame);
    }

    [Fact]
    public void FramesRoundTripAndAdvanceOnlyWhenComplete()
    {
        var frame = PacketFrame_5Bytes.Encode(17, [1, 2, 3, 4]);
        Assert.Equal(1 ^ 2 ^ 3 ^ 4, frame[4]);
        var partial = new ReadOnlySequence<byte>(frame, 0, frame.Length - 1);
        Assert.False(PacketFrame_5Bytes.TryReadFrame(ref partial, 1024, out _, out _));
        Assert.Equal(frame.Length - 1, partial.Length);
        var whole = new ReadOnlySequence<byte>(frame);
        Assert.True(PacketFrame_5Bytes.TryReadFrame(ref whole, 1024, out var opcode, out var payload));
        Assert.Equal(17, opcode);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, payload);
        Assert.Equal(0, whole.Length);
    }

    [Fact]
    public void BadPayloadXorAndOversizeAreRejected()
    {
        var frame = PacketFrame_5Bytes.Encode(3, [9, 9]);
        frame[4] ^= 1;
        var corrupt = new ReadOnlySequence<byte>(frame);
        Assert.Throws<PacketFormatException>(() => PacketFrame_5Bytes.TryReadFrame(ref corrupt, 1024, out _, out _));
        var large = new ReadOnlySequence<byte>(PacketFrame_5Bytes.Encode(3, new byte[10]));
        Assert.Throws<PacketFormatException>(() => PacketFrame_5Bytes.TryReadFrame(ref large, 9, out _, out _));
    }

    [Fact]
    public void Kr812SharesTheKr1256StreamButUsesAFiveByteHeader()
    {
        var dialect = TestWireDialects.Get("kr-812");
        Assert.Equal(812, dialect.Profile.ClientVersionMajor);
        Assert.Equal(BootstrapGreeting.Size, dialect.PreambleLength(PacketDirection.ServerToClient));
        Assert.Equal(PacketFrame_5Bytes.HeaderSize, dialect.MaxFrameOverheadBytes);
        Assert.Equal(PacketFrame_5Bytes.Encode(2, [5]), dialect.EncodeFrame(2, [5]));
    }

    [Theory]
    [InlineData("cn-735", 735)]
    [InlineData("cn-738", 738)]
    [InlineData("cn-742", 742)]
    public void MidCnDialectsUseTheLegacyStream(string id, int major)
    {
        var dialect = TestWireDialects.Get(id);
        Assert.Equal(major, dialect.Profile.ClientVersionMajor);
        Assert.Equal(BootstrapGreeting.Size, dialect.PreambleLength(PacketDirection.ServerToClient));
        Assert.Equal(258, BootstrapGreeting.Size);
        Assert.Equal(PacketFrame_5Bytes.HeaderSize, dialect.MaxFrameOverheadBytes);
        Assert.Equal(5, PacketFrame_5Bytes.HeaderSize);
        var frame = dialect.EncodeFrame(3, [1, 2, 3]);
        Assert.Equal(PacketFrame_5Bytes.Encode(3, [1, 2, 3]), frame);
        Assert.Equal(5 + 3, frame.Length);
    }
}
