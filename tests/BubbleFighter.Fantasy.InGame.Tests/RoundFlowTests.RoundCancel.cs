using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Handlers;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Tests.Shared;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    /// <summary>A dropped connection while the round loads or plays cancels it and sends the others to the plaza.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisconnectDuringRoundCancelsItAndReturnsEveryoneToThePlaza(bool playing)
    {
        var rig = new Rig();
        try
        {
            await rig.StartAsync();
            var room = rig.Registry.Find(rig.RoomId)!;
            if (playing)
            {
                await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
                await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
                await rig.WaitStartedAsync(rig.Host);
            }
            await DropAsync(rig, rig.Host);
            var left = await WaitLeftAsync(rig, rig.Guest);
            await room.RoundCancelTask.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(rig.RoomId, left.RoomId);
            Assert.Null(rig.Registry.TryGet(rig.Guest.Id));
            Assert.Equal(RoomPhase.Closed, room.Phase);
            Assert.Empty(rig.Ended.To(rig.Guest));
            if (!playing)
                Assert.Empty(rig.Countdown.To(rig.Guest));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>With the cancel switched off a dropped player leaves alone and the round plays on.</summary>
    [Fact]
    public async Task DisconnectWithoutRoundCancelLeavesTheOthersPlaying()
    {
        var rig = new Rig(configure: options => options.CancelRoundOnDisconnect = false);
        try
        {
            var third = (await StartPlayingAsync(rig))!;
            var room = rig.Registry.Find(rig.RoomId)!;
            await DropAsync(rig, third);
            Assert.Equal(RoomPhase.Playing, room.Phase);
            Assert.Empty(rig.Left.To(rig.Host));
            Assert.NotNull(rig.Registry.TryGet(rig.Guest.Id));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A player who leaves the room themselves never cancels the round; the others play on.</summary>
    [Fact]
    public async Task VoluntaryLeaveDuringRoundKeepsTheOthersPlaying()
    {
        var rig = new Rig();
        try
        {
            var third = (await StartPlayingAsync(rig))!;
            var room = rig.Registry.Find(rig.RoomId)!;
            await rig.Rooms.HandleLeaveAsync(third, new(), default);
            Assert.Single(rig.Left.To(third));
            Assert.Equal(RoomPhase.Playing, room.Phase);
            Assert.False(room.RoundCancelled);
            Assert.Empty(rig.Left.To(rig.Host));
            Assert.Empty(rig.Left.To(rig.Guest));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A round that never finishes loading is cancelled at the load timeout; one that loads in time plays.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadTimeoutCancelsOnlyARoundThatNeverLoads(bool loads)
    {
        var rig = new Rig(configure: options => options.LoadTimeoutMs = 150);
        try
        {
            await rig.StartAsync();
            var room = rig.Registry.Find(rig.RoomId)!;
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            if (loads)
            {
                await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
                await rig.WaitStartedAsync(rig.Host);
            }
            await room.LoadTimeoutTask.WaitAsync(TimeSpan.FromSeconds(2));
            if (loads)
            {
                Assert.Equal(RoomPhase.Playing, room.Phase);
                Assert.Empty(rig.Left.To(rig.Host));
            }
            else
            {
                await WaitLeftAsync(rig, rig.Host);
                await WaitLeftAsync(rig, rig.Guest);
                await room.RoundCancelTask.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal(RoomPhase.Closed, room.Phase);
                Assert.Empty(rig.Countdown.To(rig.Host));
            }
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>
    /// The zombies win once every present player is infected: a human whose connection already closed neither
    /// keeps the round going nor counts as a survivor.
    /// </summary>
    [Fact]
    public async Task ZombieBattleIgnoresAHumanWhoseConnectionClosed()
    {
        var (rig, context) = ZombieScoringRig();
        try
        {
            var third = (await StartZombiePvpAsync(rig, third: true))!;
            var room = rig.Registry.Find(rig.RoomId)!;
            await rig.Rooms.HandleTeamChangeAsync(rig.Guest, new(), default);
            // The close event is still queued: the seat is there, its player is not.
            await third.Connection.CloseAsync("test");
            await rig.Rooms.HandleTeamChangeAsync(rig.Host, new(), default);
            Assert.Equal(RoomFlow.ZombiePvpZombieTeam, room.DecidedWinnerTeam);
            Assert.Equal(RoomPhase.Result, room.Phase);
        }
        finally { await rig.Registry.StopAllAsync(); await context.UnwindAsync(); }
    }

    /// <summary>The optional start rules refuse a master's start below the player minimum or with unbalanced teams.</summary>
    [Theory]
    [InlineData("min-refused")]
    [InlineData("min-met")]
    [InlineData("unbalanced")]
    [InlineData("balanced")]
    public async Task StartRulesRefuseTooFewPlayersAndUnbalancedTeams(string rule)
    {
        var rig = new Rig(configure: options =>
        {
            options.StartMinPlayers = rule is "min-refused" ? 3 : rule is "min-met" ? 2 : 0;
            options.StartRequiresTeamBalance = rule is "unbalanced" or "balanced";
        });
        var third = new RecordingSession(33);
        third.SetLobbyPlayer(33, "third");
        var fourth = new RecordingSession(44);
        fourth.SetLobbyPlayer(44, "fourth");
        try
        {
            // Mode 1 places its seats on red or blue (PlayRoom.RequiresSpawnTeams).
            await rig.Rooms.HandleCreateAsync(rig.Host, new RoomCreateRequested("room", "", 1, "map"), default);
            var roomId = rig.Entered.To(rig.Host)[0].Room.RoomId;
            var room = rig.Registry.Find(roomId)!;
            var members = rule.StartsWith("min", StringComparison.Ordinal) ? new ISession[] { rig.Guest }
                : [rig.Guest, third, fourth];
            foreach (var member in members)
            {
                await rig.Rooms.HandleJoinAsync(member, new RoomJoinRequested(roomId, ""), default);
                await rig.Rooms.HandleReadyAsync(member, new RoomReadyRequested(true), default);
            }
            if (rule is "unbalanced" or "balanced")
                lock (room.Gate)
                    foreach (var seat in room.Seats)
                        seat.Team = rule == "balanced" && seat.Slot % 2 == 1 ? (byte)2 : (byte)1;
            await rig.Rooms.HandleStartAsync(rig.Host, new RoomStartRequested("map"), default);
            var refused = rule is "min-refused" or "unbalanced";
            Assert.Equal(refused ? 0 : 1, rig.Starting.To(rig.Guest).Length);
            Assert.Equal(refused ? RoomPhase.Waiting : RoomPhase.Starting, room.Phase);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A cancelled round cannot evict a player who already entered a different room.</summary>
    [Fact]
    public async Task DelayedCancelledReturnDoesNotLeaveANewerRoom()
    {
        var rig = new Rig();
        try
        {
            await rig.StartAsync();
            var oldRoom = rig.Registry.Find(rig.RoomId)!;
            var generation = oldRoom.StartGeneration;
            oldRoom.RoundCancelled = true;
            await rig.Rooms.HandleLeaveAsync(rig.Guest, new(), default);
            await rig.Rooms.HandleCreateAsync(rig.Guest, new RoomCreateRequested("new", "", 1, "map"), default);
            var newRoom = rig.Registry.TryGet(rig.Guest.Id)!;
            var priorLeaves = rig.Left.To(rig.Guest).Length;

            Assert.False(await rig.Rooms.ReturnCancelledPlayerAsync(rig.Guest, oldRoom, generation, default));

            Assert.Same(newRoom, rig.Registry.TryGet(rig.Guest.Id));
            Assert.Equal(priorLeaves, rig.Left.To(rig.Guest).Length);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A cancelled start cannot evict a player after the same room starts its next round.</summary>
    [Fact]
    public async Task DelayedCancelledReturnDoesNotLeaveANewerStart()
    {
        var rig = new Rig(configure: options => options.LoadTimeoutMs = 30_000);
        try
        {
            await rig.StartAsync();
            var room = rig.Registry.Find(rig.RoomId)!;
            var generation = room.StartGeneration;
            var oldWatch = room.LoadTimeoutTask;
            room.RoundCancelled = true;
            room.ResetToWaiting();
            await rig.Rooms.HandleReadyAsync(rig.Guest, new(true), default);
            await rig.Rooms.HandleStartAsync(rig.Host, new("map"), default);

            await oldWatch.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(room.StartGeneration > generation);
            Assert.False(await rig.Rooms.ReturnCancelledPlayerAsync(rig.Guest, room, generation, default));
            Assert.Same(room, rig.Registry.TryGet(rig.Guest.Id));
            Assert.Empty(rig.Left.To(rig.Guest));
            Assert.False(room.RoundCancelled);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A stopped or emptied room releases its long loading delay without waiting for its deadline.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadWatchStopsWithItsRoom(bool emptyRoom)
    {
        var rig = new Rig(configure: options => options.LoadTimeoutMs = 30_000);
        try
        {
            await rig.StartAsync();
            var room = rig.Registry.Find(rig.RoomId)!;
            var watch = room.LoadTimeoutTask;
            if (emptyRoom)
            {
                await rig.Rooms.HandleLeaveAsync(rig.Guest, new(), default);
                await rig.Rooms.HandleLeaveAsync(rig.Host, new(), default);
            }
            else
                await rig.Registry.StopAllAsync().WaitAsync(TimeSpan.FromSeconds(2));

            await watch.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(room.RoundCancelled);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    private static async Task<RecordingSession?> StartPlayingAsync(Rig rig)
    {
        var third = new RecordingSession(33);
        third.SetLobbyPlayer(33, "third");
        await rig.StartAsync(third);
        foreach (var member in new ISession[] { rig.Host, rig.Guest, third })
            await rig.Round.HandleLoadReportedAsync(member, new(), default);
        await rig.WaitStartedAsync(rig.Host);
        return third;
    }

    private static async Task<RoomLeft> WaitLeftAsync(Rig rig, ISession player)
    {
        for (var attempt = 0; attempt < 200 && rig.Left.To(player).Length == 0; attempt++)
            await Task.Delay(10);
        return Assert.Single(rig.Left.To(player));
    }

    private static async Task DropAsync(Rig rig, ISession player)
    {
        await player.Connection.CloseAsync("test");
        rig.Rooms.OnConnectionClosed(new ConnectionClosedEvent(player.Connection, "test"));
        for (var attempt = 0; attempt < 200 && rig.Registry.TryGet(player.Id) is not null; attempt++)
            await Task.Delay(10);
        Assert.Null(rig.Registry.TryGet(player.Id));
    }
}
