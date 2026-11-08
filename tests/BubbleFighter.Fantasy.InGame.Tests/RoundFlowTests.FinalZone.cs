using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Sessions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    /// <summary>A bubble keeps its survivor, a terminal death cannot revive, and the last individual wins once.</summary>
    [Theory]
    [InlineData(GameModeId.Area32)]
    [InlineData(GameModeId.FinalZone)]
    [InlineData(GameModeId.ZombieSurvival)]
    public async Task FinalZoneUsesSharedLifeAndLastIndividualOutcome(GameModeId mode)
    {
        var rig = new Rig();
        var eliminated = new Sink<RoundFinalZonePlayerEliminated>();
        rig.Round.FinalZoneEliminated = eliminated;
        try
        {
            var third = CaptainPlayer(33, "third");
            var room = await StartFinalZoneModeAsync(rig, mode, third);
            var host = room.FindByGame(rig.Host.Id)!;
            var guest = room.FindByGame(rig.Guest.Id)!;
            var thirdSeat = room.FindByGame(third.Id)!;
            var actor = RoomRegistry.SceneEntity(guest.Slot);
            await rig.Round.HandleLifeReportedAsync(rig.Guest, new(RoundLifeEvent.Bubbled, actor), default);
            Assert.Empty(eliminated.To(rig.Host));
            Assert.Equal(SeatLife.Bubble, guest.Life.State);
            await rig.Round.HandleLifeReportedAsync(rig.Guest, new(RoundLifeEvent.BubbleExpired, actor), default);
            Assert.Equal(new(actor, 2), Assert.Single(eliminated.To(rig.Host)));
            await rig.Round.HandleLifeReportedAsync(rig.Guest, new(RoundLifeEvent.ResetConfirmed, actor), default);
            Assert.Equal(SeatLife.Dead, guest.Life.State);
            Assert.Equal(RoomPhase.Playing, room.Phase);
            await rig.Round.HandleEndRequestedAsync(rig.Host, new() { Reason = RoundEndReason.StageReported }, default);
            Assert.Equal(RoomPhase.Playing, room.Phase);
            await rig.Round.HandleLifeReportedAsync(third, new(RoundLifeEvent.Died, RoomRegistry.SceneEntity(thirdSeat.Slot)), default);
            Assert.Equal(host.Slot, Assert.Single(room.DecidedWinnerSlots!));
            Assert.Equal(RoundOutcome.Won, Assert.Single(rig.Ended.To(rig.Host)).Outcome);
            Assert.Equal(RoundOutcome.Lost, Assert.Single(rig.Ended.To(rig.Guest)).Outcome);
            Assert.Equal(2, eliminated.To(rig.Host).Length);
            await rig.Round.HandleLifeReportedAsync(third, new(RoundLifeEvent.Died, RoomRegistry.SceneEntity(thirdSeat.Slot)), default);
            Assert.Equal(2, eliminated.To(rig.Host).Length);
            Assert.Single(rig.Ended.To(rig.Host));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>The shared death envelope also eliminates once, with exact sender ownership and no revival timer.</summary>
    [Fact]
    public async Task FinalZoneSharedDeathEnvelopeRequiresCurrentOwnerAndEndsOnce()
    {
        var rig = new Rig();
        var eliminated = new Sink<RoundFinalZonePlayerEliminated>();
        rig.Round.FinalZoneEliminated = eliminated;
        try
        {
            var room = await StartFinalZoneRoundAsync(rig);
            var guest = room.FindByGame(rig.Guest.Id)!;
            var report = new RoundDeathReported(RoomRegistry.SceneEntity(guest.Slot), 0, ServerRevives: true);
            await rig.Round.HandleDeathReportedAsync(CaptainPlayer(rig.Guest.Id, "replaced"), report, default);
            await rig.Round.HandleDeathReportedAsync(rig.Host, report, default);
            Assert.Equal(SeatLife.Alive, guest.Life.State);
            Assert.Empty(eliminated.To(rig.Host));
            await rig.Round.HandleDeathReportedAsync(rig.Guest, report, default);
            await rig.Round.HandleDeathReportedAsync(rig.Guest, report, default);
            Assert.Equal(SeatLife.Dead, guest.Life.State);
            Assert.False(guest.Life.ServerTimedWait);
            Assert.Equal(new(report.Actor, 1), Assert.Single(eliminated.To(rig.Host)));
            Assert.Equal(RoundOutcome.Won, Assert.Single(rig.Ended.To(rig.Host)).Outcome);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>The script time value stays available without allowing the common time limit to end FinalZone.</summary>
    [Fact]
    public async Task FinalZoneRetainsScriptDurationWithoutCommonTimedCompletion()
    {
        var rig = new Rig(timeLimitMs: 25);
        rig.Round.FinalZoneEliminated = new Sink<RoundFinalZonePlayerEliminated>();
        try
        {
            var room = await StartFinalZoneRoundAsync(rig);
            await Task.Delay(80);
            Assert.Equal(RoomPhase.Playing, room.Phase);
            Assert.Empty(rig.Ended.To(rig.Host));
            Assert.False(Assert.Single(rig.Started.To(rig.Host)).Unlimited);
            Assert.NotNull(room.FinalZone);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Map-script publication is master-only, includes its sender and rejects stale or replaced session objects.</summary>
    [Fact]
    public async Task FinalZoneMapEffectRequiresCurrentMasterAndEchoesOpaqueSequence()
    {
        var rig = new Rig();
        rig.Round.FinalZoneEliminated = new Sink<RoundFinalZonePlayerEliminated>();
        var effects = new Sink<RoundFinalZoneMapEffectChanged>();
        rig.Round.FinalZoneMapEffects = effects;
        try
        {
            var room = await StartFinalZoneRoundAsync(rig);
            var effect = new RoundFinalZoneMapEffectChanged("#dummy", "effect", "", true, 0, 100);
            await rig.Round.HandleFinalZoneMapEffectAsync(rig.Guest, effect, default);
            await rig.Round.HandleFinalZoneMapEffectAsync(CaptainPlayer(rig.Host.Id, "impostor"), effect, default);
            Assert.Empty(effects.To(rig.Host));
            await rig.Round.HandleFinalZoneMapEffectAsync(rig.Host, effect, default);
            Assert.Equal(effect, Assert.Single(effects.To(rig.Host)));
            Assert.Equal(effect, Assert.Single(effects.To(rig.Guest)));
            await rig.Round.HandleFinalZoneMapEffectAsync(rig.Host, effect with { Sequence = 2 }, default);
            Assert.Equal(2, effects.To(rig.Guest)[^1].Sequence);
            lock (room.Gate) room.FinalZone = new(room.StartGeneration - 1, room.Seats);
            await rig.Round.HandleFinalZoneMapEffectAsync(rig.Host, effect, default);
            Assert.Equal(2, effects.To(rig.Host).Length);
            lock (room.Gate) room.RoundCancelled = true;
            await rig.Round.HandleFinalZoneMapEffectAsync(rig.Host, effect, default);
            Assert.Equal(2, effects.To(rig.Host).Length);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A departed participant is eliminated once and cannot keep the final connected player waiting.</summary>
    [Fact]
    public async Task FinalZoneDepartureDecidesTheRemainingIndividual()
    {
        var rig = new Rig();
        var eliminated = new Sink<RoundFinalZonePlayerEliminated>();
        rig.Round.FinalZoneEliminated = eliminated;
        try
        {
            var room = await StartFinalZoneRoundAsync(rig);
            var guestActor = RoomRegistry.SceneEntity(room.FindByGame(rig.Guest.Id)!.Slot);
            await rig.Rooms.HandleLeaveAsync(rig.Guest, new(), default);
            Assert.Equal(new(guestActor, 1), Assert.Single(eliminated.To(rig.Host)));
            Assert.Equal(RoundOutcome.Won, Assert.Single(rig.Ended.To(rig.Host)).Outcome);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Simultaneous terminal lives produce a draw; solo practice continues until death or departure.</summary>
    [Fact]
    public async Task FinalZoneEliminationStateDistinguishesDrawAndSoloPractice()
    {
        var rig = new Rig();
        rig.Round.FinalZoneEliminated = new Sink<RoundFinalZonePlayerEliminated>();
        try
        {
            var room = await StartFinalZoneRoundAsync(rig);
            var state = room.FinalZone!;
            foreach (var seat in room.Seats) seat.Life.Die();
            var notices = state.Synchronize(room.Seats);
            Assert.Equal(new[] { 1, 0 }, notices.Select(notice => notice.SurvivorsRemaining));
            Assert.True(state.Finished);
            Assert.Empty(state.WinnerSlots);
            Assert.Empty(state.Synchronize(room.Seats));
            var solo = new FinalZoneRoundState(room.StartGeneration, [room.Seats[0]]);
            room.Seats[0].Life.Reset();
            Assert.Empty(solo.Synchronize(room.Seats));
            Assert.False(solo.Finished);
            Assert.Single(solo.Synchronize([]));
            Assert.True(solo.Finished);
            Assert.Empty(solo.WinnerSlots);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>The team round continues while an opposing team remains and awards the surviving side.</summary>
    [Fact]
    public async Task TeamSurvivalSettlesWithAllMembersOfTheWinningSide()
    {
        var rig = new Rig();
        rig.Round.FinalZoneEliminated = new Sink<RoundFinalZonePlayerEliminated>();
        var teammate = CaptainPlayer(33, "teammate");
        try
        {
            var room = await StartFinalZoneModeAsync(rig, GameModeId.SuddenSurvival, teammate);
            var host = room.FindByGame(rig.Host.Id)!;
            var guest = room.FindByGame(rig.Guest.Id)!;
            var third = room.FindByGame(teammate.Id)!;
            host.Team = third.Team = 1;
            guest.Team = 2;
            await rig.Round.HandleLifeReportedAsync(teammate, new(RoundLifeEvent.Died, RoomRegistry.SceneEntity(third.Slot)), default);
            Assert.Equal(RoomPhase.Playing, room.Phase);
            await rig.Round.HandleLifeReportedAsync(rig.Guest, new(RoundLifeEvent.Died, RoomRegistry.SceneEntity(guest.Slot)), default);
            Assert.Equal(new[] { host.Slot, third.Slot }.Order(), room.DecidedWinnerSlots!.Order());
            Assert.Equal(RoundOutcome.Won, Assert.Single(rig.Ended.To(rig.Host)).Outcome);
            Assert.Equal(RoundOutcome.Won, Assert.Single(rig.Ended.To(teammate)).Outcome);
            Assert.Equal(RoundOutcome.Lost, Assert.Single(rig.Ended.To(rig.Guest)).Outcome);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    private static Task<PlayRoom> StartFinalZoneRoundAsync(Rig rig, params ISession[] extra) =>
        StartFinalZoneModeAsync(rig, GameModeId.FinalZone, extra);

    private static async Task<PlayRoom> StartFinalZoneModeAsync(Rig rig, GameModeId mode, params ISession[] extra)
    {
        await rig.Rooms.HandleCreateAsync(rig.Host, new RoomCreateRequested("FinalZone room", "", (int)mode, "map"), default);
        var roomId = rig.Entered.To(rig.Host)[0].Room.RoomId;
        foreach (var member in extra.Prepend(rig.Guest))
        {
            await rig.Rooms.HandleJoinAsync(member, new(roomId, ""), default);
            await rig.Rooms.HandleReadyAsync(member, new(true), default);
        }
        await rig.Rooms.HandleStartAsync(rig.Host, new("map"), default);
        foreach (var member in extra.Prepend(rig.Guest).Prepend(rig.Host))
            await rig.Round.HandleLoadReportedAsync(member, new(), default);
        await rig.WaitStartedAsync(rig.Host);
        return rig.Registry.Find(roomId)!;
    }
}
