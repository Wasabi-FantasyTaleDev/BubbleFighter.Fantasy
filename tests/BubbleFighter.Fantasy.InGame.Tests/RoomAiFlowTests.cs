using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Movement;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Handlers;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Computer players reserve real seats while only authenticated humans own connections and loading barriers.</summary>
public sealed class RoomAiFlowTests
{
    /// <summary>Team-room menus balance an empty seat; free-for-all retains an explicit no-team selection.</summary>
    [Theory]
    [InlineData(38, 0, 2)]
    [InlineData(39, 0, 2)]
    [InlineData(38, null, 2)]
    [InlineData(38, 1, 1)]
    [InlineData(38, 2, 2)]
    [InlineData(13, 0, 0)]
    public async Task AiTeamSelectionFollowsTheRoomsSpawnRules(ushort mode, int? requested, int expected)
    {
        var options = Options();
        options.AiModeIds = [13, 38, 39];
        var rig = new RoomFlowTests.Rig(options: options);
        var host = RoomFlowTests.Rig.Player(11, "host");
        try
        {
            await rig.Flow.HandleCreateAsync(host, new("AI", "", mode, "map"), default);
            await rig.Flow.HandleAiAddAsync(host, new(requested, 1, 1, 2), default);
            Assert.Equal(expected, Assert.Single(rig.Registry.TryGet(host.Id)!.Bots).Appearance.Team);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Menu changes retain an existing bot's team and balance new seats against both humans and bots.</summary>
    [Fact]
    public async Task AiMenuPreservesExistingTeamsAndBalancesNewSeats()
    {
        var rig = new RoomFlowTests.Rig(options: Options());
        var host = RoomFlowTests.Rig.Player(11, "host");
        var guest = RoomFlowTests.Rig.Player(22, "guest");
        try
        {
            await rig.Flow.HandleCreateAsync(host, new("AI", "", 38, "map"), default);
            var room = rig.Registry.TryGet(host.Id)!;
            await rig.Flow.HandleAiAddAsync(host, new(0, 1, 1, 2), default);
            Assert.Equal(2, room.BotAt(1)!.Appearance.Team);
            await rig.Flow.HandleJoinAsync(guest, new(room.Id, ""), default);
            Assert.Equal(1, room.FindByGame(guest.Id)!.Team);
            await rig.Flow.HandleAiAddAsync(host, new(0, 3, 1, 2), default);
            Assert.Equal(2, room.BotAt(3)!.Appearance.Team);
            room.FindByGame(guest.Id)!.Team = 2;
            await rig.Flow.HandleAiAddAsync(host, new(0, 1, 1, 2), default);
            Assert.Equal(2, room.BotAt(1)!.Appearance.Team);
            Assert.Equal(2, rig.SeatUpdated.To(guest)[^1].Seat.Team);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>An equal roster permits either team, and the following addition restores equal team sizes.</summary>
    [Fact]
    public async Task AiMenuTieSelectsAValidTeamThenFillsTheSmallerTeam()
    {
        var rig = new RoomFlowTests.Rig(options: Options());
        var host = RoomFlowTests.Rig.Player(11, "host");
        var guest = RoomFlowTests.Rig.Player(22, "guest");
        try
        {
            await rig.Flow.HandleCreateAsync(host, new("AI", "", 38, "map"), default);
            var room = rig.Registry.TryGet(host.Id)!;
            await rig.Flow.HandleJoinAsync(guest, new(room.Id, ""), default);
            Assert.Equal(1, room.FindByGame(host.Id)!.Team);
            Assert.Equal(2, room.FindByGame(guest.Id)!.Team);
            await rig.Flow.HandleAiAddAsync(host, new(0, 2, 1, 2), default);
            Assert.Contains(room.BotAt(2)!.Appearance.Team, new[] { 1, 2 });
            await rig.Flow.HandleAiAddAsync(host, new(0, 3, 1, 2), default);
            Assert.NotEqual(room.BotAt(2)!.Appearance.Team, room.BotAt(3)!.Appearance.Team);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A bot's death and reset affect its own life, and only its current controller can request that reset.</summary>
    [Fact]
    public async Task AiDeathAndResetNeverBorrowTheControllersLife()
    {
        var options = Options();
        var rig = new RoomFlowTests.Rig(options: options);
        var host = RoomFlowTests.Rig.Player(11, "host");
        var guest = RoomFlowTests.Rig.Player(22, "guest");
        var grants = new RoomFlowTests.Sink<RoundResetGranted>();
        var round = new RoundFlow(rig.Registry, options, new(null, new RoomFlowTests.Sink<RoundCountdownStarted>(),
            new RoomFlowTests.Sink<RoundStarted>()), NullLogger.Instance, default, rig.Flow.ReturnToRoomAsync);
        try
        {
            await rig.Flow.HandleCreateAsync(host, new("AI", "", 38, "map"), default);
            var room = rig.Registry.TryGet(host.Id)!;
            await rig.Flow.HandleAiAddAsync(host, new(2, 1, 1, 2), default);
            await rig.Flow.HandleJoinAsync(guest, new(room.Id, ""), default);
            room.MarkStarting();
            room.MarkPlaying();
            var controller = room.FindByGame(host.Id)!;
            controller.GameLinkGeneration = room.StartGeneration;
            var bot = Assert.Single(room.Bots);
            var actor = RoomRegistry.SceneEntity(1);
            await round.HandleLifeReportedAsync(guest, new(RoundLifeEvent.Died, actor), default);
            Assert.Equal(SeatLife.Alive, bot.Life);
            await round.HandleLifeReportedAsync(host, new(RoundLifeEvent.Died, actor), default);
            await round.HandleLifeReportedAsync(host, new(RoundLifeEvent.DeathCleaned, actor), default);
            Assert.Equal(SeatLife.Dead, bot.Life);
            Assert.True(bot.DeathCleaned);
            Assert.Equal(SeatLife.Alive, controller.Life);
            var reset = new RoundResetRequested(actor, new byte[10]);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                round.HandleResetRequestedAsync(guest, reset, grants, default).AsTask());
            await round.HandleResetRequestedAsync(host, reset, grants, default);
            Assert.Single(grants.To(host));
            Assert.Equal(SeatLife.ResetPending, bot.Life);
            await round.HandleLifeReportedAsync(host, new(RoundLifeEvent.ResetConfirmed, actor), default);
            Assert.Equal(SeatLife.Alive, bot.Life);
            Assert.False(bot.DeathCleaned);
            Assert.Null(bot.ResetRelay);
            Assert.Equal(SeatLife.Alive, controller.Life);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    private static InGameOptions Options() => new()
    {
        MaxPlayersPerMatch = 4, StartTimerMs = 1, AiModeIds = [38, 39],
        AiPresets = [new() { Difficulty = 1, Preset = 2, Nickname = "test bot", CharacterItemId = 300025, EquippedItemIds = [203108] }],
    };

    /// <summary>Mode changes remove AI reservations and notify all humans before publishing the new settings.</summary>
    [Fact]
    public async Task LeavingAiModeReleasesSeatsAndAnnouncesRemoval()
    {
        var rig = new RoomFlowTests.Rig(options: Options());
        var host = RoomFlowTests.Rig.Player(11, "host");
        var guest = RoomFlowTests.Rig.Player(22, "guest");
        try
        {
            await rig.Flow.HandleCreateAsync(host, new("AI", "", 38, "map"), default);
            var room = rig.Registry.TryGet(host.Id)!;
            await rig.Flow.HandleAiAddAsync(host, new(2, 1, 1, 2), default);
            await rig.Flow.HandleJoinAsync(guest, new(room.Id, ""), default);
            await rig.Flow.HandleSettingsChangeAsync(host, new(39, "map"), default);
            Assert.Single(room.Bots);
            await rig.Flow.HandleSettingsChangeAsync(host, new(1, "map"), default);
            Assert.Empty(room.Bots);
            Assert.Equal(1, Assert.Single(rig.SeatVacated.To(guest)).Seat.Slot);
            Assert.True(room.SetSlotClosed(1, true));
            Assert.True(room.SetSlotClosed(1, false));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A single real client's load completes a room with bots, and the start roster still includes every AI.</summary>
    [Fact]
    public async Task AiSeatsDoNotNeedIndependentLoadingReports()
    {
        var options = Options();
        var rig = new RoomFlowTests.Rig(options: options);
        var host = RoomFlowTests.Rig.Player(11, "host");
        var loaded = new RoomFlowTests.Sink<RoundPlayerLoaded>();
        var countdown = new RoomFlowTests.Sink<RoundCountdownStarted>();
        var started = new RoomFlowTests.Sink<RoundStarted>();
        var round = new RoundFlow(rig.Registry, options, new(loaded, countdown, started), NullLogger.Instance,
            default, rig.Flow.ReturnToRoomAsync);
        try
        {
            await rig.Flow.HandleCreateAsync(host, new("AI", "", 38, "map"), default);
            await rig.Flow.HandleAiAddAsync(host, new(1, 1, 1, 2), default);
            await rig.Flow.HandleAiAddAsync(host, new(2, 2, 1, 2), default);
            await rig.Flow.HandleAiAddAsync(host, new(2, 3, 1, 2), default);
            await rig.Flow.HandleStartAsync(host, new("map"), default);
            Assert.Equal(4, Assert.Single(rig.Starting.To(host)).Room.Seats.Count);
            var room = rig.Registry.TryGet(host.Id)!;
            await round.HandleLoadReportedAsync(host, new(), default);
            await room.CountdownTask;
            Assert.Equal(RoomPhase.Playing, room.Phase);
            Assert.Single(started.To(host));
            Assert.Single(room.Seats);
            await rig.Flow.HandleAiRemoveAsync(host, new(1), default);
            Assert.Equal(3, room.Bots.Count);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A controller's AI movement has its own delta history and cannot be impersonated by another human.</summary>
    [Fact]
    public async Task BotMovementAndActionsRequireTheRealController()
    {
        var options = Options();
        var rig = new RoomFlowTests.Rig(options: options);
        var host = RoomFlowTests.Rig.Player(11, "host");
        var guest = RoomFlowTests.Rig.Player(22, "guest");
        var movement = new RoomFlowTests.Sink<RoundMovementRelayed>();
        var feed = new RoomFlowTests.Sink<RoundFeedReceived>();
        var round = new RoundFlow(rig.Registry, options, new(null, new RoomFlowTests.Sink<RoundCountdownStarted>(),
            new RoomFlowTests.Sink<RoundStarted>(), Feed: feed), NullLogger.Instance, default, rig.Flow.ReturnToRoomAsync);
        try
        {
            await rig.Flow.HandleCreateAsync(host, new("AI", "", 38, "map"), default);
            var room = rig.Registry.TryGet(host.Id)!;
            await rig.Flow.HandleAiAddAsync(host, new(2, 1, 1, 2), default);
            await rig.Flow.HandleJoinAsync(guest, new(room.Id, ""), default);
            room.MarkStarting();
            room.MarkPlaying();
            room.FindByGame(guest.Id)!.GameLinkGeneration = room.StartGeneration;
            var actor = RoomRegistry.SceneEntity(1);
            var report = new RoundMovementReported(actor, PlayerMovementState.FullMask, 1, 2, 3, 4, 5, 6, 7);
            await round.HandleMovementReportedAsync(guest, report, movement, default);
            Assert.Empty(movement.Sent);
            await round.HandleMovementReportedAsync(host, report, movement, default);
            Assert.Equal(1, Assert.Single(movement.To(guest)).Movement.X);
            Assert.Null(room.FindByGame(host.Id)!.Movement);
            Assert.NotNull(Assert.Single(room.Bots).Movement);
            await round.HandleFeedAsync(host, new(1, new byte[] { 7 })
            {
                ReportedActors = [actor, RoomRegistry.SceneEntity(2)],
            }, default);
            Assert.Single(feed.To(guest));
            await round.HandleFeedAsync(guest, new(1, new byte[] { 7 }) { ReportedActors = [actor] }, default);
            Assert.Empty(feed.To(host));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }
}
