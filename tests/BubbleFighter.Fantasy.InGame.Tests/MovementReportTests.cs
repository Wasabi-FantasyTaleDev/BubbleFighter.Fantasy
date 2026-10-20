using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.InGame.Wire;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>
/// Byte-length pins for the C2S 32885 movement report, whose body the emulator
/// parses from <c>sub_95E920</c>'s input record.
/// </summary>
public sealed class MovementReportTests
{
    /// <summary>A reference frame, byte for byte.</summary>
    private static byte[] CapturedFirstFrame() =>
    [
        0x40, 0x42, 0x0F, 0x00,             // object id 1000000
        0xD1, 0xBB, 0xA7, 0x44,             // x
        0x80, 0xA7, 0x25, 0x43,             // y
        0x78, 0x9A, 0xDA, 0x44,             // z
        0x00, 0x30, 0x15, 0x02, 0x00,       // yaw, pitch, state, u16 tick
    ];

    /// <summary>The report parses to the reference field values and consumes the body exactly.</summary>
    [Fact]
    public void CapturedFrameParsesToItsFieldValues()
    {
        var report = MovementReport.Parse(CapturedFirstFrame());

        Assert.Equal(1_000_000, report.ObjectId);
        Assert.Equal(BitConverter.Int32BitsToSingle(0x44A7BBD1), report.X);
        Assert.Equal(BitConverter.Int32BitsToSingle(0x4325A780), report.Y);
        Assert.Equal(BitConverter.Int32BitsToSingle(0x44DA9A78), report.Z);
        Assert.Equal((byte)0x00, report.Yaw);
        Assert.Equal((byte)0x30, report.Pitch);
        Assert.Equal((byte)0x15, report.State);
        Assert.Equal((ushort)2, report.Tick);
    }

    /// <summary>
    /// A body one byte short or one byte long is rejected rather than parsed
    /// optimistically. <see cref="PacketReader"/> throws on truncation and
    /// <see cref="WireUtil.RequireConsumed"/> throws on trailing bytes, so both
    /// directions surface as <see cref="PacketFormatException"/>.
    /// </summary>
    [Fact]
    public void ShortBodyIsRejected()
    {
        var shortBody = CapturedFirstFrame().AsSpan(0, MovementReport.BodyLength - 1).ToArray();
        Assert.Throws<PacketFormatException>(() => MovementReport.Parse(shortBody));
    }

    [Fact]
    public void LongBodyIsRejected()
    {
        var longBody = CapturedFirstFrame().Concat(new byte[] { 0x00 }).ToArray();
        Assert.Throws<PacketFormatException>(() => MovementReport.Parse(longBody));
    }

    /// <summary>
    /// Writing a parsed report back reproduces the body byte for byte. This is what
    /// makes the S2C 32885 echo safe: the reply is the same 21 bytes the client
    /// sent, so the writer cannot drift from the parser without this failing.
    /// </summary>
    [Fact]
    public void WritingAParsedReportReproducesTheBody()
    {
        var body = CapturedFirstFrame();
        var report = MovementReport.Parse(body);

        var written = WireLengthSupport.Written(report.Write);

        Assert.Equal(body, written);
    }

    /// <summary>
    /// The S2C 32885 body has the pinned length and parses back to the original field values,
    /// preserving the request shape.
    /// </summary>
    [Fact]
    public void WrittenBodyReparsesToTheSameValues()
    {
        var report = MovementReport.Parse(CapturedFirstFrame());

        var written = WireLengthSupport.Written(report.Write);
        var reparsed = MovementReport.Parse(written);

        Assert.Equal(MovementReport.BodyLength, written.Length);
        Assert.Equal(report, reparsed);
    }
}
