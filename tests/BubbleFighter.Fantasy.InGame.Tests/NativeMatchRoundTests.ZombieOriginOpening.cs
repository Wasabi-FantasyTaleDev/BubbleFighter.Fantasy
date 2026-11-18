using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Sessions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class NativeMatchRoundTests
{
    /// <summary>A completed native countdown can open G without a prior death. A room missing a reported actor waits for the native request.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ZombieOriginOpeningTargetsOneBuiltActorAfterBothStartFrames(bool bothActorsReady)
    {
        await using var rig = new Rig("kr-1256", 20, countdownMs: 10);
        var rolls = new Recorder<RoundZombieOriginPrototypeRoll>();
        rig.Round.ZombieOriginRolls = rolls;
        await rig.StartAsync(43);
        var room = rig.Room;
        foreach (var game in new[] { rig.Host, rig.Guest })
        {
            await rig.LoadAsync(game);
            var slot = room.FindByGame(game.Id)!.Slot;
            if (bothActorsReady || game == rig.Host)
                await rig.Round.HandleObjectAddRequestedAsync(game, new(RoomRegistry.SceneEntity(slot)), default);
            var relay = new Session(100 + game.Id, "kr-1256");
            var binding = await rig.Registry.BindCapabilityAsync(relay, new(rig.Offers.Sent[^1].Message.Ticket), default);
            rig.Registry.MarkInitialized(relay, binding);
            await rig.Round.HandleMatchConnectionReportedAsync(game, new(true), default);
        }
        await room.CountdownTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RoomPhase.Playing, room.Phase);
        Assert.Equal(2, rig.Started.Sent.Count);
        Assert.All(rig.Started.Sent, start => Assert.Equal(180_000, start.TimeLimitMs));
        var selected = Assert.Single(room.Seats, s => room.ZombieOrigin!.For(s) is not null);
        if (bothActorsReady)
            Assert.Equal(selected.Game.Id, Assert.Single(rolls.Sent).Session);
        else Assert.Empty(rolls.Sent);
        foreach (var s in room.Seats)
            await rig.Round.HandleZombieOriginPrototypeAsync(s.Game, new(0), default);
        Assert.Equal(selected.Game.Id, Assert.Single(rolls.Sent).Session);
        Assert.Equal(room.ZombieOrigin!.For(selected)!.Roll, rolls.Sent[0].Message.Roll);
    }
}
