using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Sessions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    /// <summary>Only a normal map spawner supplies a boss clock; result-scene actors and the test boss are not objectives.</summary>
    [Fact]
    public void ZombiePveBossUsesNumberedGameplayProperties()
    {
        const string ini = """
            [CHR_PROPERTY]
            CHR_PROPERTY_COUNT=3
            CHR_PROPERTY_2=[HP:20][Name:Zombie_Boss]
            CHR_PROPERTY_0=[Name:Zombie_Walking]
            CHR_PROPERTY_1=[Name:Zombie_Boss_Test]
            [RESULTINFO]
            CHR_SPAWNER_0=[NameID:2][SartTime:0]
            [DIFFICULTY_0]
            CHR_SPAWNER_1=[NameID:2][SartTime:185000]
            """;
        Assert.Equal(new ZombiePveBoss(2, 185000), MapCatalog.ReadZombiePveBoss(ini));
        Assert.Null(MapCatalog.ReadZombiePveBoss(ini.Replace("[DIFFICULTY_0]", "[RESULTINFO]", StringComparison.Ordinal)));
        Assert.Null(MapCatalog.ReadZombiePveBoss(ini.Replace("CHR_PROPERTY_1=", "CHR_PROPERTY_7=", StringComparison.Ordinal)));
    }

    /// <summary>Two members share one master-reported outcome; ordinary monsters, early finishes and other reporters cannot clear the stage.</summary>
    [Theory]
    [InlineData(0, RoundEndReason.CooperativeFailed)]
    [InlineData(1, RoundEndReason.WavesCleared)]
    public async Task ZombiePveBossNeedsCurrentGameplaySpawnBeforeVictory(int outcome, RoundEndReason reason)
    {
        var rig = new Rig(configure: options => options.ResultCloseGraceMs = 60_000);
        var earliest = int.MaxValue;
        rig.Round.ZombiePveBossFor = _ => new(31, earliest);
        try
        {
            await rig.Rooms.HandleCreateAsync(rig.Host, new("test", "", 16, "stage"), default);
            var room = rig.Registry.TryGet(rig.Host.Id)!;
            await rig.Rooms.HandleJoinAsync(rig.Guest, new(room.Id, ""), default);
            await rig.Rooms.HandleReadyAsync(rig.Guest, new(true), default);
            await rig.Rooms.HandleStartAsync(rig.Host, new("stage"), default);
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleStageResultAsync(rig.Host, new(1), default);
            Assert.Equal(RoomPhase.Starting, room.Phase);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            var ack = new Sink<RoundNpcAcknowledged>();
            RoundNpcReported Spawn(int index) => new(RoundNpcOperation.SpawnActor, new byte[32])
                { PeerFeed = new(32892, new byte[32]) { OnRelay = true }, MapPropertyIndex = index };
            await rig.Round.HandleNpcAsync(rig.Host, Spawn(0), ack, default);
            await rig.Round.HandleNpcAsync(rig.Guest, Spawn(31), ack, default);
            await rig.Round.HandleEndRequestedAsync(rig.Host, new(), default);
            await rig.Round.HandleStageResultAsync(rig.Host, new(1), default);
            Assert.Null(room.ZombiePveBossSpawn);
            Assert.Equal(RoomPhase.Playing, room.Phase);
            await rig.Round.HandleNpcAsync(rig.Host, Spawn(31), ack, default);
            await rig.Round.HandleStageResultAsync(rig.Host, new(1), default);
            Assert.Equal(RoomPhase.Playing, room.Phase);
            earliest = 0;
            await rig.Round.HandleNpcAsync(rig.Host, Spawn(31), ack, default);
            await rig.Round.HandleStageResultAsync(rig.Guest, new(1), default);
            await rig.Round.HandleStageResultAsync(CaptainPlayer(rig.Host.Id, "replacement"), new(1), default);
            Assert.Equal(RoomPhase.Playing, room.Phase);
            await rig.Round.HandleStageResultAsync(rig.Host, new(outcome), default);
            await rig.Round.HandleStageResultAsync(rig.Host, new(outcome), default);
            Assert.Equal(RoomPhase.Result, room.Phase);
            Assert.Equal(outcome == 1 ? 2 : 35, room.DecidedWinnerTeam);
            foreach (var member in new[] { rig.Host, rig.Guest })
                Assert.Equal(reason, Assert.Single(rig.Ended.To(member)).Reason);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }
}
