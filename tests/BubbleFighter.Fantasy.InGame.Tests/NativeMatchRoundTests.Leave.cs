using BubbleFighter.Fantasy.Contracts.Match;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class NativeMatchRoundTests
{
    /// <summary>The current relay leaves its game seat and receives the ordinary departure on the game connection.</summary>
    [Theory]
    [InlineData("kr-812", false)]
    [InlineData("cn-717", false)]
    [InlineData("id-1809", false)]
    [InlineData("cn-717", true)]
    public async Task CurrentRelayLeavesThroughItsGameScene(string build, bool waiting)
    {
        await using var rig = new Rig(build, 20);
        await rig.StartAsync();
        var room = rig.Room;
        await rig.LoadAsync(rig.Guest);
        var relay = new Session(101, build);
        await rig.Registry.BindCapabilityAsync(relay, new(rig.Offers.Sent[^1].Message.Ticket), default);
        if (waiting)
            await room.Executor.ExecuteAsync(_ => { room.ResetToWaiting(); return ValueTask.CompletedTask; });

        await rig.Rooms.HandleMatchLeaveAsync(relay, new(), default);

        Assert.Null(rig.Registry.TryGet(rig.Guest.Id));
        Assert.Null(rig.Registry.TryGet(relay.Id));
        Assert.Same(room, rig.Registry.TryGet(rig.Host.Id));
        Assert.Equal(rig.Guest.Id, Assert.Single(rig.Departures.Sent).Session);
        await rig.Rooms.HandleMatchLeaveAsync(relay, new(), default);
        Assert.Single(rig.Departures.Sent);
    }

    /// <summary>An unbound session, including one with a copied numeric id, cannot remove a game seat.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnboundOrCopiedRelayIdentityCannotLeave(bool copyId)
    {
        await using var rig = new Rig("cn-717", 20);
        await rig.StartAsync();
        await rig.LoadAsync(rig.Guest);
        var relay = new Session(101, "cn-717");
        await rig.Registry.BindCapabilityAsync(relay, new(rig.Offers.Sent[^1].Message.Ticket), default);

        await rig.Rooms.HandleMatchLeaveAsync(new Session(copyId ? 101 : 102, "cn-717"), new(), default);

        Assert.Same(rig.Room, rig.Registry.TryGet(rig.Guest.Id));
        Assert.Same(relay, rig.Room.Seats[1].Relay);
        Assert.Empty(rig.Departures.Sent);
    }

    /// <summary>Replacement or a new round queued before departure invalidates the captured relay ownership.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedRelayDepartureRechecksItsSeatAndRound(bool newRound)
    {
        await using var rig = new Rig("cn-717", 20);
        await rig.StartAsync();
        var room = rig.Room;
        await rig.LoadAsync(rig.Guest);
        var key = rig.Offers.Sent[^1].Message.Ticket;
        var relay = new Session(101, "cn-717");
        await rig.Registry.BindCapabilityAsync(relay, new(key), default);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocking = room.Executor.ExecuteAsync(async _ =>
        {
            entered.SetResult();
            await release.Task;
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var replacement = new Session(102, "cn-717");
        Task change = newRound
            ? room.Executor.ExecuteAsync(_ => { room.ResetToWaiting(); room.MarkStarting(); return ValueTask.CompletedTask; })
            : rig.Registry.BindCapabilityAsync(replacement, new(key), default).AsTask();
        var leave = rig.Rooms.HandleMatchLeaveAsync(relay, new(), default).AsTask();
        try
        {
            release.SetResult();
            await Task.WhenAll(blocking, change, leave).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { release.TrySetResult(); }

        Assert.Same(room, rig.Registry.TryGet(rig.Guest.Id));
        Assert.Empty(rig.Departures.Sent);
        if (!newRound)
        {
            Assert.Same(replacement, room.Seats[1].Relay);
            await rig.Rooms.HandleMatchLeaveAsync(relay, new(), default);
            Assert.Same(room, rig.Registry.TryGet(rig.Guest.Id));
        }
    }
}
