using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Sessions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class NativeMatchRoundTests
{
    /// <summary>Only a delivered roll can publish its own actor's actual form to the other match link once.</summary>
    [Fact]
    public async Task ZombieOriginConfirmationRequiresTheSelectedActorAndNativeWeightedKind()
    {
        await using var rig = new Rig("kr-1256", 20, fallback: true);
        rig.Round.ZombieOriginRolls = new Recorder<RoundZombieOriginPrototypeRoll>();
        rig.Round.ZombieOriginRules = new(() => new(1, 0, 0, true));
        await rig.StartAsync(43);
        foreach (var member in new[] { rig.Host, rig.Guest })
        {
            await rig.LoadAsync(member);
            await rig.Round.HandleMatchConnectionReportedAsync(member, new(false), default);
        }
        var room = rig.Room;
        lock (room.Gate) { room.MarkPlaying(); rig.Round.StartZombieOrigin(room); }
        var selected = Assert.Single(room.Seats, s => room.ZombieOrigin!.For(s) is not null);
        var other = Assert.Single(room.Seats, s => selected != s);
        var owner = RoomRegistry.SceneEntity(selected.Slot);
        var stranger = RoomRegistry.SceneEntity(other.Slot);
        Task Report(ISession session, int actor, int? kind)
        {
            var body = BitConverter.GetBytes(actor).Concat(kind.HasValue
                ? new byte[] { 0x3A }.Concat(BitConverter.GetBytes(kind.Value)) : new byte[] { 0xBA }).ToArray();
            return rig.Round.HandleFeedAsync(session, new RoundFeedSent(34277, body)
                { OnRelay = true, ReportedActors = [actor], ZombieOriginPrototype = (actor, kind) }, default).AsTask();
        }
        await Report(selected.Game, owner, 201);
        await rig.Round.HandleZombieOriginPrototypeAsync(selected.Game, new(202), default);
        await Report(other.Game, stranger, 201);
        await Report(other.Game, owner, 201);
        await Report(new Session(selected.Game.Id, "kr-1256"), owner, 201);
        await Report(selected.Game, stranger, 201);
        await Report(selected.Game, owner, null);
        await Report(selected.Game, owner, 203);
        Assert.Empty(rig.Feed.Sent);
        var participant = room.ZombieOrigin!.MemberFor(selected)!;
        Assert.Equal(ZombieOriginRole.Fighter, participant.Role);
        var lobbyTeam = selected.Team;
        await Task.WhenAll(Report(selected.Game, owner, 201), Report(selected.Game, owner, 201));
        Assert.Equal(ZombieOriginRole.Prototype, participant.Role);
        Assert.Equal(201, participant.Kind);
        Assert.Equal(34, (int)selected.Team);
        var sent = Assert.Single(rig.Feed.Sent);
        Assert.Equal(other.Game.Id, sent.Session);
        Assert.Equal((ushort)34277, sent.Message.Code);
        Assert.Equal(BitConverter.GetBytes(owner).Concat(Convert.FromHexString("3AC9000000")).ToArray(), sent.Message.Payload.ToArray());
        room.ResetToWaiting();
        Assert.Equal(lobbyTeam, selected.Team);
    }

    /// <summary>A reported character kind cannot nominate another fighter, an unselected fighter, or a second draw.</summary>
    [Fact]
    public async Task ZombieOriginDrawIsPrivateAndCannotBeClaimedTwice()
    {
        await using var rig = new Rig("kr-1256", 20);
        var rolls = new Recorder<RoundZombieOriginPrototypeRoll>();
        rig.Round.ZombieOriginRolls = rolls;
        await rig.StartAsync(43);
        var room = rig.Room;
        Task Report(ISession s, int kind) => rig.Round.HandleZombieOriginPrototypeAsync(s, new(kind), default).AsTask();
        await Report(rig.Host, 203);
        await Report(rig.Guest, 201);
        Assert.Empty(rolls.Sent);
        lock (room.Gate) { room.MarkPlaying(); rig.Round.StartZombieOrigin(room); }
        var selected = Assert.Single(room.Seats, seat => room.ZombieOrigin!.For(seat) is not null);
        var other = Assert.Single(room.Seats, s => s != selected);
        await Report(other.Game, 201);
        await Report(new Session(selected.Game.Id, "kr-1256"), 203);
        await Report(new Session(9194, "kr-1256"), 212);
        Assert.Empty(rolls.Sent);
        var draw = room.ZombieOrigin!.For(selected)!;
        rolls.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => Report(selected.Game, 201));
        Assert.False(draw.Delivered);
        rolls.Fail = false;
        await Task.WhenAll(Report(selected.Game, 0), Report(other.Game, -1), Report(selected.Game, 212));
        var received = Assert.Single(rolls.Sent);
        Assert.Equal(selected.Game.Id, received.Session);
        Assert.Equal(draw.Roll, received.Message.Roll);
        Assert.InRange(received.Message.Roll, 0, 9999);
        Assert.True(draw.Delivered);
        lock (room.Gate) room.ResetToWaiting();
        await Report(selected.Game, 202);
        Assert.Null(room.ZombieOrigin);
        Assert.Single(rolls.Sent);
        lock (room.Gate) { room.MarkStarting(); room.MarkPlaying(); rig.Round.StartZombieOrigin(room); }
        await Task.WhenAll(Report(rig.Host, 1), Report(rig.Guest, 2));
        Assert.Equal(2, rolls.Sent.Count);
    }

    /// <summary>Non-G rounds, settled rounds and closed connections never turn into prototype recipients.</summary>
    [Theory]
    [InlineData(8)]
    [InlineData(28)]
    [InlineData(32)]
    [InlineData(43)]
    public async Task ZombieOriginDrawRejectsOtherModesAndEndedRounds(int mode)
    {
        await using var rig = new Rig("kr-1256", 20);
        var rolls = new Recorder<RoundZombieOriginPrototypeRoll>();
        rig.Round.ZombieOriginRolls = rolls;
        await rig.StartAsync(mode);
        var room = rig.Room;
        lock (room.Gate) { room.MarkPlaying(); rig.Round.StartZombieOrigin(room); }
        if (mode == 43)
        {
            var seat = Assert.Single(room.Seats, s => room.ZombieOrigin!.For(s) is not null);
            seat.Life.Bubble();
            await rig.Round.HandleZombieOriginPrototypeAsync(seat.Game, new(1), default);
            Assert.Empty(rolls.Sent);
            lock (room.Gate) room.MarkResult();
        }
        else Assert.Null(room.ZombieOrigin);
        foreach (var s in room.Seats) await rig.Round.HandleZombieOriginPrototypeAsync(s.Game, new(1), default);
        Assert.Empty(rolls.Sent);
    }

    /// <summary>A large configured quota always leaves a human, and a disconnected or replaced fighter cannot inherit a grant.</summary>
    [Fact]
    public async Task ZombieOriginQuotaLeavesAHumanAndBindsTheOriginalSeat()
    {
        await using var rig = new Rig("kr-1256", 20);
        var rolls = new Recorder<RoundZombieOriginPrototypeRoll>();
        rig.Round.ZombieOriginRolls = rolls;
        await rig.StartAsync(43);
        var room = rig.Room;
        var original = room.Seats.ToArray();
        var many = new ZombieOriginRoundState(room.StartGeneration, [.. original], 15);
        var selected = Assert.Single(original, s => many.For(s) is not null);
        Assert.Null(many.For(new RoomSeat(selected.Slot, selected.Game, selected.Key, selected.UserNum, selected.Nickname)));
        var none = new ZombieOriginRoundState(room.StartGeneration, [.. original], 0);
        Assert.All(original, s => Assert.Null(none.For(s)));
        var solo = new ZombieOriginRoundState(room.StartGeneration, [selected], 1);
        Assert.Null(solo.For(selected));
        lock (room.Gate) { room.MarkPlaying(); room.ZombieOrigin = many; }
        await selected.Game.Connection.CloseAsync("test disconnect");
        await rig.Round.HandleZombieOriginPrototypeAsync(selected.Game, new(1), default);
        Assert.Empty(rolls.Sent);
    }
}
