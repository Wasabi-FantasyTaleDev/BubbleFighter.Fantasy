using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.InGame.Wire;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>
/// Byte-length pins for C2S 32781, read by the fallback dispatcher 0x6D8E90.
/// </summary>
public sealed class InPlayReportTests
{
    /// <summary>A reference frame, byte for byte.</summary>
    private static byte[] CapturedFrame() =>
    [
        0x40, 0x42, 0x0F, 0x00,             // +0 identity 1000000
        0x22, 0x15, 0x87, 0x43,             // +4 sequence
        0x01, 0x00, 0x00, 0x00,             // +8 flag 1
    ];

    /// <summary>The report parses to the reference field values and consumes the body exactly.</summary>
    [Fact]
    public void CapturedFrameParsesToItsFieldValues()
    {
        var report = InPlayReport.Parse(CapturedFrame());

        Assert.Equal(1_000_000, report.Identity);
        Assert.Equal(unchecked((int)0x43871522), report.Sequence);
        Assert.Equal(1, report.Flag);
    }

    /// <summary>A body one byte short or one byte long is rejected.</summary>
    [Fact]
    public void WrongLengthIsRejected()
    {
        var shortBody = CapturedFrame().AsSpan(0, InPlayReport.BodyLength - 1).ToArray();
        Assert.Throws<PacketFormatException>(() => InPlayReport.Parse(shortBody));

        var longBody = CapturedFrame().Concat(new byte[] { 0x00 }).ToArray();
        Assert.Throws<PacketFormatException>(() => InPlayReport.Parse(longBody));
    }
}
