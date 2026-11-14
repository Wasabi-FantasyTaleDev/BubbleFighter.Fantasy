using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

public sealed class TutorialPracticeWireTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void NativeEventNoticePinsTheUnalignedCompleteElement(bool cleared, int progress)
    {
        var bytes = TutorialPracticeBinding.Write(TestBuilds.Packets, "cn-279", cleared);
        Assert.Equal(87, bytes.Length);
        Assert.Equal(1, BitConverter.ToInt32(bytes, 0));
        Assert.Equal(23, BitConverter.ToInt32(bytes, 4));
        Assert.Equal(progress, BitConverter.ToInt32(bytes, 8));
        Assert.All(bytes.Skip(12), value => Assert.Equal(0, value));
        var decoded = PacketCodec.Read(TestBuilds.Packets, "cn-279", "TutorialPracticeNotice", bytes);
        Assert.Equal(bytes, PacketCodec.Write(TestBuilds.Packets, "cn-279", "TutorialPracticeNotice", decoded));
    }
}
