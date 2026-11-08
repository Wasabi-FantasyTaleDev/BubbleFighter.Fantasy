using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Handlers;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    /// <summary>The master selects a bounded initial roster; only each target can confirm and receive its conversion echo.</summary>
    [Fact]
    public async Task ZombieEscapeInitialSelectionRequiresCurrentMasterAndOwnedConfirmation()
    {
        var rig = new Rig();
        var (teams, peers) = EnableZombieEscape(rig);
        try
        {
            var room = await StartZombieEscapeAsync(rig);
            Assert.Equal(600_000, Assert.Single(rig.Started.To(rig.Host)).TimeLimitMs);
            Assert.All(room.Seats, seat => Assert.Equal(1, seat.Team));
            var guest = room.FindByGame(rig.Guest.Id)!;
            var conversion = new ScriptZombieConversion(guest.Slot, 204, 110289);
            await rig.Round.HandleZombieEscapeConversionAsync(rig.Guest, conversion, default);
            await rig.Round.HandleZombieEscapeConversionAsync(CaptainPlayer(rig.Host.Id, "replacement"), conversion, default);
            await rig.Round.HandleZombieEscapeConversionAsync(rig.Host, conversion with { Seat = 1000000 }, default);
            await rig.Round.HandleZombieEscapeConversionAsync(rig.Host, conversion with { ZombieKind = 999 }, default);
            await rig.Round.HandleZombieEscapeConversionAsync(rig.Host, conversion with { WeaponItemId = 1 }, default);
            Assert.Empty(teams.To(rig.Host));
            await rig.Rooms.HandleTeamChangeAsync(rig.Host, new() { ZombieConversion = conversion }, default);
            Assert.Equal(conversion, Assert.Single(teams.To(rig.Host)).Conversion);
            Assert.Single(teams.To(rig.Guest));
            Assert.Equal(34, guest.Team);
            var peer = new RoundScriptZombieConversionReported(guest.Slot, 34, 0, 204, 110289);
            await rig.Round.HandleZombieEscapePeerAsync(rig.Host, peer, default);
            await rig.Round.HandleZombieEscapePeerAsync(rig.Guest, peer with { ZombieKind = 207 }, default);
            Assert.Empty(peers.To(rig.Host));
            await rig.Round.HandleZombieEscapePeerAsync(rig.Guest, peer, default);
            await rig.Round.HandleZombieEscapePeerAsync(rig.Guest, peer, default);
            await rig.Round.HandleZombieEscapeConversionAsync(rig.Host, conversion, default);
            await rig.Round.HandleZombieEscapeConversionAsync(rig.Guest, conversion, default);
            Assert.Equal(peer, Assert.Single(peers.To(rig.Host)));
            Assert.Single(peers.To(rig.Guest));
            Assert.Single(teams.To(rig.Host));
            Assert.Equal(RoomPhase.Playing, room.Phase);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Six players receive two distinct initial zombies; later requests cannot select another player's human.</summary>
    [Fact]
    public async Task ZombieEscapeInitialQuotaRoundsUpAndRejectsFurtherRemoteSelection()
    {
        var rig = new Rig();
        var (teams, _) = EnableZombieEscape(rig);
        try
        {
            var extra = Enumerable.Range(3, 4).Select(index => CaptainPlayer(index * 11, "player" + index)).ToArray();
            var room = await StartZombieEscapeAsync(rig, extra);
            Assert.Equal(2, room.ZombieEscape!.InitialZombieLimit);
            var first = room.FindByGame(rig.Guest.Id)!;
            var second = room.FindByGame(extra[0].Id)!;
            await rig.Round.HandleZombieEscapeConversionAsync(extra[0], new(second.Slot, 205, 110289), default);
            await rig.Round.HandleZombieEscapeConversionAsync(rig.Host, new(first.Slot, 202, 110289), default);
            await rig.Round.HandleZombieEscapeConversionAsync(rig.Host, new(first.Slot, 204, 110289), default);
            Assert.Single(teams.To(rig.Host));
            await rig.Round.HandleZombieEscapeConversionAsync(rig.Host, new(second.Slot, 205, 110289), default);
            await rig.Round.HandleZombieEscapeConversionAsync(rig.Host, new(room.FindByGame(extra[1].Id)!.Slot, 207, 110289), default);
            Assert.Equal(2, teams.To(rig.Host).Length);
            Assert.Equal(2, room.ZombieEscape.InitialZombies);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Shared death and reset evidence allows a zombie's next incarnation while duplicate live conversions stay rejected.</summary>
    [Fact]
    public async Task ZombieEscapeRespawnPreservesKindAndUsesSharedLifeGeneration()
    {
        var rig = new Rig();
        var (teams, peers) = EnableZombieEscape(rig);
        try
        {
            var room = await StartZombieEscapeAsync(rig);
            var guest = room.FindByGame(rig.Guest.Id)!;
            var selection = new ScriptZombieConversion(guest.Slot, 202, 110289);
            var peer = new RoundScriptZombieConversionReported(guest.Slot, 34, 0, 202, 110289);
            await rig.Round.HandleZombieEscapeConversionAsync(rig.Host, selection, default);
            await rig.Round.HandleZombieEscapePeerAsync(rig.Guest, peer, default);
            var generation = guest.Life.Generation;
            await rig.Round.HandleLifeReportedAsync(rig.Guest, new(RoundLifeEvent.Died, RoomRegistry.SceneEntity(guest.Slot)), default);
            Assert.Equal(SeatLife.Dead, guest.Life.State);
            await rig.Round.HandleZombieEscapeConversionAsync(rig.Guest, selection with { ZombieKind = 204 }, default);
            Assert.Single(teams.To(rig.Host));
            // The local Lua OnPostRespawn callback can follow the shared reset confirmation.
            var relay = new RecordingSession(92);
            rig.Registry.BindRelay(relay, guest.Key);
            var actor = RoomRegistry.SceneEntity(guest.Slot);
            await rig.Round.HandleLifeReportedAsync(rig.Guest, new(RoundLifeEvent.DeathCleaned, actor), default);
            await rig.Round.HandleSpawnAcceptedAsync(rig.Guest, new(actor, 0)
            {
                Rules = new(RoundRespawnAudience.Requester, RoundRespawnCompletion.MatchConfirmation, RoundRespawnAdmission.CleanedDeath),
            }, default);
            Assert.Equal(SeatLife.ResetPending, guest.Life.State);
            await rig.Round.HandleLifeReportedAsync(relay, new(RoundLifeEvent.ResetConfirmed, actor), default);
            Assert.Equal(SeatLife.Alive, guest.Life.State);
            await rig.Round.HandleZombieEscapeConversionAsync(rig.Guest, selection, default);
            await rig.Round.HandleZombieEscapePeerAsync(rig.Guest, peer, default);
            Assert.Equal(2, teams.To(rig.Host).Length);
            Assert.Equal(2, peers.To(rig.Host).Length);
            Assert.True(guest.Life.Generation > generation);
            Assert.Equal(SeatLife.Alive, guest.Life.State);
            await rig.Round.HandleZombieEscapePeerAsync(rig.Guest, peer with { Operation = 1 }, default);
            Assert.Equal(2, peers.To(rig.Host).Length);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>The native infection gauge may convert its living owner; once every human converts the shared result runs once.</summary>
    [Fact]
    public async Task ZombieEscapeInfectionWithoutDeathEndsOnceAfterOwnedPeerEcho()
    {
        var rig = new Rig(configure: options => options.ResultCloseGraceMs = 60_000);
        var (_, peers) = EnableZombieEscape(rig);
        try
        {
            var room = await StartZombieEscapeAsync(rig);
            var guest = room.FindByGame(rig.Guest.Id)!;
            await rig.Round.HandleZombieEscapeConversionAsync(rig.Host, new(guest.Slot, 204, 110289), default);
            await rig.Round.HandleZombieEscapePeerAsync(rig.Guest, new(guest.Slot, 34, 0, 204, 110289), default);
            var host = room.FindByGame(rig.Host.Id)!;
            Assert.Equal(SeatLife.Alive, host.Life.State);
            await rig.Round.HandleZombieEscapeConversionAsync(rig.Host, new(host.Slot, 205, 110289), default);
            Assert.Empty(rig.Ended.To(rig.Host));
            var peer = new RoundScriptZombieConversionReported(host.Slot, 34, 0, 205, 110289);
            await rig.Round.HandleZombieEscapePeerAsync(rig.Host, peer, default);
            await rig.Round.HandleZombieEscapePeerAsync(rig.Host, peer, default);
            await rig.Round.HandleStageResultAsync(rig.Host, new(1), default);
            Assert.Equal(2, peers.To(rig.Host).Length);
            Assert.Equal(34, room.DecidedWinnerTeam);
            Assert.Equal(RoundOutcome.Won, Assert.Single(rig.Ended.To(rig.Host)).Outcome);
            Assert.Single(rig.Ended.To(rig.Guest));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Only the current master can report escape; a duplicate result settles each participant once.</summary>
    [Fact]
    public async Task ZombieEscapeHumanEscapeSettlesOnceThroughTheSharedResult()
    {
        var progress = new FakeProgress(new Dictionary<long, PlayerProgress> { [11] = new(11, 1, 0), [22] = new(22, 1, 0) });
        var (context, _, _) = ResultContext(progress);
        var boards = (Sink<RoundScoreboard>)context.Services.GetAll<IMessageSender<RoundScoreboard>>().Single();
        var rig = new Rig(resultContext: context, resultRewardDelayMs: 0, configure: options =>
        {
            options.ResultScoreboardDelayMs = 0;
            options.ResultCloseGraceMs = 1;
            options.LevelExpThresholds = [0, 1000, 5000];
            options.MaxLevel = 3;
        });
        EnableZombieEscape(rig);
        try
        {
            var room = await StartZombieEscapeAsync(rig);
            var guest = room.FindByGame(rig.Guest.Id)!;
            await rig.Round.HandleZombieEscapeConversionAsync(rig.Host, new(guest.Slot, 204, 110289), default);
            await rig.Round.HandleZombieEscapePeerAsync(rig.Guest, new(guest.Slot, 34, 0, 204, 110289), default);
            await rig.Round.HandleEndRequestedAsync(rig.Host, new(), default);
            await rig.Round.HandleStageResultAsync(rig.Guest, new(0), default);
            await rig.Round.HandleStageResultAsync(CaptainPlayer(rig.Host.Id, "replacement"), new(0), default);
            await rig.Round.HandleStageResultAsync(rig.Host, new(2), default);
            Assert.Equal(RoomPhase.Playing, room.Phase);
            await rig.Round.HandleStageResultAsync(rig.Host, new(0), default);
            await rig.Round.HandleStageResultAsync(rig.Host, new(0), default);
            await room.ResultTask.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(2, progress.Commands.Count);
            Assert.Equal(2, progress.Commands.Select(command => command.UserNum).Distinct().Count());
            Assert.Equal(1, boards.To(rig.Host)[^1].WinnerTeam);
            Assert.Equal(RoundOutcome.Won, boards.To(rig.Host)[^1].Rows.Single(row => row.Slot == 0).Outcome);
            Assert.Equal(RoomPhase.Waiting, room.Phase);
            Assert.Null(room.ZombieEscape);
        }
        finally { await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    /// <summary>Generation, phase and cancellation fences reject stale work, and the disabled mode has no conversion authority.</summary>
    [Fact]
    public async Task ZombieEscapeRejectsOldGenerationWrongModeCancelledAndDisabledReports()
    {
        var rig = new Rig();
        var (teams, _) = EnableZombieEscape(rig);
        try
        {
            var room = await StartZombieEscapeAsync(rig);
            var conversion = new ScriptZombieConversion(room.FindByGame(rig.Guest.Id)!.Slot, 204, 110289);
            var state = room.ZombieEscape;
            room.ZombieEscape = new(room.StartGeneration - 1, rig.Host, room.Seats);
            await rig.Round.HandleZombieEscapeConversionAsync(rig.Host, conversion, default);
            room.ZombieEscape = state;
            room.ModeId = 1;
            await rig.Round.HandleZombieEscapeConversionAsync(rig.Host, conversion, default);
            room.ModeId = 32;
            room.RoundCancelled = true;
            await rig.Round.HandleZombieEscapeConversionAsync(rig.Host, conversion, default);
            room.RoundCancelled = false;
            rig.Round.ScriptZombieConversions = null;
            await rig.Round.HandleZombieEscapeConversionAsync(rig.Host, conversion, default);
            Assert.Empty(teams.To(rig.Host));
            Assert.Empty(rig.Ended.To(rig.Host));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A queued conversion from an earlier start cannot mutate the next round, whose conversion ledger is fresh.</summary>
    [Fact]
    public async Task ZombieEscapeQueuedConversionCannotCrossRoundRestart()
    {
        var rig = new Rig();
        var (teams, _) = EnableZombieEscape(rig);
        try
        {
            var room = await StartZombieEscapeAsync(rig);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var held = room.Executor.ExecuteAsync(async _ =>
            {
                entered.SetResult();
                await release.Task;
                lock (room.Gate)
                {
                    room.ResetToWaiting();
                    room.MarkStarting();
                    rig.Round.InitializeZombieEscape(room);
                    room.MarkPlaying();
                }
            }, default);
            await entered.Task;
            var conversion = new ScriptZombieConversion(room.FindByGame(rig.Guest.Id)!.Slot, 204, 110289);
            var queued = rig.Round.HandleZombieEscapeConversionAsync(rig.Host, conversion, default).AsTask();
            release.SetResult();
            await held;
            await queued;
            Assert.Empty(teams.To(rig.Host));
            Assert.Equal(0, room.ZombieEscape!.InitialZombies);
            await rig.Round.HandleZombieEscapeConversionAsync(rig.Host, conversion, default);
            Assert.Single(teams.To(rig.Host));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A required Lua master's departure cancels without an award; an unaudited late join stays refused.</summary>
    [Fact]
    public async Task ZombieEscapeMasterDepartureCancelsAndLateJoinIsRefused()
    {
        var rig = new Rig();
        EnableZombieEscape(rig);
        try
        {
            var room = await StartZombieEscapeAsync(rig);
            var refused = new Sink<RoomJoinRefused>();
            var admissions = new RoomFlow(rig.Registry, new RoomSenders(rig.Entered, rig.SeatUpdated,
                rig.SeatVacated, rig.MasterChanged, JoinRefused: refused), NullLogger.Instance);
            var late = CaptainPlayer(33, "late");
            await admissions.HandleJoinAsync(late, new(room.Id, ""), default);
            Assert.Equal(RoomJoinRefusal.Started, Assert.Single(refused.To(late)).Reason);
            await rig.Rooms.HandleLeaveAsync(rig.Host, new(), default);
            Assert.True(room.RoundCancelled);
            await rig.Round.HandleStageResultAsync(rig.Guest, new(0), default);
            Assert.Empty(rig.Ended.To(rig.Guest));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>The server deadline awards the zombie allegiance and never treats timeout as human survival.</summary>
    [Fact]
    public async Task ZombieEscapeTimeoutAwardsZombies()
    {
        var rig = new Rig(configure: options => { options.ZombieEscapeRoundTimeLimitMs = 150; options.ResultCloseGraceMs = 60_000; });
        EnableZombieEscape(rig);
        try
        {
            var room = await StartZombieEscapeAsync(rig);
            await WaitForAsync(() => rig.Ended.To(rig.Host).Length == 1);
            Assert.Equal(34, room.DecidedWinnerTeam);
            Assert.Equal(RoundOutcome.Lost, Assert.Single(rig.Ended.To(rig.Host)).Outcome);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    private static (Sink<RoundScriptZombieTeamChanged> Teams, Sink<RoundScriptZombieConversionReported> Peers) EnableZombieEscape(Rig rig)
    {
        var teams = new Sink<RoundScriptZombieTeamChanged>();
        var peers = new Sink<RoundScriptZombieConversionReported>();
        rig.Round.ZombieEscapeEnabled = true;
        rig.Round.ScriptZombieTeams = teams;
        rig.Round.ScriptZombieConversions = peers;
        rig.Rooms.ZombieEscapeStarting = rig.Round.InitializeZombieEscape;
        rig.Rooms.ScriptZombieConversionRequested = rig.Round.HandleScriptZombieConversionAsync;
        return (teams, peers);
    }

    private static async Task<PlayRoom> StartZombieEscapeAsync(Rig rig, params ISession[] extra)
    {
        await rig.Rooms.HandleCreateAsync(rig.Host, new("Escape", "", 32, "rel_zombieEscape01_pyramid"), default);
        var room = rig.Registry.TryGet(rig.Host.Id)!;
        foreach (var member in extra.Prepend(rig.Guest))
        {
            await rig.Rooms.HandleJoinAsync(member, new(room.Id, ""), default);
            await rig.Rooms.HandleReadyAsync(member, new(true), default);
        }
        await rig.Rooms.HandleStartAsync(rig.Host, new(room.MapKey), default);
        foreach (var member in extra.Prepend(rig.Guest).Prepend(rig.Host))
            await rig.Round.HandleLoadReportedAsync(member, new(), default);
        await rig.WaitStartedAsync(rig.Host);
        return room;
    }

    /// <summary>The shared conversion routes preserve the waiting allegiance after a script-owned round is returned.</summary>
    [Fact]
    public async Task ZombieEscapeSharedRouteRestoresWaitingTeams()
    {
        var rig = NewScriptZombieRig();
        var (teams, peers) = EnableZombieEscape(rig);
        try
        {
            await rig.Rooms.HandleCreateAsync(rig.Host, new("Escape", "", 32, "rel_zombieEscape01_pyramid"), default);
            var room = rig.Registry.TryGet(rig.Host.Id)!;
            await rig.Rooms.HandleJoinAsync(rig.Guest, new(room.Id, ""), default);
            var waiting = room.Seats.ToDictionary(seat => seat.Slot, seat => seat.Team);
            await rig.Rooms.HandleReadyAsync(rig.Guest, new(true), default);
            await rig.Rooms.HandleStartAsync(rig.Host, new(room.MapKey), default);
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            var guest = room.FindByGame(rig.Guest.Id)!;
            await rig.Round.HandleScriptZombieConversionAsync(rig.Host, new(guest.Slot, 204, 110289), default);
            await rig.Round.HandleScriptZombiePeerAsync(rig.Guest, new(guest.Slot, 34, 0, 204, 110289), default);
            Assert.Single(teams.To(rig.Host));
            Assert.Single(peers.To(rig.Host));
            room.ResetToWaiting();
            Assert.Null(room.ZombieEscape);
            Assert.All(room.Seats, seat => Assert.Equal(waiting[seat.Slot], seat.Team));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }
}
