using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Sessions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class NativeMatchRoundTests
{
    /// <summary>A revived infected fighter remains an opponent. Neither the old pop nor a replacement in its slot proves a Perfect Win.</summary>
    [Fact]
    public async Task ZombieOriginPreviousLifeAndReplacementDoNotCompletePerfectWin()
    {
        await using var rig = new Rig("kr-1256", 20);
        await rig.StartAsync(43);
        var room = rig.Room;
        var extra = new Session(33, "kr-1256");
        room.Add(extra, default, 33, "third");
        room.MarkPlaying();
        var state = new ZombieOriginRoundState(room.StartGeneration, [.. room.Seats], 1);
        var prototype = room.Seats.Single(s => state.For(s) is not null);
        state.For(prototype)!.Confirmed = true;
        state.MemberFor(prototype)!.Confirm(prototype, ZombieOriginRole.Prototype, 201);
        var infected = room.Seats.First(s => s != prototype);
        state.MemberFor(infected)!.Confirm(infected, ZombieOriginRole.ConvertedFighter, 211);
        state.MemberFor(infected)!.PoppedLife = infected.Life.Generation;
        infected.Life.Revive();
        state.MemberFor(prototype)!.PoppedLife = prototype.Life.Generation;
        Assert.False(state.Decide(room, false));
        room.RemoveSession(infected.Game.Id);
        var replacement = room.Add(new Session(44, "kr-1256"), default, 44, "replacement");
        Assert.Equal(infected.Slot, replacement.Slot);
        Assert.False(state.Decide(room, false));
        Assert.True(state.Decide(room, true));
        Assert.Equal(ZombieOriginOutcome.FightersSurvivedTimeout, state.Outcome);
        Assert.DoesNotContain(replacement.Slot, room.DecidedWinnerSlots!);
    }

    /// <summary>A human win requires positive evidence from the live round; an ordinary stage report or cleanup alone cannot end it.</summary>
    [Theory]
    [InlineData(ZombieOriginOutcome.FightersSurvivedTimeout)]
    [InlineData(ZombieOriginOutcome.FightersPerfectWin)]
    [InlineData(ZombieOriginOutcome.ZombiesConvertedAllFighters)]
    public async Task ZombieOriginRoundEndsOnItsActualObjective(ZombieOriginOutcome expected)
    {
        var ended = new Recorder<RoundEnded>();
        var resets = new Recorder<RoundPlayerRespawned>();
        await using var rig = new Rig("kr-1256", 20, fallback: true, ended: ended, respawned: resets);
        rig.Round.ZombieOriginRolls = new Recorder<RoundZombieOriginPrototypeRoll>();
        rig.Round.ZombieOriginRules = new(() => new(1, 0, 0, true));
        await rig.StartAsync(43);
        var room = rig.Room;
        foreach (var seat in room.Seats.ToArray())
        {
            await rig.LoadAsync(seat.Game);
            await rig.Round.HandleMatchConnectionReportedAsync(seat.Game, new(false), default);
        }
        lock (room.Gate) { room.MarkPlaying(); rig.Round.StartZombieOrigin(room); }
        var state = room.ZombieOrigin!;
        var prototype = room.Seats.Single(s => state.For(s) is not null);
        var human = room.Seats.Single(s => s != prototype);
        var target = RoomRegistry.SceneEntity(prototype.Slot);
        var attacker = RoomRegistry.SceneEntity(human.Slot);
        await rig.Round.HandleZombieOriginPrototypeAsync(prototype.Game, new(0), default);
        var body = BitConverter.GetBytes(target).Concat(Convert.FromHexString("3AC9000000")).ToArray();
        await rig.Round.HandleFeedAsync(prototype.Game, new(34277, body)
            { OnRelay = true, ReportedActors = [target], ZombieOriginPrototype = (target, 201) }, default);
        Assert.Equal(ZombieOriginRole.Prototype, state.MemberFor(prototype)!.Role);
        await rig.Round.HandleEndRequestedAsync(human.Game, new() { Reason = RoundEndReason.WavesCleared }, default);
        await rig.Round.HandleLifeReportedAsync(prototype.Game, new(RoundLifeEvent.DeathCleaned, target), default);
        Assert.Equal(RoomPhase.Playing, room.Phase);
        if (expected == ZombieOriginOutcome.FightersPerfectWin)
        {
            await rig.Round.HandleLifeReportedAsync(prototype.Game, new(RoundLifeEvent.Bubbled, target) { Initiator = attacker }, default);
            await rig.Round.HandleLifeReportedAsync(human.Game, new(RoundLifeEvent.Finished, target) { Initiator = attacker }, default);
        }
        else if (expected == ZombieOriginOutcome.ZombiesConvertedAllFighters)
        {
            await rig.Round.HandleLifeReportedAsync(human.Game, new(RoundLifeEvent.Died, attacker), default);
            await rig.Round.HandleLifeReportedAsync(human.Game, new(RoundLifeEvent.DeathCleaned, attacker), default);
            byte[] request = [.. BitConverter.GetBytes(attacker), 0xBA, 0x3A, 211, 0, 0, 0, 0, 0, 0, 0];
            await rig.Round.HandleSpawnAcceptedAsync(human.Game, new(attacker, 0)
            {
                Request = request, TaggedReset = new(null, 211),
                Rules = new(RoundRespawnAudience.Requester, RoundRespawnCompletion.MatchConfirmation, RoundRespawnAdmission.CleanedDeath),
            }, default);
            Assert.Single(resets.Sent);
            Assert.Equal(RoomPhase.Playing, room.Phase);
            byte[] confirmation = [.. request, 0];
            await rig.Round.HandleFeedAsync(human.Game, new(32926, confirmation)
                { OnRelay = true, ReportedActors = [attacker], TaggedReset = new(null, 211) }, default);
        }
        else await rig.Round.EndRoundNowAsync(room, default);
        Assert.Equal(RoomPhase.Result, room.Phase);
        Assert.Equal(expected, state.Outcome);
        var converted = expected == ZombieOriginOutcome.ZombiesConvertedAllFighters;
        Assert.True(room.DecidedWinnerSlots!.SetEquals(converted ? [human.Slot, prototype.Slot] : [human.Slot]));
        Assert.Equal(2, ended.Sent.Count);
        Assert.All(ended.Sent, delivery => Assert.Equal(state.Outcome, delivery.Message.ZombieOriginOutcome));
        Assert.Equal(RoundOutcome.Won, ended.Sent.Single(d => d.Session == human.Game.Id).Message.Outcome);
        Assert.Equal(converted ? RoundOutcome.Won : RoundOutcome.Lost, ended.Sent.Single(d => d.Session == prototype.Game.Id).Message.Outcome);
    }
}
