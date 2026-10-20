using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins tutorial and ordinary-room control words across every audited legacy build.</summary>
public sealed class TutorialScalarTableTests
{
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("cn-324")]
    [InlineData("cn-279")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-706")]
    public void MasterAndWaveBodiesPreserveTheirNativeFourByteWords(string build)
    {
        Assert.Equal(new byte[] { 4, 3, 2, 1 },
            TutorialWire.WriteMasterSlot(TestBuilds.Packets, build, 0x01020304));
        Assert.Equal(new byte[] { 255, 255, 255, 255 },
            TutorialWire.WriteStartNewWave(TestBuilds.Packets, build, -1));
        Assert.Equal(-1, TutorialWire.ReadStartNewWave(TestBuilds.Packets, build,
            new byte[] { 255, 255, 255, 255 }));
        var spawn = TutorialWire.WriteCreateMobsFromSpawnGroup(TestBuilds.Packets, build, 0x01020304);
        Assert.Equal(new byte[] { 4, 3, 2, 1 }, spawn);
        Assert.Equal(0x01020304, TutorialWire.ReadCreateMobsFromSpawnGroup(TestBuilds.Packets, build, spawn));
        Assert.True(TutorialWire.ReadEndStage(TestBuilds.Packets, build, new byte[] { 1, 0, 0, 0 }));
        Assert.False(TutorialWire.ReadEndStage(TestBuilds.Packets, build, new byte[4]));
        Assert.Throws<PacketFormatException>(() =>
            TutorialWire.ReadEndStage(TestBuilds.Packets, build, new byte[] { 2, 0, 0, 0 }));
        foreach (var malformed in new[] { new byte[3], new byte[5] })
        {
            Assert.Throws<PacketFormatException>(() => TutorialWire.ReadStartNewWave(TestBuilds.Packets, build, malformed));
            Assert.Throws<PacketFormatException>(() => TutorialWire.ReadCreateMobsFromSpawnGroup(TestBuilds.Packets, build, malformed));
            Assert.Throws<PacketFormatException>(() => TutorialWire.ReadEndStage(TestBuilds.Packets, build, malformed));
        }
    }

    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("cn-324")]
    [InlineData("cn-279")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-706")]
    public void LoadingReportsPreserveFourWordsAndRejectIncompleteOrTrailingBodies(string build)
    {
        var body = Convert.FromHexString("FFFFFFFF040302011F27000000000080");
        var report = TutorialWire.ReadMapLoadingComplete(TestBuilds.Packets, build, body);
        Assert.Equal(-1, report.Unknown0);
        Assert.Equal(0x01020304, report.Unknown1);
        Assert.Equal(0x271F, report.Unknown2);
        Assert.Equal(int.MinValue, report.Unknown3);
        for (var length = 0; length < body.Length; length++)
        {
            var truncated = body[..length];
            Assert.Throws<PacketFormatException>(() =>
                TutorialWire.ReadMapLoadingComplete(TestBuilds.Packets, build, truncated));
        }
        Assert.Throws<PacketFormatException>(() =>
            TutorialWire.ReadMapLoadingComplete(TestBuilds.Packets, build, new byte[17]));
    }

    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("id-1809")]
    public void AuditedHostStartBodiesPreserveTheFourByteResult(string build) =>
        Assert.Equal(new byte[] { 7, 0, 0, 0 },
            TutorialWire.WriteUserHostStart(TestBuilds.Packets, build, 7));

    [Theory]
    [InlineData("cn-324")]
    [InlineData("cn-279")]
    [InlineData("id-400")]
    [InlineData("cn-706")]
    public void BuildsWithoutAnAuditedHostStartClassHaveNoHostStartFields(string build) =>
        Assert.Empty(TestBuilds.Packets.Resolve(build, "TutorialHostStart"));
}
