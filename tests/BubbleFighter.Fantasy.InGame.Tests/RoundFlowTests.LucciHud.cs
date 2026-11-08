using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    /// <summary>Publishes the authenticated pickup total once per world item without changing kills, team scores or rewards twice.</summary>
    [Fact]
    public async Task LucciHudTracksAcceptedPickupsAndPreservesCombatCounters()
    {
        var services = new ServiceRegistry();
        var context = new PluginContext(new PluginRow { Id = "coin-hud", Plugin = "test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        var scores = new Sink<RoundScoreUpdated>();
        context.Effect(services.RegisterMany<IMessageSender<RoundScoreUpdated>>(scores));
        context.Effect(services.RegisterMany<IMessageSender<RoundScoreboard>>(new Sink<RoundScoreboard>()));
        context.Effect(services.RegisterMany<IMessageSender<RoundEnded>>(new Sink<RoundEnded>()));
        context.Effect(services.RegisterMany<IMessageSender<ResultStageAdvanced>>(new Sink<ResultStageAdvanced>()));
        var rig = new Rig(resultContext: context, configure: options => options.MaxRoundLucci = 150);
        try
        {
            var room = await StartedRoundAsync(rig);
            var guest = room.FindByGame(rig.Guest.Id)!.Slot;
            foreach (var player in new[] { rig.Host, rig.Guest }) Assert.Empty(scores.To(player));
            await rig.Round.HandleLucciPickupAsync(rig.Guest, new(1000000, 999), default);
            foreach (var expected in new[] { 100, 150 })
            {
                await rig.Round.HandleLucciDropAsync(rig.Host, new(new(0, 0, 0, 0, 0, 0), 2, 0), default);
                var item = rig.LucciCreated.To(rig.Guest).Last();
                // A mismatched actor reference cannot redirect the authenticated recipient's credit.
                await rig.Round.HandleLucciPickupAsync(rig.Guest, new(1000000, item.ObjectId), default);
                await rig.Round.HandleLucciPickupAsync(rig.Host, new(1000000, item.ObjectId), default);
                foreach (var player in new[] { rig.Host, rig.Guest })
                {
                    var update = scores.To(player).Last();
                    Assert.True(update.CollectedLucciOnly);
                    Assert.Equal(0, update.ScoringTeam);
                    Assert.Equal(new[] { 0, 0 }, update.TeamTotals);
                    var stats = Assert.Single(update.Seats);
                    Assert.Equal(guest, stats.Slot);
                    Assert.Equal(expected, stats.CollectedLucci);
                    Assert.Equal((0, 0, 0, 0), (stats.Kills, stats.Knockdowns, stats.Score, stats.Deaths));
                    Assert.Equal(expected, update.Scoreboard!.Rows.Single(row => row.Slot == guest).Stats.CollectedLucci);
                }
            }
            foreach (var player in new[] { rig.Host, rig.Guest }) Assert.Equal(2, scores.To(player).Length);
            Assert.Equal(150, room.Scoring.Lucci(guest));
            Assert.Null(room.Scoring.Stats(room.FindByGame(rig.Host.Id)!.Slot).CollectedLucci);
            room.ResetToWaiting();
            room.MarkStarting();
            Assert.Null(room.Scoring.Stats(guest).CollectedLucci);
        }
        finally
        {
            await rig.Registry.StopAllAsync();
            await context.UnwindAsync();
        }
    }
}
