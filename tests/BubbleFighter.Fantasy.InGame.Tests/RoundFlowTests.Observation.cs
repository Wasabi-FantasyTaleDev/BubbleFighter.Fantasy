using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Tests.Shared;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    /// <summary>The dead owner receives observation with its peers, once, without any revival or reward settlement.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BombRespawnBecomesOneObservation(bool serverTimed)
    {
        var rig = new Rig(observation: true, configure: options =>
        {
            options.RespawnDisabledModeIds = [19];
            options.PveRevivalModes = [19];
        });
        try
        {
            var room = rig.Registry.Find(await StartBombRoundAsync(rig))!;
            var life = room.FindByGame(rig.Guest.Id)!.Life;
            var request = new RoundSpawnAccepted(1_100_000, 1) { ActorSeat = 1 };
            await rig.Round.HandlePveRevivalAsync(rig.Guest, new(), default);
            Assert.False(life.RevivalRequestedEarly);
            await rig.Round.HandleSpawnAcceptedAsync(rig.Guest, request, default);
            Assert.Empty(rig.Observed.To(rig.Host));
            await rig.Round.HandleDeathReportedAsync(rig.Guest,
                new(1_100_000, 5, serverTimed) { ActorSeat = 1 }, default);
            Assert.False(life.ServerTimedWait);
            await rig.Round.HandleInstantRevivalAsync(rig.Guest, new(1_100_000, 1), default);
            await rig.Round.HandlePveRevivalAsync(rig.Guest, new(), default);
            await rig.Round.HandleRespawnRequestedAsync(rig.Guest, new(1_100_000) { ActorSeat = 1 }, default);
            await rig.Round.HandleSpawnAcceptedAsync(rig.Guest, request, default);
            await rig.Round.HandleDeathReportedAsync(rig.Guest,
                new(1_100_000, 5, serverTimed) { ActorSeat = 1 }, default);
            await rig.Round.HandleSpawnAcceptedAsync(rig.Guest, request, default);
            await Task.Delay(30);
            Assert.Equal(SeatLife.Dead, life.State);
            Assert.True(life.ObservationSent);
            foreach (var member in new[] { rig.Host, rig.Guest })
            {
                Assert.Equal(new RoundPlayerObserved(1_100_000), Assert.Single(rig.Observed.To(member)));
                Assert.Empty(rig.SpawnOffered.To(member));
                Assert.Empty(rig.Respawned.To(member));
                Assert.Empty(rig.Ended.To(member));
            }
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>An observation sender never disables bomb respawn without an explicit mode rule.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BombRespawnIsAllowedUnlessExplicitlyDenied(bool observation)
    {
        var rig = new Rig(observation: observation);
        try
        {
            var room = rig.Registry.Find(await StartBombRoundAsync(rig))!;
            await rig.Round.HandleDeathReportedAsync(rig.Guest, new(1_100_000, 5) { ActorSeat = 1 }, default);
            await rig.Round.HandleSpawnAcceptedAsync(rig.Guest, new(1_100_000, 1) { ActorSeat = 1 }, default);
            Assert.Single(rig.Respawned.To(rig.Guest));
            Assert.Equal(SeatLife.Alive, room.FindByGame(rig.Guest.Id)!.Life.State);
            Assert.Empty(rig.Observed.To(rig.Host));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A stale reset after restart cannot observe the new life; the next admitted death can notify again.</summary>
    [Fact]
    public async Task ObservationClearsWithTheSharedRoundGeneration()
    {
        var rig = new Rig(observation: true, configure: options => options.RespawnDisabledModeIds = [19]);
        try
        {
            var room = rig.Registry.Find(await StartBombRoundAsync(rig))!;
            var life = room.FindByGame(rig.Guest.Id)!.Life;
            var request = new RoundSpawnAccepted(1_100_000, 1) { ActorSeat = 1 };
            await rig.Round.HandleDeathReportedAsync(rig.Guest, new(1_100_000, 5) { ActorSeat = 1 }, default);
            await rig.Round.HandleSpawnAcceptedAsync(rig.Guest, request, default);
            var previousGeneration = room.StartGeneration;
            lock (room.Gate)
            {
                room.ResetToWaiting();
                room.MarkStarting();
                room.MarkPlaying();
            }
            Assert.Equal(previousGeneration + 1, room.StartGeneration);
            Assert.Equal(SeatLife.Alive, life.State);
            Assert.False(life.ObservationSent);
            Assert.Null(life.DeathActor);
            await rig.Round.HandleSpawnAcceptedAsync(rig.Guest, request, default);
            Assert.Single(rig.Observed.To(rig.Host));
            await rig.Round.HandleDeathReportedAsync(rig.Guest, new(1_100_000, 5) { ActorSeat = 1 }, default);
            await rig.Round.HandleSpawnAcceptedAsync(rig.Guest, request, default);
            Assert.Equal(2, rig.Observed.To(rig.Host).Length);
            Assert.Equal(SeatLife.Dead, life.State);
            Assert.Empty(rig.Respawned.To(rig.Guest));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A rule that denies one mode leaves ordinary, later and unknown modes on the shared respawn path.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(24)]
    [InlineData(30)]
    [InlineData(106)]
    [InlineData(65535)]
    public async Task ObservationSenderLeavesOtherModesRespawnUnchanged(int mode)
    {
        var rig = new Rig(observation: true, configure: options => options.RespawnDisabledModeIds = [19]);
        try
        {
            await rig.StartAsync();
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Guest);
            var room = rig.Registry.Find(rig.RoomId)!;
            lock (room.Gate) room.ModeId = checked((ushort)mode);
            await rig.Round.HandleDeathReportedAsync(rig.Guest, new(1_100_000, 5) { ActorSeat = 1 }, default);
            await rig.Round.HandleRespawnRequestedAsync(rig.Guest, new(1_100_000) { ActorSeat = 1 }, default);
            Assert.Single(rig.SpawnOffered.To(rig.Guest));
            await rig.Round.HandleSpawnAcceptedAsync(rig.Guest, new(1_100_000, 1) { ActorSeat = 1 }, default);
            Assert.Single(rig.Respawned.To(rig.Host));
            Assert.Equal(SeatLife.Alive, rig.Registry.Find(rig.RoomId)!.FindByGame(rig.Guest.Id)!.Life.State);
            Assert.Empty(rig.Observed.To(rig.Host));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Another seat, a different actor, an outsider and a loading report cannot observe an admitted death.</summary>
    [Fact]
    public async Task ObservationRequiresThePlayingDeadOwner()
    {
        var rig = new Rig(observation: true, configure: options => options.RespawnDisabledModeIds = [19]);
        try
        {
            var room = rig.Registry.Find(await StartBombRoundAsync(rig))!;
            await rig.Round.HandleDeathReportedAsync(rig.Guest, new(1_100_000, 5) { ActorSeat = 1 }, default);
            var request = new RoundSpawnAccepted(1_100_000, 1) { ActorSeat = 1 };
            await rig.Round.HandleSpawnAcceptedAsync(rig.Host, request, default);
            await rig.Round.HandleSpawnAcceptedAsync(rig.Guest, new(1_200_000, 1) { ActorSeat = 1 }, default);
            var outsider = new RecordingSession(44);
            outsider.SetLobbyPlayer(44, "outsider");
            await rig.Round.HandleSpawnAcceptedAsync(outsider, request, default);
            Assert.Empty(rig.Observed.To(rig.Host));
            lock (room.Gate)
            {
                room.ResetToWaiting();
                room.MarkStarting();
                room.FindByGame(rig.Guest.Id)!.Life.RecordDeath(1_100_000, false);
            }
            await rig.Round.HandleSpawnAcceptedAsync(rig.Guest, request, default);
            Assert.Empty(rig.Observed.To(rig.Host));
            Assert.False(room.FindByGame(rig.Guest.Id)!.Life.ObservationSent);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>An explicit deny without its native observation sender is a configuration error, not a silent fallback.</summary>
    [Fact]
    public void ExplicitRespawnDenialRequiresAnObservationSender()
    {
        var error = Assert.Throws<ConfigException>(() => new Rig(configure: options => options.RespawnDisabledModeIds = [19]));
        Assert.Contains("respawn_disabled_mode_ids", error.Message, StringComparison.Ordinal);
    }

    /// <summary>The generic rule does not require bomb's team identifiers and can explicitly deny another mode.</summary>
    [Fact]
    public async Task ExplicitRespawnDenialIsNotBombOrTeamSpecific()
    {
        var rig = new Rig(observation: true, configure: options => options.RespawnDisabledModeIds = [4]);
        try
        {
            await rig.StartAsync();
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Guest);
            var room = rig.Registry.Find(rig.RoomId)!;
            lock (room.Gate) room.FindByGame(rig.Guest.Id)!.Team = 0;
            await rig.Round.HandleDeathReportedAsync(rig.Guest, new(1_100_000, 5) { ActorSeat = 1 }, default);
            await rig.Round.HandleSpawnAcceptedAsync(rig.Guest, new(1_100_000, 1) { ActorSeat = 1 }, default);
            Assert.Equal(new RoundPlayerObserved(1_100_000), Assert.Single(rig.Observed.To(rig.Host)));
            Assert.Equal(SeatLife.Dead, room.FindByGame(rig.Guest.Id)!.Life.State);
            Assert.Empty(rig.Respawned.To(rig.Guest));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Zombie revival and post-death conversion cannot bypass an explicit mode-level respawn denial.</summary>
    [Theory]
    [InlineData(2)]
    [InlineData(15)]
    public async Task ExplicitRespawnDenialAlsoBlocksZombieRevival(int team)
    {
        var rig = new Rig(zombieConversionModeId: 4, observation: true,
            configure: options => options.RespawnDisabledModeIds = [4]);
        try
        {
            await rig.StartAsync();
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Guest);
            var room = rig.Registry.Find(rig.RoomId)!;
            var seat = room.FindByGame(rig.Guest.Id)!;
            lock (room.Gate) seat.Team = checked((byte)team);
            var count = rig.TeamConverted.To(rig.Host).Length;
            await rig.Round.HandleDeathReportedAsync(rig.Guest, new(1_100_000, 5, true) { ActorSeat = 1 }, default);
            await rig.Rooms.TryPveZombieRevivalAsync(rig.Guest, default);
            await rig.Rooms.HandleTeamChangeAsync(rig.Guest, new(), default);
            Assert.Equal(team, seat.Team);
            Assert.Equal(SeatLife.Dead, seat.Life.State);
            Assert.Equal(count, rig.TeamConverted.To(rig.Host).Length);
            Assert.Empty(rig.Respawned.To(rig.Guest));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Battle conversions obey explicit denial while the existing king-defeat outcome remains intact.</summary>
    [Theory]
    [InlineData(1, false)]
    [InlineData(14, false)]
    [InlineData(14, true)]
    public async Task ExplicitRespawnDenialAlsoBlocksZombieBattleRevival(int team, bool king)
    {
        var rig = new Rig(observation: true, configure: options =>
        {
            options.RespawnDisabledModeIds = [4];
            options.ZombiePvpModeIds = [4];
            options.ZombieKingDelayMs = 60_000;
        });
        try
        {
            await StartZombiePvpAsync(rig);
            var room = rig.Registry.Find(rig.RoomId)!;
            var seat = room.FindByGame(rig.Guest.Id)!;
            lock (room.Gate)
            {
                seat.Team = checked((byte)team);
                room.ZombieKingSlot = king ? seat.Slot : null;
            }
            var count = rig.TeamConverted.To(rig.Host).Length;
            await rig.Round.HandleDeathReportedAsync(rig.Guest, new(1_100_000, 5) { ActorSeat = 1 }, default);
            await rig.Rooms.HandleTeamChangeAsync(rig.Guest, new(), default);
            Assert.Equal(team, seat.Team);
            Assert.Equal(SeatLife.Dead, seat.Life.State);
            Assert.Equal(count, rig.TeamConverted.To(rig.Host).Length);
            Assert.Empty(rig.Respawned.To(rig.Guest));
            Assert.Null(room.DecidedWinnerTeam);
            if (king)
            {
                await rig.Round.HandleLifeReportedAsync(rig.Guest,
                    new(RoundLifeEvent.DeathCleaned, RoomRegistry.SceneEntity(seat.Slot)), default);
                Assert.Equal(ZombieHuman, room.DecidedWinnerTeam);
            }
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>An opening transformation cannot revive a dead human, but a bubbled living player remains eligible.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitRespawnDenialExcludesDeadPlayersFromKingSelection(bool dead)
    {
        var select = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rig = new Rig(observation: true, configure: options =>
        {
            options.RespawnDisabledModeIds = [4];
            options.ZombiePvpModeIds = [4];
        }, zombieKingDelay: (_, ct) => new ValueTask(select.Task.WaitAsync(ct)));
        try
        {
            await StartZombiePvpAsync(rig);
            var room = rig.Registry.Find(rig.RoomId)!;
            if (dead)
            {
                await rig.Round.HandleDeathReportedAsync(rig.Host, new(1_000_000, 5) { ActorSeat = 0 }, default);
                await rig.Round.HandleDeathReportedAsync(rig.Guest, new(1_100_000, 5) { ActorSeat = 1 }, default);
            }
            else
            {
                lock (room.Gate)
                    foreach (var seat in room.Seats) seat.Life.Bubble();
            }
            var count = rig.TeamConverted.To(rig.Host).Length;
            select.SetResult();
            await room.ZombieKingTask!;
            Assert.Equal(dead, room.ZombieKingSlot is null);
            Assert.Equal(count + (dead ? 0 : 1), rig.TeamConverted.To(rig.Host).Length);
            Assert.All(room.Seats, seat => Assert.Equal(dead ? SeatLife.Dead : SeatLife.Bubble, seat.Life.State));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A matching native reset confirmation still cannot override an explicit respawn prohibition.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitRespawnDenialAlsoGuardsResetConfirmation(bool denied)
    {
        var rig = new Rig(observation: true, configure: options =>
        {
            options.RespawnDisabledModeIds = denied ? [4] : [];
            options.NativeLifeEveryMode = true;
        });
        try
        {
            await rig.StartAsync();
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Guest);
            var room = rig.Registry.Find(rig.RoomId)!;
            var life = room.FindByGame(rig.Guest.Id)!.Life;
            lock (room.Gate)
            {
                life.RecordDeath(RoomRegistry.SceneEntity(1), false);
                life.Grant(rig.Guest);
            }
            await rig.Round.HandleLifeReportedAsync(rig.Guest,
                new(RoundLifeEvent.ResetConfirmed, RoomRegistry.SceneEntity(1)), default);
            Assert.Equal(denied ? SeatLife.ResetPending : SeatLife.Alive, life.State);
            Assert.Equal(denied ? 1 : 0, room.LifeChecks.Count(LifeCheck.ResetConfirmedWhileRespawnDisabled));
            Assert.Equal(0, room.LifeChecks.Count(LifeCheck.ResetConfirmedOnOtherLink));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }
}
