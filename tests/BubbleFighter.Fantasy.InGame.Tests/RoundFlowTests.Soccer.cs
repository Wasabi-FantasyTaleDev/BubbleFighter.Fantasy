using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Sessions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    /// <summary>
    /// Only the host's goal counts, a ball in the first box scores for team 2, and when the clock runs out the team
    /// with more goals wins; equal goals are a draw.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SoccerCountsTheHostsGoalsAndDecidesTheTimedResult(bool draw)
    {
        var (rig, context) = CaptainResultRig(timeLimitMs: 1200);
        var ended = Assert.IsType<Sink<RoundEnded>>(Assert.Single(context.Services.GetAll<IMessageSender<RoundEnded>>()));
        try
        {
            var room = await StartSoccerRoundAsync(rig);
            // The guest is not the host: its report is ignored.
            await rig.Round.HandleSoccerGoalAsync(rig.Guest, new RoundSoccerGoalReported(1, 0), default);
            await rig.Round.HandleSoccerGoalAsync(rig.Host, new RoundSoccerGoalReported(0, 1), default);
            await rig.Round.HandleSoccerGoalAsync(rig.Host, new RoundSoccerGoalReported(2, 1), default);
            if (draw)
                await rig.Round.HandleSoccerGoalAsync(rig.Host, new RoundSoccerGoalReported(1, 0), default);
            lock (room.Gate) Assert.Equal(draw ? [1, 1] : new[] { 0, 1 }, room.SoccerGoals);
            for (var i = 0; i < 500 && ended.To(rig.Host).Length == 0; i++) await Task.Delay(10);
            // The host sits on team 1 and the guest on team 2.
            Assert.Equal(draw ? RoundOutcome.Draw : RoundOutcome.Lost, Assert.Single(ended.To(rig.Host)).Outcome);
            Assert.Equal(draw ? RoundOutcome.Draw : RoundOutcome.Won, Assert.Single(ended.To(rig.Guest)).Outcome);
        }
        finally { await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    /// <summary>The host may add one ball from its own scene range besides its character; nobody else may.</summary>
    [Fact]
    public async Task SoccerHostAddsOneBallFromItsOwnRange()
    {
        var (rig, context) = CaptainResultRig();
        try
        {
            var room = await StartSoccerRoundAsync(rig);
            var host = room.FindByGame(rig.Host.Id)!;
            var guest = room.FindByGame(rig.Guest.Id)!;
            var ball = RoomRegistry.SceneEntity(host.Slot) + 7;
            await rig.Round.HandleObjectAddRequestedAsync(rig.Guest, new(RoomRegistry.SceneEntity(guest.Slot) + 7), default);
            await rig.Round.HandleObjectAddRequestedAsync(rig.Host, new(ball), default);
            await rig.Round.HandleObjectAddRequestedAsync(rig.Host, new(ball + 1), default);
            lock (room.Gate) Assert.Equal(ball, room.SoccerBall);
            Assert.Equal(ball, Assert.Single(rig.ObjectAdded.To(rig.Host), ack => ack.ObjectId != RoomRegistry.SceneEntity(host.Slot)).ObjectId);
            Assert.DoesNotContain(rig.ObjectAdded.To(rig.Guest), ack => ack.ObjectId != RoomRegistry.SceneEntity(guest.Slot));
        }
        finally { await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    private static async Task<PlayRoom> StartSoccerRoundAsync(Rig rig)
    {
        await rig.Rooms.HandleCreateAsync(rig.Host, new RoomCreateRequested("soccer room", "", (int)GameModeId.EventSoccer, "map"), default);
        var roomId = rig.Entered.To(rig.Host)[0].Room.RoomId;
        await rig.Rooms.HandleJoinAsync(rig.Guest, new RoomJoinRequested(roomId, ""), default);
        await rig.Rooms.HandleReadyAsync(rig.Guest, new RoomReadyRequested(true), default);
        await rig.Rooms.HandleStartAsync(rig.Host, new RoomStartRequested("map"), default);
        var room = rig.Registry.Find(roomId)!;
        lock (room.Gate)
        {
            room.FindByGame(rig.Host.Id)!.Team = 1;
            room.FindByGame(rig.Guest.Id)!.Team = 2;
        }
        foreach (var member in new[] { rig.Host, rig.Guest })
            await rig.Round.HandleLoadReportedAsync(member, new(), default);
        foreach (var member in new[] { rig.Host, rig.Guest })
            await rig.WaitStartedAsync(member);
        return room;
    }
}
