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
    /// <summary>Both native evidence paths credit the same killer, victim, team and live scoreboard.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedFinishingBlowsSharePersonalAndTeamAccounting(bool bubbleContact)
    {
        var services = new ServiceRegistry();
        var context = new PluginContext(new PluginRow { Id = "shared-scoring", Plugin = "test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        var scores = new Sink<RoundScoreUpdated>();
        context.Effect(services.RegisterMany<IMessageSender<RoundScoreUpdated>>(scores));
        context.Effect(services.RegisterMany<IMessageSender<RoundScoreboard>>(new Sink<RoundScoreboard>()));
        context.Effect(services.RegisterMany<IMessageSender<RoundEnded>>(new Sink<RoundEnded>()));
        context.Effect(services.RegisterMany<IMessageSender<ResultStageAdvanced>>(new Sink<ResultStageAdvanced>()));
        var rig = new Rig(resultContext: context, configure: options => options.NativeLifeEveryMode = true);
        try
        {
            await rig.StartAsync();
            var room = rig.Registry.Find(rig.RoomId)!;
            var host = room.FindByGame(rig.Host.Id)!;
            var guest = room.FindByGame(rig.Guest.Id)!;
            lock (room.Gate)
            {
                room.MarkPlaying();
                host.Team = 1;
                guest.Team = 2;
            }
            if (bubbleContact)
            {
                await rig.Round.HandleLifeReportedAsync(rig.Guest,
                    new(RoundLifeEvent.Bubbled, RoomRegistry.SceneEntity(guest.Slot)), default);
                await rig.Round.HandleLifeReportedAsync(rig.Host,
                    new(RoundLifeEvent.Finished, RoomRegistry.SceneEntity(guest.Slot))
                        { Initiator = RoomRegistry.SceneEntity(host.Slot) }, default);
            }
            else
                await rig.Round.HandleFinishingBlowReportedAsync(rig.Host,
                    new(host.Slot) { HasVictimReference = true, Victim = guest.Slot, RequiresReporterOwnership = true }, default);

            foreach (var recipient in new[] { rig.Host, rig.Guest })
            {
                var update = Assert.Single(scores.To(recipient));
                Assert.Equal([1, 0], update.TeamTotals);
                Assert.Equal(1, update.ScoringTeam);
                Assert.Collection(update.Seats,
                    stats => Assert.Equal(new RoundSeatStats(host.Slot, 1, 0, 2, 0), stats),
                    stats => Assert.Equal(new RoundSeatStats(guest.Slot, 0, 0, 0, 1), stats));
                Assert.NotNull(update.Scoreboard);
                Assert.Equal(1, update.Scoreboard.Rows.Single(row => row.Slot == host.Slot).Stats.Kills);
                Assert.Equal(1, update.Scoreboard.Rows.Single(row => row.Slot == guest.Slot).Stats.Deaths);
            }
            // A score report is accounting evidence, not a replacement for that client's separate death report.
            Assert.Equal(bubbleContact ? SeatLife.Dead : SeatLife.Alive, guest.Life.State);
        }
        finally { await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }
}
