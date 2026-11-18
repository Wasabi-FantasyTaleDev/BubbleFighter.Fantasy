using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Checks the separate failure notice, stage word and result surface used by the KR pirate assault.</summary>
public sealed class PirateAssaultWaveTests
{
    /// <summary>The wave and tutorial rows coexist on 906, each build decodes its own 624 loss shape, and 907 has no body.</summary>
    [Theory]
    [InlineData("kr-1256", 8)]
    [InlineData("cn-774", 4)]
    [InlineData("tw-454", 4)]
    public async Task PirateAssaultUsesOneSharedFailureDecoderAndAnEmptyWaveStateReply(string build, int stageBytes)
    {
        var protocol = build + "/game";
        var table = TestBuilds.Opcodes(build);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(build, protocol);
        try
        {
            var npc = new NpcBinding(protocol, table, TestBuilds.Packets, build, NullLogger.Instance);
            npc.Mount(context);
            if (build == "kr-1256") new TutorialNpcBinding(protocol, table, TestBuilds.Packets, build, NullLogger.Instance).Mount(context);
            new StageResultBinding(protocol, table, TestBuilds.Packets, build, NullLogger.Instance).Mount(context);
            var session = new MatchMessagesMountTests.Session(table.Profile);
            await fixture.Packets.DispatchAsync(protocol, session, new(906, ReadOnlyMemory<byte>.Empty), default);
            var failed = Assert.IsType<RoundNpcReported>(Assert.Single(fixture.Messages.Received));
            Assert.Equal(RoundNpcOperation.FailWaves, failed.Operation);
            Assert.True(failed.Payload.IsEmpty);
            await Assert.ThrowsAsync<PacketFormatException>(() => fixture.Packets.DispatchAsync(protocol, session, new(906, new byte[1]), default).AsTask());
            await fixture.Packets.DispatchAsync(protocol, session, new(624, new byte[stageBytes]), default);
            Assert.Equal(new RoundStageResultReported(0), Assert.IsType<RoundStageResultReported>(fixture.Messages.Received.Last()));
            await npc.SendAsync(session, new(failed.Operation, failed.Payload), default);
            Assert.Equal((ushort)907, Assert.Single(session.Sent));
            Assert.Empty(Assert.Single(session.Bodies));
        }
        finally { await context.UnwindAsync(); }
    }

    /// <summary>The effective KR mode list permits the same 614 team scoreboard consumed by DlgGameResult_Horde for mode 106.</summary>
    [Fact]
    public async Task PirateAssaultPublishesTheKoreanScoreboardBeforeReturningToRoom()
    {
        const string build = "kr-1256", protocol = build + "/game";
        var table = TestBuilds.Opcodes(build);
        var profiles = Path.Combine(Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName, "profiles");
        var rows = new ProfileComposer(profiles, key =>
            key is "BFF_MYSQL_PASSWORD" or "BFF_ADMIN_TOKEN" ? "isolated-test-placeholder" : null).Compose(build, []);
        var options = new PluginConfig(Assert.Single(rows, row => row.Id == "dialect.rooms")).Bind<RoomsOptions>();
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(build, protocol);
        try
        {
            new RoundResultBinding(protocol, table, TestBuilds.Packets, build, options.ResultScoreboardModes, NullLogger.Instance).Mount(context);
            var sender = Assert.Single(fixture.Services.GetAll<IMessageSender<RoundScoreboard>>());
            var session = new MatchMessagesMountTests.Session(table.Profile);
            await sender.SendAsync(session, new RoundScoreboard(1, 2, [8, 12], [], true) { ModeId = 106 }, default);
            Assert.Equal((ushort)614, Assert.Single(session.Sent));
            var reader = new PacketReader(Assert.Single(session.Bodies));
            Assert.Equal((byte)2, PacketCodec.ReadPrefix(ref reader, TestBuilds.Packets, build, "ScoreDataLead")["winner_slot"]);
            foreach (var total in new[] { 8, 12 })
                Assert.Equal(total, PacketCodec.ReadPrefix(ref reader, TestBuilds.Packets, build, "ScoreHeadEntry")["team_total"]);
        }
        finally { await context.UnwindAsync(); }
    }
}
