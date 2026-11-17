using BubbleFighter.Fantasy.Abstractions.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Wire.Tests;

public sealed class PacketFrame_ClanServerTests
{
    [Fact]
    public void Encode_WritesHeaderInTheClear()
    {
        var body = new byte[] { 0x11, 0x22, 0x33 };
        var frame = PacketFrame_ClanServer.Encode(0x3EA, body);

        // Header: [u16 length=3][u16 opcode=0x3EA]
        Assert.Equal(7, frame.Length);
        Assert.Equal(0x03, frame[0]);
        Assert.Equal(0x00, frame[1]);
        Assert.Equal(0xEA, frame[2]);
        Assert.Equal(0x03, frame[3]);
    }

    [Fact]
    public void Encode_AppliesPerFrameXorToBody()
    {
        var body = new byte[] { 0x00, 0x00, 0x00 };
        var frame = PacketFrame_ClanServer.Encode(0x3EA, body);

        // (1002 + 3) % 255 = 240 selects key 0x1A; byte i is XORed with key + key % (i + 1).
        Assert.Equal(0x1A, frame[4]);
        Assert.Equal(0x1A, frame[5]);
        Assert.Equal(0x1C, frame[6]);
    }

    [Fact]
    public void TryReadFrame_DeciphersBody()
    {
        // Encode a frame, then decode it
        var original = new byte[] { 0xAA, 0xBB, 0xCC };
        var encoded = PacketFrame_ClanServer.Encode(0x3F0, original);
        var buffer = new System.Buffers.ReadOnlySequence<byte>(encoded);

        var consumed = PacketFrame_ClanServer.TryReadFrame(ref buffer, 65535, out var opcode, out var body);

        Assert.True(consumed);
        Assert.Equal(0x3F0, opcode);
        Assert.Equal(original, body);
        Assert.Equal(0, buffer.Length);
    }

    [Fact]
    public void TryReadFrame_ReturnsFalseWhenIncomplete()
    {
        var partial = new byte[] { 0x05, 0x00, 0xEA };
        var buffer = new System.Buffers.ReadOnlySequence<byte>(partial);

        var consumed = PacketFrame_ClanServer.TryReadFrame(ref buffer, 65535, out _, out var body);

        Assert.False(consumed);
        Assert.Empty(body);
        Assert.Equal(3, buffer.Length); // Buffer unchanged
    }

    [Fact]
    public void TryReadFrame_ThrowsWhenLengthExceedsClientBound()
    {
        // bodyLength >= 0xFFFC is rejected by the client (kr-1256 sub_11143A0)
        var header = new byte[] { 0xFC, 0xFF, 0xEA, 0x03 };
        var buffer = new System.Buffers.ReadOnlySequence<byte>(header);

        var ex = Assert.Throws<PacketFormatException>(() =>
            PacketFrame_ClanServer.TryReadFrame(ref buffer, 65535, out _, out _));

        Assert.Contains("0xFFFB", ex.Message);
    }

    [Fact]
    public void TryReadFrame_ThrowsWhenLengthExceedsMaxBodyBytes()
    {
        var header = new byte[] { 0x0A, 0x00, 0xEA, 0x03 };
        var buffer = new System.Buffers.ReadOnlySequence<byte>(header);

        var ex = Assert.Throws<PacketFormatException>(() =>
            PacketFrame_ClanServer.TryReadFrame(ref buffer, 5, out _, out _));

        Assert.Contains("max_body_bytes", ex.Message);
    }

    [Fact]
    public void Encode_ThrowsWhenBodyExceedsMaxBodyBytes()
    {
        var body = new byte[PacketFrame_ClanServer.MaxBodyBytes + 1];

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            PacketFrame_ClanServer.Encode(0x3EA, body));

        Assert.Contains("65531", ex.Message);
    }

    [Fact]
    public void CipherIsSymmetric()
    {
        var original = new byte[100];
        new Random(42).NextBytes(original);

        var encoded = PacketFrame_ClanServer.Encode(0x400, original);
        var buffer = new System.Buffers.ReadOnlySequence<byte>(encoded);
        PacketFrame_ClanServer.TryReadFrame(ref buffer, 65535, out _, out var decoded);

        Assert.Equal(original, decoded);
    }

    /// <summary>
    /// Pins the clan frame byte layout so a wire change that breaks the client is caught.
    /// The expected bytes come from encoding a known body with the verified cipher formula.
    /// </summary>
    [Fact]
    public void Encode_ProducesExpectedBytesForKnownInput()
    {
        var body = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 };
        var frame = PacketFrame_ClanServer.Encode(0x3EA, body);

        // (1002 + 5) % 255 = 242 selects key 0x64, giving the stream 64 64 65 64 64.
        var expected = new byte[] { 0x05, 0x00, 0xEA, 0x03, 0x65, 0x66, 0x66, 0x60, 0x61 };

        Assert.Equal(expected, frame);
    }

    /// <summary>
    /// Pins the key table against the bytes at kr-1256 VA 0x1891F28. A one-byte zero body
    /// ciphers to the key itself, so 255 opcodes read the whole reachable table back
    /// (index 255 is never selected: the index is taken modulo 255).
    /// </summary>
    [Fact]
    public void KeyTable_MatchesTheClientConstant()
    {
        var table = Convert.FromHexString(
            "8a0c85f3cc825fc19256576aaa03f0ba70e7877abb38849e4c189978504dc55a" +
            "0f955bf25a9934ea491ac48964b5d3d73972ea1818ed60065351fdac567a20d6" +
            "657263d2db12359cc01052a9f73d9057fba8894c550a684342928f8bcb09413e" +
            "2dde87764c930cf8371112b1d3eccc6eba213fa2722fd9c07c4f9263e28522cf" +
            "4ccc178f30380830362bfb5bea8d50663ca4cc7a7570967b29af3259cf71dadc" +
            "ebdf44290b119d8a06206bb3b45b06190d345605e2d8a105378e7ae44b45a9ca" +
            "f870249f671f61e2b4e4a09d2a067ef97b02654bc9e2a401d37bdcd192e77016" +
            "240bb3a3d2da8faa9524bdcdca32ec0b1a7364a53681edabf53aae40e53ab44b");

        for (var index = 0; index < 255; index++)
        {
            var opcode = (ushort)((index + 254) % 255 + 255);
            var frame = PacketFrame_ClanServer.Encode(opcode, [0]);
            Assert.Equal(table[index], frame[4]);
        }
    }

    [Fact]
    public void HeaderSize_Is4()
    {
        Assert.Equal(4, PacketFrame_ClanServer.HeaderSize);
    }

    [Fact]
    public void MaxBodyBytes_Is65531()
    {
        Assert.Equal(0xFFFB, PacketFrame_ClanServer.MaxBodyBytes);
    }
}
