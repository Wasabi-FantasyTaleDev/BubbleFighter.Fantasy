using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Tests.Shared;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    /// <summary>A relay attachment does not grant the right to report a game-side taunt.</summary>
    [Fact]
    public async Task BoundRelayCannotShortenAPlayersRespawnWait()
    {
        var rig = new Rig();
        try
        {
            await StartWithPendingRespawnAsync(rig);
            var relay = new RecordingSession(33);
            var room = rig.Registry.Find(rig.RoomId)!;
            rig.Registry.BindRelay(relay, room.FindByGame(rig.Host.Id)!.Key);

            await rig.Round.HandleTauntReportedAsync(relay, new(1100000), default);
            Assert.Empty(rig.WaitChanged.To(rig.Guest));

            await rig.Round.HandleTauntReportedAsync(rig.Host, new(1100000), default);
            Assert.Equal(new RoundRespawnWaitChanged(1100000, -2000), Assert.Single(rig.WaitChanged.To(rig.Guest)));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Late reports cannot change a previous round's pending wait after play has ended.</summary>
    [Theory]
    [InlineData(RoomPhase.Waiting)]
    [InlineData(RoomPhase.Starting)]
    [InlineData(RoomPhase.Result)]
    [InlineData(RoomPhase.Closed)]
    public async Task TauntRequiresAnActivelyPlayingRoom(RoomPhase phase)
    {
        var rig = new Rig();
        try
        {
            await StartWithPendingRespawnAsync(rig);
            var room = rig.Registry.Find(rig.RoomId)!;
            lock (room.Gate)
            {
                switch (phase)
                {
                    case RoomPhase.Waiting: room.ResetToWaiting(); break;
                    case RoomPhase.Starting: room.ResetToWaiting(); room.MarkStarting(); break;
                    case RoomPhase.Result: room.MarkResult(); break;
                    case RoomPhase.Closed:
                        room.RemoveSession(rig.Host.Id);
                        room.RemoveSession(rig.Guest.Id);
                        break;
                }
                Assert.Equal(phase, room.Phase);
            }

            await rig.Round.HandleTauntReportedAsync(rig.Host, new(1100000), default);
            Assert.Empty(rig.WaitChanged.To(rig.Guest));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A game seat in a different room cannot reach the victim's pending wait.</summary>
    [Fact]
    public async Task TauntCannotReachAPlayerInAnotherRoom()
    {
        var rig = new Rig();
        try
        {
            await StartWithPendingRespawnAsync(rig);
            var outsider = new RecordingSession(44);
            outsider.SetLobbyPlayer(44, "outsider");
            await rig.Rooms.HandleCreateAsync(outsider, new("other room", "", 4, "map"), default);

            await rig.Round.HandleTauntReportedAsync(outsider, new(1100000), default);
            Assert.Empty(rig.WaitChanged.To(rig.Guest));
            Assert.Empty(rig.WaitChanged.To(outsider));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    private static async Task StartWithPendingRespawnAsync(Rig rig)
    {
        await rig.StartAsync();
        await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
        await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
        await rig.WaitStartedAsync(rig.Guest);
        await rig.Round.HandleDeathReportedAsync(rig.Guest, new(1100000, 20000, ServerRevives: true), default);
    }
}
