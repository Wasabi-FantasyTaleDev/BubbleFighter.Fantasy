using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.InGame.Wire;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>
/// Direct body-length pin for C2S 25 <see cref="PostSwapReport"/> (A1 gap).
/// Length comes from the codec XML ("5 bytes") and <see cref="PostSwapReport.BodyLength"/>.
/// </summary>
public sealed class PostSwapReportCodecLengthTests
{
    [Fact]
    public void BodyLengthIsPinnedAtFiveBytesAndLiveFormParses()
    {
        // XML: one i32 actor id + one u8 preset slot (sub_6DA420).
        Assert.Equal(5, PostSwapReport.BodyLength);
        var body = new byte[] { 0x40, 0x42, 0x0F, 0x00, 0x03 };
        Assert.Equal(PostSwapReport.BodyLength, body.Length);
        var report = PostSwapReport.Parse(body);
        Assert.Equal(0x000F4240, report.ActorId);
        Assert.Equal((byte)3, report.Slot);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    public void ShortOrLongBodiesAreRejected(int length)
    {
        var body = new byte[length];
        Assert.Throws<PacketFormatException>(() => PostSwapReport.Parse(body));
    }
}
