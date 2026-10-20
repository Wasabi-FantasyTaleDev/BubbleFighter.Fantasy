using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    /// <summary>A compact finishing report consumes a knockdown victim only from its own round.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompactFinishingBlowDoesNotBorrowAVictimFromAnEarlierRound(bool restartRound)
    {
        var services = new ServiceRegistry();
        var context = new PluginContext(new PluginRow { Id = "victim-lifetime", Plugin = "test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        var score = new Sink<RoundScoreUpdated>();
        context.Effect(services.RegisterMany<IMessageSender<RoundScoreUpdated>>(score));
        context.Effect(services.RegisterMany<IMessageSender<RoundScoreboard>>(new Sink<RoundScoreboard>()));
        context.Effect(services.RegisterMany<IMessageSender<RoundEnded>>(new Sink<RoundEnded>()));
        context.Effect(services.RegisterMany<IMessageSender<ResultStageAdvanced>>(new Sink<ResultStageAdvanced>()));
        var rig = new Rig(resultContext: context);
        try
        {
            await rig.StartAsync();
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            var room = rig.Registry.Find(rig.RoomId)!;
            var reporterSlot = room.FindByGame(rig.Host.Id)!.Slot;
            var victimSlot = room.FindByGame(rig.Guest.Id)!.Slot;
            await rig.Round.HandleKillCreditReportedAsync(rig.Host, new(reporterSlot, victimSlot, 1), default);

            if (restartRound)
            {
                room.ResetToWaiting();
                await rig.Rooms.ReturnToRoomAsync(room, default);
                await rig.Rooms.HandleReadyAsync(rig.Guest, new(true), default);
                await rig.Rooms.HandleStartAsync(rig.Host, new("map"), default);
                await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
                await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
                for (var i = 0; i < 200 && rig.Started.To(rig.Host).Length != 2; i++)
                    await Task.Delay(10);
                Assert.Equal(2, rig.Started.To(rig.Host).Length);
                Assert.Equal(2, room.StartGeneration);
            }

            await rig.Round.HandleFinishingBlowReportedAsync(rig.Host, new(reporterSlot), default);
            Assert.Equal(1, room.Scoring.Stats(reporterSlot).Kills);
            Assert.Equal(restartRound ? 0 : 1, room.Scoring.Stats(victimSlot).Deaths);
            Assert.Equal(restartRound ? 1 : 2, score.To(rig.Guest)[^1].Seats.Count);
        }
        finally
        {
            await rig.Registry.StopAllAsync();
            await context.UnwindAsync();
        }
    }
}
