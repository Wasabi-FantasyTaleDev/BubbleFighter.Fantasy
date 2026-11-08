using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.InGame.Handlers;
using BubbleFighter.Fantasy.InGame.Sessions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed class GameSessionStateTests
{
    [Fact]
    public async Task OnlyExactPlayingOrdinaryMembershipAllowsEarlyDeparture()
    {
        var rig = new RoomFlowTests.Rig();
        using var owner = new CancellationTokenSource();
        var reader = new GameSessionStateReader(rig.Registry, owner.Token, () => true);
        var player = RoomFlowTests.Rig.Player(11, "host");
        try
        {
            Assert.Null(reader.Describe(player));
            await rig.CreateAsync(player);
            Assert.Equal(new("room_waiting", false, false, false, 4), reader.Describe(player));
            Assert.Null(reader.Describe(RoomFlowTests.Rig.Player(11, "host")));
            await rig.Flow.HandleStartAsync(player, new("rel_Village07_Easy02"), default);
            Assert.Equal(new("room_loading", false, false, false, 4), reader.Describe(player));
            var room = rig.Registry.TryGet(player.Id)!;
            lock (room.Gate) room.CountdownGeneration = room.StartGeneration;
            Assert.Equal(new("room_countdown", false, false, false, 4), reader.Describe(player));
            lock (room.Gate) room.MarkPlaying();
            Assert.Equal(new("match", true, true, false, 4), reader.Describe(player));
            Assert.Equal(new("match", true, false, false, 4), new GameSessionStateReader(rig.Registry, default, () => false).Describe(player));
            lock (room.Gate) room.RoundCancelled = true;
            Assert.Equal(GameSessionState.Unavailable, reader.Describe(player));
            owner.Cancel();
            Assert.Null(reader.Describe(player));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    [Fact]
    public async Task TutorialAndTrainingNeverBecomeOrdinaryMatchDeparture()
    {
        var rig = new RoomFlowTests.Rig();
        var reader = new GameSessionStateReader(rig.Registry, default, () => true);
        var player = RoomFlowTests.Rig.Player(11, "host");
        try
        {
            var (room, _) = rig.Registry.CreateTrainingRoom(player, RoomWireSupport.SampleRules(), 11, "host");
            Assert.Equal("training", reader.Describe(player)!.State);
            Assert.False(reader.Describe(player)!.CanLeaveMatch);
            room.Tutorial = new(4, "test", 1, [], 0, 0, 0, false);
            Assert.Equal(new("tutorial", false, false, true, room.ModeId), reader.Describe(player));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }
}
