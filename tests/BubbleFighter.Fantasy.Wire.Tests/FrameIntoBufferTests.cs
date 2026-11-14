using BubbleFighter.Fantasy.Wire;
using Xunit;

namespace BubbleFighter.Fantasy.Wire.Tests;

/// <summary>
/// Encoding a frame into a caller buffer writes the same bytes as the array encoder, whatever the buffer held
/// before, and allocates nothing: the session writes frames straight into its output pipe's reused memory.
/// </summary>
public sealed class FrameIntoBufferTests
{
    private delegate byte[] ArrayEncoder(ushort opcode, ReadOnlySpan<byte> payload);

    private delegate int SpanEncoder(ushort opcode, ReadOnlySpan<byte> payload, Span<byte> destination);

    public static TheoryData<string> Kinds => ["legacy_5", "modern_8", "cn_10"];

    private static (ArrayEncoder Array, SpanEncoder Span, int Header) Encoders(string kind) => kind switch
    {
        "legacy_5" => (PacketFrame_5Bytes.Encode, PacketFrame_5Bytes.Encode, PacketFrame_5Bytes.HeaderSize),
        "modern_8" => (PacketFrame_8Bytes.Encode, PacketFrame_8Bytes.Encode, PacketFrame_8Bytes.HeaderSize),
        _ => (PacketFrame_10Bytes.Encode, PacketFrame_10Bytes.Encode, PacketFrame_10Bytes.HeaderSize),
    };

    [Theory]
    [MemberData(nameof(Kinds))]
    public void DirtyBufferReceivesTheArrayEncodersBytes(string kind)
    {
        var (array, span, header) = Encoders(kind);
        byte[] payload = [1, 2, 3, 0x80, 0xFF];
        var buffer = Enumerable.Repeat((byte)0xAA, header + payload.Length + 4).ToArray();

        var written = span(0x1234, payload, buffer);

        Assert.Equal(header + payload.Length, written);
        Assert.Equal(array(0x1234, payload), buffer[..written]);
        Assert.All(buffer[written..], value => Assert.Equal(0xAA, value));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void EncodingIntoABufferAllocatesNothing(string kind)
    {
        var (_, span, header) = Encoders(kind);
        var payload = new byte[64];
        var buffer = new byte[header + payload.Length];
        span(7, payload, buffer);

        var before = GC.GetAllocatedBytesForCurrentThread();
        span(7, payload, buffer);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void ShortBufferIsRefused(string kind)
    {
        var (_, span, header) = Encoders(kind);
        var buffer = new byte[header + 2];

        Assert.Throws<ArgumentException>(() => span(7, new byte[3], buffer));
    }
}
