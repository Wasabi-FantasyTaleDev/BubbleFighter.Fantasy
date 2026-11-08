using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    /// <summary>Zombie score reports cannot invent a victim or claim another player's or a teammate's kill.</summary>
    [Theory]
    [InlineData("foreign-reporter")]
    [InlineData("self")]
    [InlineData("friendly")]
    [InlineData("missing-victim")]
    [InlineData("unknown-victim")]
    public async Task ZombieInvalidFinishingReportsPreserveLifeAndScore(string claim)
    {
        var (rig, context) = ZombieScoringRig();
        try
        {
            var third = (await StartZombiePvpAsync(rig, third: true))!;
            await rig.Rooms.HandleTeamChangeAsync(rig.Guest, new(), default);
            var room = rig.Registry.Find(rig.RoomId)!;
            var human = room.FindByGame(rig.Host.Id)!;
            var king = room.FindByGame(rig.Guest.Id)!;
            var friend = room.FindByGame(third.Id)!;
            var report = claim switch
            {
                "foreign-reporter" => new RoundFinishingBlowReported(king.Slot) { Victim = human.Slot, HasVictimReference = true },
                "self" => new RoundFinishingBlowReported(human.Slot) { Victim = human.Slot, HasVictimReference = true },
                "friendly" => new RoundFinishingBlowReported(human.Slot) { Victim = friend.Slot, HasVictimReference = true },
                "missing-victim" => new RoundFinishingBlowReported(human.Slot),
                "unknown-victim" => new RoundFinishingBlowReported(human.Slot) { Victim = 31, HasVictimReference = true },
                _ => throw new InvalidOperationException("Unknown invalid-report test case."),
            };
            await rig.Round.HandleFinishingBlowReportedAsync(rig.Host, report, default);
            Assert.Equal(RoomPhase.Playing, room.Phase);
            Assert.Null(room.DecidedWinnerTeam);
            Assert.All(room.Seats, seat =>
            {
                Assert.Equal(SeatLife.Alive, seat.Life.State);
                Assert.Equal(0, room.Scoring.Stats(seat.Slot).Kills);
                Assert.Equal(0, room.Scoring.Stats(seat.Slot).Deaths);
            });
        }
        finally { await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    /// <summary>Native death and score reports share one credit, and ordinary zombie conversion opens a new life.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ZombieOrdinaryDeathsDeduplicateAcrossPathsAndRevive(bool nativeFirst)
    {
        var (rig, context) = ZombieScoringRig();
        try
        {
            var third = (await StartZombiePvpAsync(rig, third: true))!;
            await rig.Rooms.HandleTeamChangeAsync(rig.Guest, new(), default);
            await rig.Rooms.HandleTeamChangeAsync(third, new(), default);
            var room = rig.Registry.Find(rig.RoomId)!;
            var human = room.FindByGame(rig.Host.Id)!;
            var zombie = room.FindByGame(third.Id)!;
            var death = new RoundLifeReported(RoundLifeEvent.Died, RoomRegistry.SceneEntity(zombie.Slot))
            {
                Initiator = RoomRegistry.SceneEntity(human.Slot),
            };
            var score = new RoundFinishingBlowReported(human.Slot) { Victim = zombie.Slot, HasVictimReference = true };
            for (var lives = 1; lives <= 2; lives++)
            {
                if (nativeFirst) await rig.Round.HandleLifeReportedAsync(third, death, default);
                await rig.Round.HandleFinishingBlowReportedAsync(rig.Host, score, default);
                await rig.Round.HandleLifeReportedAsync(third, death, default);
                await rig.Round.HandleFinishingBlowReportedAsync(rig.Host, score, default);
                Assert.Equal(SeatLife.Dead, zombie.Life.State);
                Assert.Equal(lives, room.Scoring.Stats(human.Slot).Kills);
                Assert.Equal(lives, room.Scoring.Stats(zombie.Slot).Deaths);
                Assert.Equal(RoomPhase.Playing, room.Phase);
                Assert.Null(room.DecidedWinnerTeam);
                await rig.Rooms.HandleTeamChangeAsync(third, new(), default);
                Assert.Equal(SeatLife.Alive, zombie.Life.State);
                Assert.Equal(14, zombie.Team);
                Assert.NotEqual(zombie.Slot, room.ZombieKingSlot);
            }
        }
        finally { await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    private static (Rig Rig, PluginContext Context) ZombieScoringRig()
    {
        var services = new ServiceRegistry();
        var context = new PluginContext(new PluginRow { Id = "zombie-score-guards", Plugin = "test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        context.Effect(services.RegisterMany<IMessageSender<RoundScoreUpdated>>(new Sink<RoundScoreUpdated>()));
        context.Effect(services.RegisterMany<IMessageSender<RoundScoreboard>>(new Sink<RoundScoreboard>()));
        context.Effect(services.RegisterMany<IMessageSender<RoundEnded>>(new Sink<RoundEnded>()));
        context.Effect(services.RegisterMany<IMessageSender<ResultStageAdvanced>>(new Sink<ResultStageAdvanced>()));
        var rig = new Rig(resultContext: context, configure: options =>
        {
            options.ZombiePvpModeIds = [4];
            options.ZombieKingDelayMs = 60000;
        });
        return (rig, context);
    }

    /// <summary>Legacy score evidence credits once; final death or completed conversion decides the battle.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ZombieLegacyFinishingReportDecidesTheNamedVictimsBattle(bool kingDies)
    {
        var services = new ServiceRegistry();
        var context = new PluginContext(new PluginRow { Id = "zombie-score", Plugin = "test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        var scores = new Sink<RoundScoreUpdated>();
        var ended = new Sink<RoundEnded>();
        context.Effect(services.RegisterMany<IMessageSender<RoundScoreUpdated>>(scores));
        context.Effect(services.RegisterMany<IMessageSender<RoundScoreboard>>(new Sink<RoundScoreboard>()));
        context.Effect(services.RegisterMany<IMessageSender<RoundEnded>>(ended));
        context.Effect(services.RegisterMany<IMessageSender<ResultStageAdvanced>>(new Sink<ResultStageAdvanced>()));
        var rig = new Rig(resultContext: context, configure: options =>
        {
            options.ZombiePvpModeIds = [4];
            options.ZombieKingDelayMs = 60000;
        });
        try
        {
            await StartZombiePvpAsync(rig);
            await rig.Rooms.HandleTeamChangeAsync(rig.Guest, new(), default);
            var room = rig.Registry.Find(rig.RoomId)!;
            var killer = room.FindByGame((kingDies ? rig.Host : rig.Guest).Id)!;
            var victim = room.FindByGame((kingDies ? rig.Guest : rig.Host).Id)!;
            var report = new RoundFinishingBlowReported(killer.Slot) { Victim = victim.Slot, HasVictimReference = true };
            await rig.Round.HandleFinishingBlowReportedAsync(killer.Game, report, default);
            await rig.Round.HandleFinishingBlowReportedAsync(killer.Game, report, default);
            Assert.Equal(SeatLife.Dead, victim.Life.State);
            Assert.Equal(RoomPhase.Playing, room.Phase);
            Assert.Null(room.DecidedWinnerTeam);
            Assert.Empty(ended.To(rig.Host));
            if (kingDies)
                await rig.Round.HandleLifeReportedAsync(victim.Game,
                    new(RoundLifeEvent.DeathCleaned, RoomRegistry.SceneEntity(victim.Slot)), default);
            if (!kingDies)
            {
                // cn-324 sub_67A690 checks conversion maps, not the character's death state.
                Assert.Equal(RoomPhase.Playing, room.Phase);
                Assert.Null(room.DecidedWinnerTeam);
                Assert.Empty(ended.To(rig.Host));
                await rig.Rooms.HandleTeamChangeAsync(victim.Game, new(), default);
                Assert.Equal(14, victim.Team);
            }
            Assert.Equal(RoomPhase.Result, room.Phase);
            Assert.Equal(kingDies ? 1 : 14, room.DecidedWinnerTeam);
            Assert.Equal(1, room.Scoring.Stats(killer.Slot).Kills);
            Assert.Equal(1, room.Scoring.Stats(victim.Slot).Deaths);
            await rig.Round.HandleEndRequestedAsync(victim.Game, new() { Reason = RoundEndReason.StageReported }, default);
            Assert.Equal(kingDies ? 1 : 14, room.DecidedWinnerTeam);
        }
        finally { await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    /// <summary>The king's native terminal life report cannot depend on its never-sent conversion request.</summary>
    [Fact]
    public async Task ZombieKingNativeDeathReachesTheHumanResult()
    {
        var rig = ZombiePvpRig();
        try
        {
            await StartZombiePvpAsync(rig);
            await rig.Rooms.HandleTeamChangeAsync(rig.Guest, new(), default);
            var room = rig.Registry.Find(rig.RoomId)!;
            var king = room.FindByGame(rig.Guest.Id)!;
            await rig.Round.HandleLifeReportedAsync(rig.Guest,
                new(RoundLifeEvent.DeathCleaned, RoomRegistry.SceneEntity(king.Slot)), default);
            Assert.Equal(RoundOutcome.Won, (await WaitEndedAsync(rig, rig.Host)).Outcome);
            Assert.Equal(RoundOutcome.Lost, (await WaitEndedAsync(rig, rig.Guest)).Outcome);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A departed last human ends a started multiplayer battle even if only the king remains.</summary>
    [Fact]
    public async Task ZombieLastHumanDepartureDoesNotWaitForTheHumanTimeout()
    {
        var rig = ZombiePvpRig();
        try
        {
            await StartZombiePvpAsync(rig);
            await rig.Rooms.HandleTeamChangeAsync(rig.Guest, new(), default);
            await rig.Rooms.HandleLeaveAsync(rig.Host, new(), default);
            var result = await WaitEndedAsync(rig, rig.Guest);
            Assert.Equal(RoundOutcome.Won, result.Outcome);
            Assert.NotEqual(RoundEndReason.TimeLimit, result.Reason);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A human's claimed death for the king's actor is still rejected.</summary>
    [Fact]
    public async Task ZombieForeignDeathCannotDefeatTheKing()
    {
        var rig = ZombiePvpRig();
        try
        {
            await StartZombiePvpAsync(rig);
            await rig.Rooms.HandleTeamChangeAsync(rig.Guest, new(), default);
            var room = rig.Registry.Find(rig.RoomId)!;
            await rig.Round.HandleLifeReportedAsync(rig.Host,
                new(RoundLifeEvent.Died, RoomRegistry.SceneEntity(room.ZombieKingSlot!.Value)), default);
            Assert.Equal(RoomPhase.Playing, room.Phase);
            Assert.Empty(rig.Ended.To(rig.Host));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>An anonymous stage flag only rechecks an already recorded death, never kills its sender or the king.</summary>
    [Fact]
    public async Task ZombieStageReportRechecksEvidenceWithoutInventingAVictim()
    {
        var rig = ZombiePvpRig();
        try
        {
            await StartZombiePvpAsync(rig);
            await rig.Rooms.HandleTeamChangeAsync(rig.Guest, new(), default);
            var room = rig.Registry.Find(rig.RoomId)!;
            var king = room.FindByGame(rig.Guest.Id)!;
            await rig.Round.HandleEndRequestedAsync(rig.Host, new() { StageSucceeded = true }, default);
            Assert.Equal(RoomPhase.Playing, room.Phase);
            Assert.Equal(SeatLife.Alive, king.Life.State);
            await rig.Round.HandleDeathReportedAsync(rig.Guest,
                new(RoomRegistry.SceneEntity(king.Slot), 10000), default);
            await rig.Round.HandleEndRequestedAsync(rig.Host, new() { StageSucceeded = true }, default);
            Assert.Equal(RoomPhase.Playing, room.Phase);
            await rig.Round.HandleLifeReportedAsync(rig.Guest,
                new(RoundLifeEvent.DeathCleaned, RoomRegistry.SceneEntity(king.Slot)), default);
            Assert.Equal(RoundOutcome.Won, (await WaitEndedAsync(rig, rig.Host)).Outcome);
            Assert.Equal(RoundOutcome.Lost, (await WaitEndedAsync(rig, rig.Guest)).Outcome);
            Assert.Empty(rig.Respawned.To(rig.Guest));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }
}
