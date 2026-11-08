using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Tests.Shared;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    /// <summary>Ghost mode admits exactly one master-selected king and ignores the Lua duplicate with a zero weapon.</summary>
    [Theory]
    [InlineData(201)]
    [InlineData(203)]
    public async Task GhostZombieSelectsOneKingAndNeverReappliesTheZeroWeaponDuplicate(int kind)
    {
        var rig = NewScriptZombieRig(mode: 28);
        var (teams, peers) = EnableScriptZombie(rig);
        try
        {
            var extra = Enumerable.Range(3, 3).Select(index => CaptainPlayer(index * 11, "player" + index)).ToArray();
            var room = await StartScriptZombieModeAsync(rig, 28, extra);
            var guest = room.FindByGame(rig.Guest.Id)!;
            var selection = new ScriptZombieConversion(guest.Slot, kind, 110290);
            await rig.Round.HandleScriptZombieConversionAsync(rig.Guest, selection, default);
            await rig.Round.HandleScriptZombieConversionAsync(rig.Host, selection with { ZombieKind = 204 }, default);
            await rig.Round.HandleScriptZombieConversionAsync(rig.Host, selection with { WeaponItemId = 110288 }, default);
            await rig.Round.HandleScriptZombieConversionAsync(rig.Host, selection with { WeaponItemId = 0 }, default);
            Assert.Empty(teams.To(rig.Host));
            Assert.Equal(0, room.ScriptZombies!.InitialZombies);
            await rig.Round.HandleScriptZombieConversionAsync(rig.Host, selection, default);
            await rig.Round.HandleScriptZombieConversionAsync(rig.Host, selection with { WeaponItemId = 0 }, default);
            await rig.Round.HandleScriptZombieConversionAsync(rig.Host, selection with { Seat = room.FindByGame(extra[0].Id)!.Slot }, default);
            Assert.Equal(1, room.ScriptZombies.InitialZombieLimit);
            Assert.Equal(1, room.ScriptZombies.InitialZombies);
            Assert.Equal(guest.Slot, room.ScriptZombies.KingSlot);
            Assert.Single(teams.To(rig.Host));
            await rig.Round.HandleScriptZombiePeerAsync(rig.Guest, new(guest.Slot, 34, 0, kind, 110290), default);
            var generation = guest.Life.Generation;
            await rig.Round.HandleScriptZombieConversionAsync(rig.Host, selection with { WeaponItemId = 0 }, default);
            await rig.Round.HandleLifeReportedAsync(rig.Guest, new(RoundLifeEvent.Died, RoomRegistry.SceneEntity(guest.Slot)), default);
            await rig.Round.HandleScriptZombieConversionAsync(rig.Guest, selection with { WeaponItemId = 0 }, default);
            await rig.Round.HandleScriptZombieConversionAsync(rig.Guest, selection, default);
            Assert.Equal(generation, guest.Life.Generation);
            Assert.Equal(SeatLife.Dead, guest.Life.State);
            Assert.Single(teams.To(rig.Host));
            Assert.Single(peers.To(rig.Host));
            Assert.Null(room.DecidedWinnerTeam);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Only the confirmed king's owned cleanup ends ghost mode; bubbles and raw stage requests are insufficient.</summary>
    [Theory]
    [InlineData(201, false)]
    [InlineData(201, true)]
    [InlineData(203, false)]
    [InlineData(203, true)]
    public async Task GhostZombieKingCleanupAwardsHumansWithoutAllowingKingRespawn(int kind, bool reportDeath)
    {
        var rig = NewScriptZombieRig(options => options.ResultCloseGraceMs = 60_000, 28);
        var (_, peers) = EnableScriptZombie(rig);
        try
        {
            var room = await StartScriptZombieModeAsync(rig, 28);
            var king = room.FindByGame(rig.Guest.Id)!;
            var actor = RoomRegistry.SceneEntity(king.Slot);
            await rig.Round.HandleScriptZombieConversionAsync(rig.Host, new(king.Slot, kind, 110290), default);
            await rig.Round.HandleScriptZombiePeerAsync(rig.Guest, new(king.Slot, 34, 0, kind, 110290), default);
            await rig.Round.HandleLifeReportedAsync(rig.Host, new(RoundLifeEvent.DeathCleaned, actor), default);
            await rig.Round.HandleEndRequestedAsync(rig.Host, new(), default);
            Assert.Empty(rig.Ended.To(rig.Host));
            Assert.Equal(SeatLife.Alive, king.Life.State);
            if (reportDeath)
            {
                await rig.Round.HandleLifeReportedAsync(rig.Guest, new(RoundLifeEvent.Bubbled, actor), default);
                Assert.Empty(rig.Ended.To(rig.Host));
                await rig.Round.HandleLifeReportedAsync(rig.Guest, new(RoundLifeEvent.Died, actor), default);
                await rig.Round.HandleSpawnAcceptedAsync(rig.Guest, new(actor, 0), default);
                await rig.Round.HandleScriptZombiePeerAsync(rig.Guest, new(king.Slot, 34, 1, kind, 110290), default);
                Assert.Empty(rig.Respawned.To(rig.Guest));
                Assert.Single(peers.To(rig.Host));
                Assert.Equal(SeatLife.Dead, king.Life.State);
                Assert.Empty(rig.Ended.To(rig.Host));
            }
            await rig.Round.HandleLifeReportedAsync(rig.Guest, new(RoundLifeEvent.DeathCleaned, actor), default);
            await rig.Round.HandleLifeReportedAsync(rig.Guest, new(RoundLifeEvent.DeathCleaned, actor), default);
            Assert.Equal(1, room.DecidedWinnerTeam);
            Assert.True(king.Life.DeathCleaned);
            Assert.Equal(RoundOutcome.Won, Assert.Single(rig.Ended.To(rig.Host)).Outcome);
            Assert.Equal(RoundOutcome.Lost, Assert.Single(rig.Ended.To(rig.Guest)).Outcome);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A missing native master or king cancels without fabricating a death result or awarding players.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GhostZombieKingOrMasterDepartureCancelsWithoutAwards(bool kingLeaves)
    {
        var rig = NewScriptZombieRig(mode: 28);
        EnableScriptZombie(rig);
        try
        {
            var room = await StartScriptZombieModeAsync(rig, 28, CaptainPlayer(33, "third"));
            var king = room.FindByGame(rig.Guest.Id)!;
            await rig.Round.HandleScriptZombieConversionAsync(rig.Host, new(king.Slot, 201, 110290), default);
            await rig.Round.HandleScriptZombiePeerAsync(rig.Guest, new(king.Slot, 34, 0, 201, 110290), default);
            await rig.Rooms.HandleLeaveAsync(kingLeaves ? rig.Guest : rig.Host, new(), default);
            Assert.True(room.RoundCancelled);
            Assert.Null(room.DecidedWinnerTeam);
            Assert.Empty(rig.Ended.To(rig.Host));
            Assert.Empty(rig.Ended.To(rig.Guest));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Each script option grants authority only to its own mode, even though both use the same packet.</summary>
    [Theory]
    [InlineData(8, 28, 201, 110290)]
    [InlineData(28, 8, 204, 110288)]
    public async Task ScriptZombieOptionsDoNotAuthorizeTheOtherScriptMode(int enabledMode, int roomMode, int kind, int weapon)
    {
        var rig = NewScriptZombieRig(mode: enabledMode);
        var (teams, peers) = EnableScriptZombie(rig);
        try
        {
            var room = await StartScriptZombieModeAsync(rig, roomMode);
            var guest = room.FindByGame(rig.Guest.Id)!;
            await rig.Round.HandleScriptZombieConversionAsync(rig.Host, new(guest.Slot, kind, weapon), default);
            await rig.Round.HandleScriptZombiePeerAsync(rig.Guest, new(guest.Slot, 34, 0, kind, weapon), default);
            Assert.Null(room.ScriptZombies);
            Assert.Empty(teams.To(rig.Host));
            Assert.Empty(peers.To(rig.Host));
            Assert.Empty(rig.Feed.To(rig.Host));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Native ULTRA and its timer restoration keep the current life and reject foreign, forged and duplicate phases.</summary>
    [Theory]
    [InlineData(8, 204, 110288, null)]
    [InlineData(8, 207, 110288, -1)]
    [InlineData(28, 201, 110290, null)]
    [InlineData(28, 203, 110290, -1)]
    public async Task ScriptZombieUltraPreservesLifeAndRequiresOwnedConfirmedPhases(int mode, int kind, int weapon, int? absentKind)
    {
        var rig = NewScriptZombieRig(mode: mode);
        var (_, peers) = EnableScriptZombie(rig);
        try
        {
            var room = await StartScriptZombieModeAsync(rig, mode);
            var guest = room.FindByGame(rig.Guest.Id)!;
            var ultra = new RoundScriptZombieConversionReported(guest.Slot, 34, 2, absentKind, 0);
            var restore = new RoundScriptZombieConversionReported(guest.Slot, 34, 1, kind, weapon);
            await rig.Round.HandleScriptZombieConversionAsync(rig.Host, new(guest.Slot, kind, weapon), default);
            await rig.Round.HandleScriptZombiePeerAsync(rig.Guest, ultra, default);
            Assert.Empty(peers.To(rig.Host));
            await rig.Round.HandleScriptZombiePeerAsync(rig.Guest, restore with { Operation = 0 }, default);
            var generation = guest.Life.Generation;
            await rig.Round.HandleScriptZombiePeerAsync(rig.Guest, restore, default);
            await rig.Round.HandleScriptZombiePeerAsync(rig.Host, ultra, default);
            await rig.Round.HandleScriptZombiePeerAsync(rig.Guest, ultra with { ZombieKind = kind }, default);
            await rig.Round.HandleScriptZombiePeerAsync(rig.Guest, ultra with { WeaponItemId = weapon }, default);
            await rig.Round.HandleScriptZombiePeerAsync(rig.Guest, ultra with { Team = 1 }, default);
            Assert.Single(peers.To(rig.Host));
            await rig.Round.HandleScriptZombiePeerAsync(rig.Guest, ultra, default);
            await rig.Round.HandleScriptZombiePeerAsync(rig.Guest, ultra, default);
            Assert.Equal(2, peers.To(rig.Host).Length);
            Assert.Equal(generation, guest.Life.Generation);
            Assert.Equal(SeatLife.Alive, guest.Life.State);
            await rig.Round.HandleScriptZombiePeerAsync(rig.Guest, restore with { ZombieKind = 999 }, default);
            await rig.Round.HandleScriptZombiePeerAsync(rig.Guest, restore with { WeaponItemId = 0 }, default);
            Assert.Equal(2, peers.To(rig.Host).Length);
            await rig.Round.HandleScriptZombiePeerAsync(rig.Guest, restore, default);
            await rig.Round.HandleScriptZombiePeerAsync(rig.Guest, restore, default);
            Assert.Equal(3, peers.To(rig.Host).Length);
            Assert.Equal(generation, guest.Life.Generation);
            await rig.Round.HandleScriptZombiePeerAsync(rig.Guest, ultra, default);
            await rig.Round.HandleLifeReportedAsync(rig.Guest, new(RoundLifeEvent.Died, RoomRegistry.SceneEntity(guest.Slot)), default);
            await rig.Round.HandleScriptZombiePeerAsync(rig.Guest, restore, default);
            await rig.Round.HandleScriptZombiePeerAsync(rig.Guest, ultra, default);
            Assert.Equal(4, peers.To(rig.Host).Length);
            Assert.Equal(generation, guest.Life.Generation);
            Assert.Equal(SeatLife.Dead, guest.Life.State);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Later small zombies retain their kind after shared respawn and cannot activate ULTRA before base confirmation.</summary>
    [Theory]
    [InlineData(8, 204, 110288)]
    [InlineData(28, 201, 110290)]
    public async Task ScriptZombieSmallInfectionAndRespawnRetainTheAuthorizedKind(int mode, int initialKind, int weapon)
    {
        var rig = NewScriptZombieRig(mode: mode);
        var (teams, peers) = EnableScriptZombie(rig);
        try
        {
            var first = CaptainPlayer(33, "first");
            var room = await StartScriptZombieModeAsync(rig, mode, first);
            var initial = room.FindByGame(first.Id)!;
            var guest = room.FindByGame(rig.Guest.Id)!;
            await rig.Round.HandleScriptZombieConversionAsync(rig.Host, new(initial.Slot, initialKind, weapon), default);
            await rig.Round.HandleScriptZombiePeerAsync(first, new(initial.Slot, 34, 0, initialKind, weapon), default);
            var selection = new ScriptZombieConversion(guest.Slot, 205, weapon);
            await rig.Round.HandleScriptZombieConversionAsync(rig.Guest, selection, default);
            Assert.Single(teams.To(rig.Host));
            var actor = RoomRegistry.SceneEntity(guest.Slot);
            await rig.Round.HandleLifeReportedAsync(rig.Guest, new(RoundLifeEvent.Died, actor), default);
            await rig.Round.HandleScriptZombieConversionAsync(rig.Host, selection, default);
            Assert.Single(teams.To(rig.Host));
            await rig.Round.HandleScriptZombieConversionAsync(rig.Guest, selection, default);
            await rig.Round.HandleScriptZombiePeerAsync(rig.Guest, new(guest.Slot, 34, 0, 205, weapon), default);
            Assert.Equal(2, teams.To(rig.Host).Length);
            var generation = guest.Life.Generation;
            var relay = new RecordingSession(92);
            rig.Registry.BindRelay(relay, guest.Key);
            await rig.Round.HandleLifeReportedAsync(rig.Guest, new(RoundLifeEvent.Died, actor), default);
            await rig.Round.HandleLifeReportedAsync(rig.Guest, new(RoundLifeEvent.DeathCleaned, actor), default);
            Assert.Empty(rig.Ended.To(rig.Host));
            await rig.Round.HandleSpawnAcceptedAsync(rig.Guest, new(actor, 0)
            {
                Rules = new(RoundRespawnAudience.Requester, RoundRespawnCompletion.MatchConfirmation, RoundRespawnAdmission.CleanedDeath),
            }, default);
            await rig.Round.HandleLifeReportedAsync(relay, new(RoundLifeEvent.ResetConfirmed, actor), default);
            Assert.Equal(SeatLife.Alive, guest.Life.State);
            Assert.True(guest.Life.Generation > generation);
            await rig.Round.HandleScriptZombiePeerAsync(rig.Guest, new(guest.Slot, 34, 2, null, 0), default);
            await rig.Round.HandleScriptZombiePeerAsync(rig.Guest, new(guest.Slot, 34, 1, 207, weapon), default);
            Assert.Equal(2, peers.To(rig.Host).Length);
            await rig.Round.HandleScriptZombiePeerAsync(rig.Guest, new(guest.Slot, 34, 1, 205, weapon), default);
            generation = guest.Life.Generation;
            await rig.Round.HandleScriptZombiePeerAsync(rig.Guest, new(guest.Slot, 34, 2, null, 0), default);
            Assert.Equal(4, peers.To(rig.Host).Length);
            Assert.Equal(generation, guest.Life.Generation);
            Assert.Equal(RoomPhase.Playing, room.Phase);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }
}
