using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins round-loading and start bytes against independently audited native field counts.</summary>
public sealed class RoundStartTableTests
{
    /// <summary>Pins native modern countdown, start and empty unlimited-clock frames independently of round enablement.</summary>
    [Theory]
    [InlineData("kr-1256", 607, 608, 609, "04030201FFFFFFFF")]
    [InlineData("cn-774", 601, 602, 638, "04030201FFFFFFFF")]
    [InlineData("tw-454", 601, 602, 638, "04030201FFFFFFFF08070605")]
    public void ModernStartsUseNativeOpcodeSpacesAndWordCounts(string build, int countdown, int start, int unbounded, string body)
    {
        var profile = new ClientProfile(build, build[..2], int.Parse(build[3..]), 0, 0, build);
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var table = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile);
        Assert.Equal((ushort)countdown, table.S2C("gspGameCountDown"));
        Assert.Equal((ushort)start, table.S2C("gspGameStartGame"));
        Assert.Equal((ushort)unbounded, table.S2C("gspGameClockUnbounded_"));
        Assert.Equal(Convert.FromHexString(body),
            TutorialWire.WriteGameStartGame(TestBuilds.Packets, build, 0x1020304, -1, 0x5060708));
        Assert.Equal(Convert.FromHexString("B80B0000"),
            TutorialWire.WriteGameCountDown(TestBuilds.Packets, build, 3000));
        Assert.Empty(PacketCodec.Write(TestBuilds.Packets, build, "RoundClockUnbounded", new Dictionary<string, object?>()));
    }

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
