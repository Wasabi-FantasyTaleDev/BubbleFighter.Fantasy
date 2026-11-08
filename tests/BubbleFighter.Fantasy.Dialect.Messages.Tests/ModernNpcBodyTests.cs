using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the modern Korean, Chinese and Taiwanese NPC bodies against each build's own wave and spawner readers.</summary>
public sealed class ModernNpcBodyTests
{
    /// <summary>Each mounted operation preserves its complete native body and rejects truncated or trailing data.</summary>
    [Theory]
    [InlineData("kr-1256", "gcpReqStartNewWave", "gspRepStartNewWave", RoundNpcOperation.StartWave, 903, 904, 4, "01000000")]
    [InlineData("kr-1256", "gcpReqEndWave", "gspRepEndWave", RoundNpcOperation.ClearWave, 904, 905, 4, "02000000")]
    [InlineData("kr-1256", "gcpReqEndAllWave", "gspRepEndAllWave", RoundNpcOperation.CompleteWaves, 905, 906, 0, "")]
    [InlineData("kr-1256", "gcpReqWaveAllSpawnMobs", "gspRepWaveAllSpawnMobs", RoundNpcOperation.SpawnTable, 908, 909, 40,
        "0100000001000300000001000000F4010000040000002E00440061006F0000000000010000000102")]
    [InlineData("kr-1256", "gppCreateMobsFromSpawnGroup", "gppCreateMobsFromSpawnGroup", RoundNpcOperation.SpawnGroup, 32857, 32857, 4, "04030201")]
    [InlineData("kr-1256", "gppCreateSpawnGroupMemberMob", "gppCreateSpawnGroupMemberMob", RoundNpcOperation.SpawnMember, 32862, 32862, 30,
        "010000000200000001000000410003000000040000000500000006000000")]
    [InlineData("kr-1256", "gppCreateAIObject", "gppCreateAIObject", RoundNpcOperation.SpawnActor, 32892, 32892, 36,
        "010000000200000003000000040000000500000006000000070000000800000009000000")]
    [InlineData("kr-1256", "gppHordeMobReplay", "gppHordeMobReplay", RoundNpcOperation.ReplayActor, 32866, 32866, 18,
        "010000000200000001000000410003000000")]
    [InlineData("kr-1256", "gppWaveSetMobSpawnGroup", "gppWaveSetMobSpawnGroup", RoundNpcOperation.AssignSpawnGroup, 32873, 32873, 6,
        "010204030201")]
    [InlineData("cn-774", "gcpReqStartNewWave", "gspRepStartNewWave", RoundNpcOperation.StartWave, 903, 904, 4, "01000000")]
    [InlineData("cn-774", "gcpReqEndWave", "gspRepEndWave", RoundNpcOperation.ClearWave, 904, 905, 4, "02000000")]
    [InlineData("cn-774", "gcpReqEndAllWave", "gspRepEndAllWave", RoundNpcOperation.CompleteWaves, 905, 906, 0, "")]
    [InlineData("cn-774", "gcpReqWaveAllSpawnMobs", "gspRepWaveAllSpawnMobs", RoundNpcOperation.SpawnTable, 908, 909, 40,
        "0100000001000300000001000000F4010000040000002E00440061006F0000000000010000000102")]
    [InlineData("cn-774", "gppCreateMobsFromSpawnGroup", "gppCreateMobsFromSpawnGroup", RoundNpcOperation.SpawnGroup, 32857, 32857, 4, "04030201")]
    [InlineData("cn-774", "gppCreateSpawnGroupMemberMob", "gppCreateSpawnGroupMemberMob", RoundNpcOperation.SpawnMember, 32862, 32862, 30,
        "010000000200000001000000410003000000040000000500000006000000")]
    [InlineData("cn-774", "gppCreateAIObject", "gppCreateAIObject", RoundNpcOperation.SpawnActor, 32892, 32892, 32,
        "0100000002000000030000000400000005000000060000000700000008000000")]
    [InlineData("cn-774", "gppHordeMobReplay", "gppHordeMobReplay", RoundNpcOperation.ReplayActor, 32866, 32866, 18,
        "010000000200000001000000410003000000")]
    [InlineData("cn-774", "gppWaveSetMobSpawnGroup", "gppWaveSetMobSpawnGroup", RoundNpcOperation.AssignSpawnGroup, 32873, 32873, 6,
        "010204030201")]
    [InlineData("tw-454", "gcpReqStartNewWave", "gspRepStartNewWave", RoundNpcOperation.StartWave, 903, 904, 4, "01000000")]
    [InlineData("tw-454", "gcpReqEndWave", "gspRepEndWave", RoundNpcOperation.ClearWave, 904, 905, 4, "02000000")]
    [InlineData("tw-454", "gcpReqEndAllWave", "gspRepEndAllWave", RoundNpcOperation.CompleteWaves, 905, 906, 0, "")]
    [InlineData("tw-454", "gcpReqWaveAllSpawnMobs", "gspRepWaveAllSpawnMobs", RoundNpcOperation.SpawnTable, 908, 909, 40,
        "0100000001000300000001000000F4010000040000002E00440061006F0000000000010000000102")]
    [InlineData("tw-454", "gppCreateMobsFromSpawnGroup", "gppCreateMobsFromSpawnGroup", RoundNpcOperation.SpawnGroup, 32857, 32857, 4, "04030201")]
    [InlineData("tw-454", "gppCreateSpawnGroupMemberMob", "gppCreateSpawnGroupMemberMob", RoundNpcOperation.SpawnMember, 32862, 32862, 30,
        "010000000200000001000000410003000000040000000500000006000000")]
    [InlineData("tw-454", "gppCreateAIObject", "gppCreateAIObject", RoundNpcOperation.SpawnActor, 32892, 32892, 32,
        "0100000002000000030000000400000005000000060000000700000008000000")]
    [InlineData("tw-454", "gppHordeMobReplay", "gppHordeMobReplay", RoundNpcOperation.ReplayActor, 32866, 32866, 18,
        "010000000200000001000000410003000000")]
    [InlineData("tw-454", "gppWaveSetMobSpawnGroup", "gppWaveSetMobSpawnGroup", RoundNpcOperation.AssignSpawnGroup, 32873, 32873, 6,
        "010204030201")]
    public async Task ModernNpcRoutesValidateTheirOwnCompleteBodies(string build, string requestName, string replyName,
        RoundNpcOperation operation, int requestOpcode, int replyOpcode, int length, string hex)
    {
        var profile = new ClientProfile(build, build[..2], int.Parse(build[3..]), 0, 0, build);
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var full = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile);
        var table = full.ForConnection(OpcodeConnection.Game);
        Assert.Equal(requestOpcode, table.C2S(requestName));
        Assert.Equal(replyOpcode, table.S2C(replyName));
        using var fixture = new MatchMessagesMountTests.Fixture(full);
        var context = fixture.Context(build, build + "/game", "dialect.rooms");
        var session = new MatchMessagesMountTests.Session(profile);
        var body = Convert.FromHexString(hex);
        Assert.Equal(length, body.Length);
        try
        {
            new NpcBinding(build + "/game", table, TestBuilds.Packets, build, NullLogger.Instance).Mount(context);
            await Request(body);
            var report = Assert.IsType<RoundNpcReported>(Assert.Single(fixture.Messages.Received));
            Assert.Equal(operation, report.Operation);
            Assert.Equal(body, report.Payload.ToArray());
            var peer = operation is RoundNpcOperation.SpawnMember
                or RoundNpcOperation.SpawnActor or RoundNpcOperation.ReplayActor or RoundNpcOperation.AssignSpawnGroup;
            Assert.Equal(peer, report.PeerFeed is not null);
            if (report.PeerFeed is { } feed)
            {
                Assert.Equal(replyOpcode, feed.Code);
                Assert.Equal(body, feed.Payload.ToArray());
                Assert.True(feed.OnRelay);
                Assert.False(feed.IncludeSender);
            }
            var sender = Assert.Single(fixture.Services.GetAll<IMessageSender<RoundNpcAcknowledged>>());
            await sender.SendAsync(session, new(operation, report.Payload), default);
            if (operation == RoundNpcOperation.SpawnTable)
            {
                Assert.Equal(new ushort[] { table.S2C("gspResetWaveMobs_"), table.S2C(replyName) }, session.Sent);
                Assert.Empty(session.Bodies[0]);
            }
            else
                Assert.Equal(table.S2C(replyName), Assert.Single(session.Sent));
            Assert.Equal(body, session.Bodies[^1]);
            if (body.Length > 0)
                await Assert.ThrowsAsync<PacketFormatException>(() => Request(body[..^1]));
            await Assert.ThrowsAsync<PacketFormatException>(() => Request([.. body, 0]));
            Assert.Single(fixture.Messages.Received);
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IMessageSender<RoundNpcAcknowledged>>());

        Task Request(byte[] payload) => fixture.Packets.DispatchAsync(build + "/game", session,
            new(table.C2S(requestName), payload), default).AsTask();
    }

    /// <summary>The China wave requests no longer resolve under the unrelated manufacturing names.</summary>
    [Fact]
    public void ChinaWaveRequestsRetainOnlyTheirVerifiedNames()
    {
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var profile = new ClientProfile("cn-774", "cn", 774, 0, 0, "cn-774");
        var table = OpcodeTableLoader.Load(Path.Combine(root, "profiles", "cn-774", "opcodes.yaml"), profile)
            .ForConnection(OpcodeConnection.Game);
        Assert.Equal(903, table.C2S("gcpReqStartNewWave"));
        Assert.Equal(906, table.C2S("Unknown906"));
        Assert.Null(table.Find(PacketDirection.ClientToServer, "gcpReqManufactureStart"));
        Assert.Null(table.Find(PacketDirection.ClientToServer, "gcpReqManufactureImmediate"));
    }

    /// <summary>China and Taiwan use their proven shorter actor body while Korea retains its own 36-byte form.</summary>
    [Theory]
    [InlineData("kr-1256", 9)]
    [InlineData("cn-774", 8)]
    [InlineData("tw-454", 8)]
    public void PresetActorLengthIsSelectedByThePacketTable(string build, int words)
    {
        var values = new Dictionary<string, object?> { ["unknown_0"] = Enumerable.Range(1, words).Cast<object?>().ToArray() };
        var body = PacketCodec.Write(TestBuilds.Packets, build, "NpcSpawnActor", values);
        Assert.Equal(words * 4, body.Length);
        Assert.Equal(Enumerable.Range(1, words).SelectMany(word => BitConverter.GetBytes(word)), body);
        _ = PacketCodec.Read(TestBuilds.Packets, build, "NpcSpawnActor", body);
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, build, "NpcSpawnActor", body[..^4]));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, build, "NpcSpawnActor", [.. body, 0, 0, 0, 0]));
    }
}
