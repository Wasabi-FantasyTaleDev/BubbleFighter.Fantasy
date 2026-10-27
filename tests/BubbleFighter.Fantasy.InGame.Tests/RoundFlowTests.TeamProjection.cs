using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    /// <summary>Initial and subsequent star updates carry the room's team in both live messages and attached scoreboards.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task StarUpdatesAndScoreboardsPreserveTheSeatsTeam(byte team)
    {
        var services = new ServiceRegistry();
        var context = new PluginContext(new PluginRow { Id = "team-projection", Plugin = "test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        var updates = new Sink<RoundScoreUpdated>();
        context.Effect(services.RegisterMany<IMessageSender<RoundScoreUpdated>>(updates));
        context.Effect(services.RegisterMany<IMessageSender<RoundScoreboard>>(new Sink<RoundScoreboard>()));
        context.Effect(services.RegisterMany<IMessageSender<RoundEnded>>(new Sink<RoundEnded>()));
        context.Effect(services.RegisterMany<IMessageSender<ResultStageAdvanced>>(new Sink<ResultStageAdvanced>()));
        var rig = new Rig(resultContext: context, configure: options => options.StarModeIds = [4]);
        try
        {
            await rig.StartAsync();
            var room = rig.Registry.Find(rig.RoomId)!;
            lock (room.Gate)
                foreach (var seat in room.Seats) seat.Team = team;
            await ReportStarsAsync(rig, rig.Host, RoundStarChange.SmallGained);
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            await room.Executor.ExecuteAsync(_ => ValueTask.CompletedTask, default);
            await ReportStarsAsync(rig, rig.Host, RoundStarChange.SmallGained);

            var sent = updates.To(rig.Host);
            Assert.Equal(3, sent.Length);
            foreach (var update in sent)
            {
                Assert.Equal(team, update.ScoringTeam);
                Assert.NotNull(update.Scoreboard);
                Assert.All(update.Scoreboard.Rows, row => Assert.Equal(team, row.Team));
            }
            Assert.Equal(2, Assert.Single(sent[^1].Seats).Stars);
            Assert.Equal(new[] { 0, 0 }, room.Scoring.TeamTotals);
        }
        finally
        {
            await rig.Registry.StopAllAsync();
            await context.UnwindAsync();
        }
    }
}
