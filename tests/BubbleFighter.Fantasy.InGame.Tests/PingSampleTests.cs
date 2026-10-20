using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.InGame.Wire;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>
/// Byte-length pins for the C2S 518 latency sample, whose body the emulator
/// parses from <c>sub_978F20</c>'s arguments.
/// </summary>
public sealed class PingSampleTests
{
    /// <summary>A reference frame, byte for byte.</summary>
    private static byte[] CapturedFrame() =>
    [
        0x00, 0x00, 0x00, 0x00,             // +0 reserved
        0x00, 0x00, 0x00, 0x00,             // +4 reserved
        0x0B, 0x00, 0x00, 0x00,             // +8 seat 11
        0x09, 0x43, 0x88, 0x42,             // +12 stamp
    ];

    /// <summary>The sample parses to the reference field values and consumes the body exactly.</summary>
    [Fact]
    public void CapturedFrameParsesToItsFieldValues()
    {
        var sample = PingSample.Parse(CapturedFrame());

        Assert.Equal(0, sample.Reserved0);
        Assert.Equal(0, sample.Reserved1);
        Assert.Equal(11, sample.Seat);
        Assert.Equal(unchecked((int)0x42884309), sample.Stamp);
    }

    /// <summary>A body one byte short or one byte long is rejected.</summary>
    [Fact]
    public void WrongLengthIsRejected()
    {
        var shortBody = CapturedFrame().AsSpan(0, PingSample.BodyLength - 1).ToArray();
        Assert.Throws<PacketFormatException>(() => PingSample.Parse(shortBody));

        var longBody = CapturedFrame().Concat(new byte[] { 0x00 }).ToArray();
        Assert.Throws<PacketFormatException>(() => PingSample.Parse(longBody));
    }
}
