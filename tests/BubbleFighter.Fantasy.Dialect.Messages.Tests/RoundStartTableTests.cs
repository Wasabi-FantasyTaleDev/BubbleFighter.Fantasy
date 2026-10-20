using BubbleFighter.Fantasy.Dialect.Rooms;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins round-loading and start bytes against independently audited native field counts.</summary>
public sealed class RoundStartTableTests
{
    [Theory]
    [InlineData("kr-812", 3)]
    [InlineData("cn-717", 3)]
    [InlineData("cn-324", 2)]
    [InlineData("cn-279", 2)]
    [InlineData("id-400", 2)]
    [InlineData("cn-406", 2)]
    [InlineData("id-1809", 3)]
    [InlineData("cn-706", 3)]
    public void LoadingFlagsAndStartWordsFollowTheNativeReader(string build, int fields)
    {
        var flags = TutorialWire.WriteEverybodyLoadingComplete(TestBuilds.Packets, build, true, false, true);
        Assert.Equal(fields == 3 ? new byte[] { 1, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0 }
            : new byte[] { 1, 0, 0, 0, 0, 0, 0, 0 }, flags);
        var start = TutorialWire.WriteGameStartGame(TestBuilds.Packets, build, 0x1020304, -1, 0x5060708);
        Assert.Equal(fields == 3 ? new byte[] { 4, 3, 2, 1, 255, 255, 255, 255, 8, 7, 6, 5 }
            : new byte[] { 4, 3, 2, 1, 255, 255, 255, 255 }, start);
        Assert.Equal(new byte[] { 7, 0, 0, 0 },
            TutorialWire.WriteMapLoadingComplete(TestBuilds.Packets, build, 7));
        Assert.Equal(new byte[] { 184, 11, 0, 0 },
            TutorialWire.WriteGameCountDown(TestBuilds.Packets, build, 3000));
    }
}
