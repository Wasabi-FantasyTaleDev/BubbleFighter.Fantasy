using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
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
    public async Task ScriptZombieInitialSelectionRequiresCurrentMasterAndOwnedConfirmation()
    {
        var rig = NewScriptZombieRig();
        var (teams, peers) = EnableScriptZombie(rig);
        try
        {
            var room = await StartScriptZombieAsync(rig);
            Assert.Equal(180_000, Assert.Single(rig.Started.To(rig.Host)).TimeLimitMs);
            Assert.All(room.Seats, seat => Assert.Equal(1, seat.Team));
            var guest = room.FindByGame(rig.Guest.Id)!;
            var conversion = new ScriptZombieConversion(guest.Slot, 204, 110288);
            await rig.Round.HandleScriptZombieConversionAsync(rig.Guest, conversion, default);
            await rig.Round.HandleScriptZombieConversionAsync(CaptainPlayer(rig.Host.Id, "replacement"), conversion, default);
            await rig.Round.HandleScriptZombieConversionAsync(rig.Host, conversion with { Seat = 1000000 }, default);
            await rig.Round.HandleScriptZombieConversionAsync(rig.Host, conversion with { ZombieKind = 999 }, default);
            await rig.Round.HandleScriptZombieConversionAsync(rig.Host, conversion with { WeaponItemId = 1 }, default);
            Assert.Empty(teams.To(rig.Host));
            await rig.Rooms.HandleTeamChangeAsync(rig.Host, new() { ZombieConversion = conversion }, default);
            Assert.Equal(conversion, Assert.Single(teams.To(rig.Host)).Conversion);
            Assert.Single(teams.To(rig.Guest));
            Assert.Equal(34, guest.Team);
            var peer = new RoundScriptZombieConversionReported(guest.Slot, 34, 0, 204, 110288);
            await rig.Round.HandleScriptZombiePeerAsync(rig.Host, peer, default);
            await rig.Round.HandleScriptZombiePeerAsync(rig.Guest, peer with { ZombieKind = 207 }, default);
            Assert.Empty(peers.To(rig.Host));
            await rig.Round.HandleScriptZombiePeerAsync(rig.Guest, peer, default);
            await rig.Round.HandleScriptZombiePeerAsync(rig.Guest, peer, default);
            await rig.Round.HandleScriptZombieConversionAsync(rig.Host, conversion, default);
            await rig.Round.HandleScriptZombieConversionAsync(rig.Guest, conversion, default);
            Assert.Equal(peer, Assert.Single(peers.To(rig.Host)));
            Assert.Single(peers.To(rig.Guest));
            Assert.Single(teams.To(rig.Host));
            Assert.Equal(RoomPhase.Playing, room.Phase);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Five players receive two distinct initial zombies; later requests cannot select another player's human.</summary>
    [Fact]
    public async Task ScriptZombieInitialQuotaRoundsUpAndRejectsFurtherRemoteSelection()
    {
        var rig = NewScriptZombieRig();
        var (teams, _) = EnableScriptZombie(rig);
        try
        {
            var extra = Enumerable.Range(3, 3).Select(index => CaptainPlayer(index * 11, "player" + index)).ToArray();
            var room = await StartScriptZombieAsync(rig, extra);
            Assert.Equal(2, room.ScriptZombies!.InitialZombieLimit);
            var first = room.FindByGame(rig.Guest.Id)!;
            var second = room.FindByGame(extra[0].Id)!;
            await rig.Round.HandleScriptZombieConversionAsync(extra[0], new(second.Slot, 205, 110288), default);
            await rig.Round.HandleScriptZombieConversionAsync(rig.Host, new(first.Slot, 202, 110288), default);
            await rig.Round.HandleScriptZombieConversionAsync(rig.Host, new(first.Slot, 204, 110288), default);
            Assert.Single(teams.To(rig.Host));
            await rig.Round.HandleScriptZombieConversionAsync(rig.Host, new(second.Slot, 205, 110288), default);
            await rig.Round.HandleScriptZombieConversionAsync(rig.Host, new(room.FindByGame(extra[1].Id)!.Slot, 207, 110288), default);
            Assert.Equal(2, teams.To(rig.Host).Length);
            Assert.Equal(2, room.ScriptZombies.InitialZombies);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Shared death and reset evidence allows a zombie's next incarnation while duplicate live conversions stay rejected.</summary>
    [Fact]
    public async Task ScriptZombieRespawnPreservesKindAndUsesSharedLifeGeneration()
    {
        var rig = NewScriptZombieRig();
        var (teams, peers) = EnableScriptZombie(rig);
        try
        {
            var room = await StartScriptZombieAsync(rig);
            var guest = room.FindByGame(rig.Guest.Id)!;
            var selection = new ScriptZombieConversion(guest.Slot, 202, 110288);
            var peer = new RoundScriptZombieConversionReported(guest.Slot, 34, 0, 202, 110288);
            await rig.Round.HandleScriptZombieConversionAsync(rig.Host, selection, default);
            await rig.Round.HandleScriptZombiePeerAsync(rig.Guest, peer, default);
            var generation = guest.Life.Generation;
            await rig.Round.HandleLifeReportedAsync(rig.Guest, new(RoundLifeEvent.Died, RoomRegistry.SceneEntity(guest.Slot)), default);
            Assert.Equal(SeatLife.Dead, guest.Life.State);
            await rig.Round.HandleScriptZombieConversionAsync(rig.Guest, selection with { ZombieKind = 204 }, default);
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
            await rig.Round.HandleScriptZombieConversionAsync(rig.Guest, selection, default);
            await rig.Round.HandleScriptZombiePeerAsync(rig.Guest, peer, default);
            Assert.Equal(2, teams.To(rig.Host).Length);
            Assert.Equal(2, peers.To(rig.Host).Length);
            Assert.True(guest.Life.Generation > generation);
            Assert.Equal(SeatLife.Alive, guest.Life.State);
            await rig.Round.HandleScriptZombiePeerAsync(rig.Guest, peer with { Operation = 1 }, default);
            Assert.Equal(2, peers.To(rig.Host).Length);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Generation, phase and cancellation fences reject stale work, and the disabled mode has no conversion authority.</summary>
    [Fact]
    public async Task ScriptZombieRejectsOldGenerationWrongModeCancelledAndDisabledReports()
    {
        var rig = NewScriptZombieRig();
        var (teams, _) = EnableScriptZombie(rig);
        try
        {
            var room = await StartScriptZombieAsync(rig);
            var conversion = new ScriptZombieConversion(room.FindByGame(rig.Guest.Id)!.Slot, 204, 110288);
            var state = room.ScriptZombies;
            room.ScriptZombies = new(room.StartGeneration - 1, rig.Host, room.Seats);
            await rig.Round.HandleScriptZombieConversionAsync(rig.Host, conversion, default);
            room.ScriptZombies = state;
            room.ModeId = 1;
            await rig.Round.HandleScriptZombieConversionAsync(rig.Host, conversion, default);
            room.ModeId = 8;
            room.RoundCancelled = true;
            await rig.Round.HandleScriptZombieConversionAsync(rig.Host, conversion, default);
            room.RoundCancelled = false;
            rig.Round.ScriptZombieConversions = null;
            await rig.Round.HandleScriptZombieConversionAsync(rig.Host, conversion, default);
            Assert.Empty(teams.To(rig.Host));
            Assert.Empty(rig.Ended.To(rig.Host));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A queued conversion from an earlier start cannot mutate the next round, whose conversion ledger is fresh.</summary>
    [Fact]
    public async Task ScriptZombieQueuedConversionCannotCrossRoundRestart()
    {
        var rig = NewScriptZombieRig();
        var (teams, _) = EnableScriptZombie(rig);
        try
        {
            var room = await StartScriptZombieAsync(rig);
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
                    rig.Round.InitializeScriptZombies(room);
                    room.MarkPlaying();
                }
            }, default);
            await entered.Task;
            var conversion = new ScriptZombieConversion(room.FindByGame(rig.Guest.Id)!.Slot, 204, 110288);
            var queued = rig.Round.HandleScriptZombieConversionAsync(rig.Host, conversion, default).AsTask();
            release.SetResult();
            await held;
            await queued;
            Assert.Empty(teams.To(rig.Host));
            Assert.Equal(0, room.ScriptZombies!.InitialZombies);
            await rig.Round.HandleScriptZombieConversionAsync(rig.Host, conversion, default);
            Assert.Single(teams.To(rig.Host));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A required Lua master's departure cancels without an award; an unaudited late join stays refused.</summary>
    [Fact]
    public async Task ScriptZombieMasterDepartureCancelsAndLateJoinIsRefused()
    {
        var rig = NewScriptZombieRig();
        EnableScriptZombie(rig);
        try
        {
            var room = await StartScriptZombieAsync(rig);
            var refused = new Sink<RoomJoinRefused>();
            var admissions = new RoomFlow(rig.Registry, new RoomSenders(rig.Entered, rig.SeatUpdated,
                rig.SeatVacated, rig.MasterChanged, JoinRefused: refused), NullLogger.Instance);
            var late = CaptainPlayer(33, "late");
            await admissions.HandleJoinAsync(late, new(room.Id, ""), default);
            Assert.Equal(RoomJoinRefusal.Started, Assert.Single(refused.To(late)).Reason);
            await rig.Rooms.HandleLeaveAsync(rig.Host, new(), default);
            Assert.True(room.RoundCancelled);
            await rig.Round.HandleEndRequestedAsync(rig.Guest, new(), default);
            Assert.Empty(rig.Ended.To(rig.Guest));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>The server deadline awards surviving humans without requiring a king death.</summary>
    [Theory]
    [InlineData(8)]
    [InlineData(28)]
    public async Task ScriptZombieTimeoutAwardsHumans(int mode)
    {
        var rig = NewScriptZombieRig(configure: options => { options.ScriptZombieRoundTimeLimitMs = 150; options.ResultCloseGraceMs = 60_000; }, mode: mode);
        EnableScriptZombie(rig);
        try
        {
            var room = await StartScriptZombieModeAsync(rig, mode);
            await WaitForAsync(() => rig.Ended.To(rig.Host).Length == 1);
            Assert.Equal(1, room.DecidedWinnerTeam);
            Assert.Equal(RoundOutcome.Won, Assert.Single(rig.Ended.To(rig.Host)).Outcome);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Both scripts require owned life evidence for later infection and end once all confirmations arrived.</summary>
    [Theory]
    [InlineData(8, 204, 110288)]
    [InlineData(28, 201, 110290)]
    public async Task ScriptZombieInfectionRequiresDeathAndEndsAfterOwnedConfirmation(int mode, int firstKind, int weapon)
    {
        var rig = NewScriptZombieRig(options => options.ResultCloseGraceMs = 60_000, mode);
        var (teams, peers) = EnableScriptZombie(rig);
        try
        {
            var room = await StartScriptZombieModeAsync(rig, mode);
            var guest = room.FindByGame(rig.Guest.Id)!;
            await rig.Round.HandleScriptZombieConversionAsync(rig.Host, new(guest.Slot, firstKind, weapon), default);
            await rig.Round.HandleScriptZombiePeerAsync(rig.Guest, new(guest.Slot, 34, 0, firstKind, weapon), default);
            var host = room.FindByGame(rig.Host.Id)!;
            var infection = new ScriptZombieConversion(host.Slot, 205, weapon);
            await rig.Round.HandleScriptZombieConversionAsync(rig.Host, infection, default);
            Assert.Single(teams.To(rig.Host));
            Assert.Equal(1, host.Team);
            await rig.Round.HandleEndRequestedAsync(rig.Host, new(), default);
            Assert.Equal(RoomPhase.Playing, room.Phase);
            await rig.Round.HandleLifeReportedAsync(rig.Host, new(RoundLifeEvent.Died, RoomRegistry.SceneEntity(host.Slot)), default);
            await rig.Rooms.HandleTeamChangeAsync(rig.Host, new() { ZombieConversion = infection }, default);
            Assert.Equal(2, teams.To(rig.Host).Length);
            Assert.Empty(rig.Ended.To(rig.Host));
            var report = new RoundScriptZombieConversionReported(host.Slot, 34, 0, 205, weapon);
            await rig.Round.HandleScriptZombiePeerAsync(rig.Host, report, default);
            await rig.Round.HandleScriptZombiePeerAsync(rig.Host, report, default);
            Assert.Equal(2, peers.To(rig.Host).Length);
            Assert.Equal(34, room.DecidedWinnerTeam);
            Assert.Equal(RoundOutcome.Won, Assert.Single(rig.Ended.To(rig.Host)).Outcome);
            Assert.Single(rig.Ended.To(rig.Guest));
            Assert.Null(room.ZombieKingSlot);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Small zombies have no king-loss condition; an owned death cannot award humans an early win.</summary>
    [Fact]
    public async Task ScriptZombieDeathAndUnqualifiedStageReportDoNotEndTheRound()
    {
        var rig = NewScriptZombieRig();
        EnableScriptZombie(rig);
        try
        {
            var room = await StartScriptZombieAsync(rig);
            var guest = room.FindByGame(rig.Guest.Id)!;
            await rig.Round.HandleScriptZombieConversionAsync(rig.Host, new(guest.Slot, 204, 110288), default);
            await rig.Round.HandleScriptZombiePeerAsync(rig.Guest, new(guest.Slot, 34, 0, 204, 110288), default);
            var actor = RoomRegistry.SceneEntity(guest.Slot);
            await rig.Round.HandleLifeReportedAsync(rig.Guest, new(RoundLifeEvent.Died, actor), default);
            await rig.Round.HandleLifeReportedAsync(rig.Guest, new(RoundLifeEvent.DeathCleaned, actor), default);
            await rig.Round.HandleEndRequestedAsync(rig.Guest, new(), default);
            Assert.Null(room.ZombieKingSlot);
            Assert.Null(room.DecidedWinnerTeam);
            Assert.Equal(RoomPhase.Playing, room.Phase);
            Assert.Empty(rig.Ended.To(rig.Host));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>An explicitly supplied pass-through policy retains its audience and bytes outside the enabled mode.</summary>
    [Theory]
    [InlineData(3, false, false)]
    [InlineData(28, false, true)]
    [InlineData(28, true, false)]
    [InlineData(43, true, true)]
    [InlineData(44, false, false)]
    public async Task ScriptZombiePeerHonorsDeclaredOtherModeFeed(int mode, bool relay, bool echo)
    {
        var rig = NewScriptZombieRig();
        var (_, peers) = EnableScriptZombie(rig);
        try
        {
            await rig.StartAsync();
            var room = rig.Registry.Find(rig.RoomId)!;
            room.ModeId = checked((ushort)mode);
            var input = relay ? new RecordingSession(92) : rig.Guest;
            if (relay) rig.Registry.BindRelay(input, room.FindByGame(rig.Guest.Id)!.Key);
            byte[] body = [34, 1, 0, 0, 0, 0, 0, 0, 0, 204, 0, 0, 0, 208, 174, 1, 0];
            var report = new RoundScriptZombieConversionReported(1, 34, 0, 204, 110288)
            {
                OtherModeFeed = new(33287, body) { IncludeSender = echo },
            };
            await rig.Round.HandleScriptZombiePeerAsync(input, report, default);
            var forwarded = Assert.Single(rig.Feed.To(rig.Host));
            Assert.Equal(33287, forwarded.Code);
            Assert.Equal(body, forwarded.Payload.ToArray());
            Assert.Equal(echo ? 1 : 0, rig.Feed.To(rig.Guest).Length);
            Assert.Empty(peers.To(rig.Host));
            Assert.Null(room.ScriptZombies);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Without declared pass-through, the shared operation gives other modes no forwarding authority.</summary>
    [Theory]
    [InlineData(3)]
    [InlineData(28)]
    [InlineData(43)]
    [InlineData(44)]
    public async Task ScriptZombiePeerWithoutFallbackCannotForwardOutsideEnabledMode(int mode)
    {
        var rig = NewScriptZombieRig();
        var (_, peers) = EnableScriptZombie(rig);
        try
        {
            await rig.StartAsync();
            var room = rig.Registry.Find(rig.RoomId)!;
            room.ModeId = checked((ushort)mode);
            await rig.Round.HandleScriptZombiePeerAsync(rig.Guest, new(1, 34, 0, 204, 110288), default);
            Assert.Empty(rig.Feed.To(rig.Host));
            Assert.Empty(rig.Feed.To(rig.Guest));
            Assert.Empty(peers.To(rig.Host));
            Assert.Null(room.ScriptZombies);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A shared-operation fallback never bypasses the enabled mode's conversion authority.</summary>
    [Fact]
    public async Task ScriptZombiePeerFallbackCannotBypassModeEightAuthority()
    {
        var rig = NewScriptZombieRig();
        var (_, peers) = EnableScriptZombie(rig);
        try
        {
            var room = await StartScriptZombieAsync(rig);
            var report = new RoundScriptZombieConversionReported(room.FindByGame(rig.Guest.Id)!.Slot, 34, 0, 204, 110288)
            {
                OtherModeFeed = new(33287, new byte[] { 1, 2, 3 }) { IncludeSender = true },
            };
            await rig.Round.HandleScriptZombiePeerAsync(rig.Guest, report, default);
            rig.Round.ScriptZombieConversions = null;
            await rig.Round.HandleScriptZombiePeerAsync(rig.Guest, report, default);
            Assert.Empty(rig.Feed.To(rig.Host));
            Assert.Empty(rig.Feed.To(rig.Guest));
            Assert.Empty(peers.To(rig.Host));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Queued shared-peer forwarding cannot migrate into another generation or a newly started zombie round.</summary>
    [Theory]
    [InlineData(8)]
    [InlineData(28)]
    public async Task ScriptZombieOtherModeFeedCannotCrossRestart(int nextMode)
    {
        var rig = NewScriptZombieRig();
        EnableScriptZombie(rig);
        try
        {
            await rig.StartAsync();
            var room = rig.Registry.Find(rig.RoomId)!;
            room.ModeId = 28;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var held = room.Executor.ExecuteAsync(async _ =>
            {
                entered.SetResult();
                await release.Task;
                lock (room.Gate)
                {
                    room.ResetToWaiting();
                    room.ModeId = checked((ushort)nextMode);
                    room.MarkStarting();
                    rig.Round.InitializeScriptZombies(room);
                    room.MarkPlaying();
                }
            }, default);
            await entered.Task;
            var queued = rig.Round.HandleScriptZombiePeerAsync(rig.Guest, new(1, 34, 0, 204, 110288)
            {
                OtherModeFeed = new(33287, new byte[] { 1, 2, 3 }),
            }, default).AsTask();
            release.SetResult();
            await held;
            await queued;
            Assert.Empty(rig.Feed.To(rig.Host));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Reports received in another mode cannot kill or revive a new script incarnation after an executor delay.</summary>
    [Theory]
    [InlineData(false, 8)]
    [InlineData(true, 8)]
    [InlineData(false, 28)]
    [InlineData(true, 28)]
    public async Task ScriptZombieRejectsQueuedLifeAndResetFromPreviousMode(bool reset, int mode)
    {
        var rig = NewScriptZombieRig(mode: mode);
        EnableScriptZombie(rig);
        try
        {
            await rig.StartAsync();
            var room = rig.Registry.Find(rig.RoomId)!;
            Assert.Equal(4, room.ModeId);
            var guest = room.FindByGame(rig.Guest.Id)!;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var held = room.Executor.ExecuteAsync(async _ =>
            {
                entered.SetResult();
                await release.Task;
                lock (room.Gate)
                {
                    room.ResetToWaiting();
                    room.ModeId = checked((ushort)mode);
                    room.MarkStarting();
                    rig.Round.InitializeScriptZombies(room);
                    room.MarkPlaying();
                    if (reset) guest.Life.Die();
                }
            }, default);
            await entered.Task;
            var actor = RoomRegistry.SceneEntity(guest.Slot);
            var queued = reset
                ? rig.Round.HandleSpawnAcceptedAsync(rig.Guest, new(actor, 0), default).AsTask()
                : rig.Round.HandleLifeReportedAsync(rig.Guest, new(RoundLifeEvent.Died, actor), default).AsTask();
            release.SetResult();
            await held;
            await queued;
            Assert.Equal(reset ? SeatLife.Dead : SeatLife.Alive, guest.Life.State);
            Assert.Empty(rig.Respawned.To(rig.Guest));
            Assert.Equal(0, room.ScriptZombies!.InitialZombies);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>The native zombie allegiance belongs to one round and cannot leak into later room modes.</summary>
    [Fact]
    public async Task ScriptZombieReturnRestoresWaitingTeamsAndClearsConversions()
    {
        var rig = NewScriptZombieRig();
        var (teams, _) = EnableScriptZombie(rig);
        try
        {
            await rig.Rooms.HandleCreateAsync(rig.Host, new("Zombies", "", 8, "map"), default);
            var room = rig.Registry.TryGet(rig.Host.Id)!;
            await rig.Rooms.HandleJoinAsync(rig.Guest, new(room.Id, ""), default);
            var guest = room.FindByGame(rig.Guest.Id)!;
            guest.Team = 2;
            var waitingTeams = room.Seats.Select(seat => seat.Team).ToArray();
            await rig.Rooms.HandleReadyAsync(rig.Guest, new(true), default);
            await rig.Rooms.HandleStartAsync(rig.Host, new(room.MapKey), default);
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            var conversion = new ScriptZombieConversion(guest.Slot, 204, 110288);
            await rig.Round.HandleScriptZombieConversionAsync(rig.Host, conversion, default);
            Assert.Equal(34, guest.Team);
            room.ResetToWaiting();
            Assert.Null(room.ScriptZombies);
            Assert.Equal(waitingTeams, room.Seats.Select(seat => seat.Team).ToArray());
            await rig.Round.HandleScriptZombieConversionAsync(rig.Host, conversion, default);
            Assert.Single(teams.To(rig.Host));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    private static Rig NewScriptZombieRig(Action<InGameOptions>? configure = null, int mode = 8) => new(configure: options =>
    {
        options.ScriptZombies = mode == 8;
        options.GhostZombies = mode == 28;
        options.NativeLifeModeIds = [mode];
        configure?.Invoke(options);
    });

    private static (Sink<RoundScriptZombieTeamChanged> Teams, Sink<RoundScriptZombieConversionReported> Peers) EnableScriptZombie(Rig rig)
    {
        var teams = new Sink<RoundScriptZombieTeamChanged>();
        var peers = new Sink<RoundScriptZombieConversionReported>();
        rig.Round.ScriptZombieTeams = teams;
        rig.Round.ScriptZombieConversions = peers;
        rig.Rooms.ScriptZombiesStarting = rig.Round.InitializeScriptZombies;
        rig.Rooms.ScriptZombieConversionRequested = rig.Round.HandleScriptZombieConversionAsync;
        return (teams, peers);
    }

    private static Task<PlayRoom> StartScriptZombieAsync(Rig rig, params ISession[] extra) => StartScriptZombieModeAsync(rig, 8, extra);

    private static async Task<PlayRoom> StartScriptZombieModeAsync(Rig rig, int mode, params ISession[] extra)
    {
        await rig.Rooms.HandleCreateAsync(rig.Host, new("Zombies", "", mode, "map"), default);
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
}
