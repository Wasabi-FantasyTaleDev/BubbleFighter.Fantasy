using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Sessions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    /// <summary>A retained terminal-publication cleanup blocks map entry even when the next match uses another mode.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalZonePendingCleanupPreventsStartUntilCompleted(bool switchMode)
    {
        var rig = new Rig();
        rig.Round.FinalZoneEliminated = new Sink<RoundFinalZonePlayerEliminated>();
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var room = await StartFinalZoneModeAsync(rig, GameModeId.Area32);
            await room.CountdownTask.WaitAsync(TimeSpan.FromSeconds(5));
            lock (room.Gate)
                room.FinalZoneCleanupTask = room.FinalZone!.CleanupTask = cleanup.Task;
            await AssertPendingCleanupBlocksStartAsync(rig, room, "map", cleanup, switchMode);
            Assert.Same(cleanup.Task, room.FinalZoneCleanupTask);
        }
        finally
        {
            cleanup.TrySetResult();
            await rig.Registry.StopAllAsync();
        }
    }

    /// <summary>A retained BnB connection cleanup blocks the next map entry regardless of its selected mode.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BnBPendingCleanupPreventsStartUntilCompleted(bool switchMode)
    {
        await using var setup = new BnBTestRig();
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var room = await setup.StartAsync();
            // StartAsync drains the start operation; CountdownTask now tracks the round deadline.
            lock (room.Gate)
                room.BnBCleanupTask = room.BnBRound!.CleanupTask = cleanup.Task;
            await AssertPendingCleanupBlocksStartAsync(setup.Rig, room, "test", cleanup, switchMode);
            Assert.Same(cleanup.Task, room.BnBCleanupTask);
        }
        finally { cleanup.TrySetResult(); }
    }

    private static async Task AssertPendingCleanupBlocksStartAsync(Rig rig, PlayRoom room, string map,
        TaskCompletionSource cleanup, bool switchMode)
    {
        var generation = room.StartGeneration;
        var recipients = new[] { rig.Host, rig.Guest }.Select(member => new
        {
            Member = member,
            Starting = rig.Starting.To(member).Length,
            Countdown = rig.Countdown.To(member).Length,
            Started = rig.Started.To(member).Length,
        }).ToArray();
        room.ResetToWaiting();
        if (switchMode)
            lock (room.Gate) room.ModeId = 4;
        await rig.Rooms.HandleReadyAsync(rig.Guest, new(true), default);
        Assert.True(room.CanStart);
        rig.Rooms.ValidateRoundMap = rig.Round.ValidateModeMap;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rig.Rooms.HandleStartAsync(rig.Host, new RoomStartRequested(map), default).AsTask());

        Assert.Equal(RoomPhase.Waiting, room.Phase);
        Assert.Equal(generation, room.StartGeneration);
        foreach (var recipient in recipients)
        {
            Assert.Equal(recipient.Starting, rig.Starting.To(recipient.Member).Length);
            Assert.Equal(recipient.Countdown, rig.Countdown.To(recipient.Member).Length);
            Assert.Equal(recipient.Started, rig.Started.To(recipient.Member).Length);
        }

        cleanup.SetResult();
        await cleanup.Task;
        await rig.Rooms.HandleStartAsync(rig.Host, new RoomStartRequested(map), default);

        Assert.Equal(RoomPhase.Starting, room.Phase);
        Assert.Equal(generation + 1, room.StartGeneration);
        foreach (var recipient in recipients)
        {
            Assert.Equal(recipient.Starting + 1, rig.Starting.To(recipient.Member).Length);
            Assert.Equal(recipient.Countdown, rig.Countdown.To(recipient.Member).Length);
            Assert.Equal(recipient.Started, rig.Started.To(recipient.Member).Length);
        }
    }
}
