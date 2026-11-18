using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Tests.Shared;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    /// <summary>Only the same playing master's 906 then zero-valued 624 fails the whole assault, once and before results.</summary>
    [Fact]
    public async Task PirateAssaultRequiresBothHostFailureReportsBeforeNotifyingEitherPlayer()
    {
        var rig = new Rig();
        var waves = new Sink<RoundNpcAcknowledged>();
        try
        {
            var room = await StartedRoundAsync(rig);
            lock (room.Gate) room.ModeId = 106;
            Task Notice(ISession session, params byte[] payload) => rig.Round.HandleNpcAsync(session,
                new(RoundNpcOperation.FailWaves, payload), waves, default).AsTask();
            Task Stage(ISession session, int value) => rig.Round.HandleMultiplayerStageResultAsync(session, new(value), default).AsTask();
            await Stage(rig.Host, 0);
            await Notice(rig.Guest);
            await Notice(new RecordingSession(rig.Host.Id));
            await Notice(rig.Host, 0);
            Assert.Null(room.PirateAssaultResult);
            await Notice(rig.Host);
            var original = Assert.IsType<PirateAssaultWaveResult>(room.PirateAssaultResult);
            await Notice(rig.Host);
            Assert.Same(original, room.PirateAssaultResult);
            await Stage(rig.Guest, 0);
            await Stage(new RecordingSession(rig.Host.Id), 0);
            await Stage(rig.Host, 1);
            await rig.Round.HandleNpcAsync(rig.Host, new(RoundNpcOperation.CompleteWaves, ReadOnlyMemory<byte>.Empty), waves, default);
            Assert.Empty(waves.To(rig.Host));
            Assert.Empty(waves.To(rig.Guest));
            Assert.Empty(rig.Ended.To(rig.Host));
            Assert.Equal(RoomPhase.Playing, room.Phase);
            waves.OnSending = (s, _, _) =>
            {
                Assert.Empty(rig.Ended.To(rig.Host));
                if (s == rig.Guest) throw new IOException("second recipient");
            };
            await Assert.ThrowsAsync<IOException>(() => Stage(rig.Host, 0));
            Assert.Single(waves.To(rig.Host));
            Assert.Empty(waves.To(rig.Guest));
            Assert.Empty(rig.Ended.To(rig.Host));
            waves.OnSending = null;
            await Task.WhenAll(Stage(rig.Host, 0), Stage(rig.Host, 0));
            foreach (var member in new[] { rig.Host, rig.Guest })
            {
                var failure = Assert.Single(waves.To(member));
                Assert.Equal(RoundNpcOperation.FailWaves, failure.Operation);
                Assert.True(failure.Payload.IsEmpty);
                var result = Assert.Single(rig.Ended.To(member));
                Assert.Equal(RoundEndReason.CooperativeFailed, result.Reason);
                Assert.Equal(RoundOutcome.Lost, result.Outcome);
            }
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Only the current master's matching 905 and positive stage word let both native clients hear a clear and receive a win.</summary>
    [Fact]
    public async Task PirateAssaultClearFollowsTheSameHostThroughBothMessagesAndPublishesToAllSeats()
    {
        var rig = new Rig();
        var waves = new Sink<RoundNpcAcknowledged>();
        try
        {
            var room = await StartedRoundAsync(rig);
            lock (room.Gate) room.ModeId = 106;
            rig.Round.PirateAssaultTerminalReports = true;
            Task Clear(ISession who) => rig.Round.HandleNpcAsync(who,
                new(RoundNpcOperation.CompleteWaves, ReadOnlyMemory<byte>.Empty), waves, default).AsTask();
            Task Stage(ISession who, int outcome) => rig.Round.HandleMultiplayerStageResultAsync(who, new(outcome), default).AsTask();
            await Clear(rig.Guest);
            await Stage(rig.Host, 1);
            await rig.Round.HandleEndRequestedAsync(rig.Host, new() { StageSucceeded = true }, default);
            Assert.Empty(rig.Ended.To(rig.Host));
            await Clear(rig.Host);
            lock (room.Gate)
            {
                Assert.True(room.TransferMaster(room.FindByGame(rig.Guest.Id)!.Slot));
                Assert.True(room.TransferMaster(room.FindByGame(rig.Host.Id)!.Slot));
            }
            await Stage(rig.Host, 1);
            Assert.Empty(waves.To(rig.Host));
            await Clear(rig.Host);
            await Stage(rig.Host, 0);
            await Stage(rig.Guest, 1);
            await Stage(new RecordingSession(rig.Host.Id), 1);
            Assert.Empty(waves.To(rig.Guest));
            Assert.Equal(RoomPhase.Playing, room.Phase);
            waves.OnSending = (_, _, _) => Assert.Empty(rig.Ended.To(rig.Host));
            await Task.WhenAll(Stage(rig.Host, 1), Stage(rig.Host, 1));
            foreach (var member in new[] { rig.Host, rig.Guest })
            {
                var cleared = Assert.Single(waves.To(member));
                Assert.Equal(RoundNpcOperation.CompleteWaves, cleared.Operation);
                Assert.True(cleared.Payload.IsEmpty);
                var result = Assert.Single(rig.Ended.To(member));
                Assert.Equal(RoundEndReason.WavesCleared, result.Reason);
                Assert.Equal(RoundOutcome.Won, result.Outcome);
            }
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A pending 906 cannot survive a restart or authorize a terminal message in the original pirate mode.</summary>
    [Fact]
    public async Task PirateAssaultFailureCannotCrossTheRoundBoundaryOrSelectOtherPirateModes()
    {
        var rig = new Rig();
        var waves = new Sink<RoundNpcAcknowledged>();
        try
        {
            var room = await StartedRoundAsync(rig);
            foreach (var unrelated in new ushort[] { 101, 102, 103 })
            {
                lock (room.Gate) room.ModeId = unrelated;
                await rig.Round.HandleNpcAsync(rig.Host, new(RoundNpcOperation.FailWaves, ReadOnlyMemory<byte>.Empty), waves, default);
                Assert.Null(room.PirateAssaultResult);
            }
            lock (room.Gate) room.ModeId = 106;
            await rig.Round.HandleNpcAsync(rig.Host, new(RoundNpcOperation.FailWaves, ReadOnlyMemory<byte>.Empty), waves, default);
            Assert.NotNull(room.PirateAssaultResult);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var blocking = room.Executor.ExecuteAsync(async ct => { entered.SetResult(); await release.Task.WaitAsync(ct); });
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var stale = rig.Round.HandleMultiplayerStageResultAsync(rig.Host, new(0), default).AsTask();
                Assert.False(stale.IsCompleted);
                lock (room.Gate) { room.ResetToWaiting(); room.MarkStarting(); room.MarkPlaying(); }
                release.SetResult();
                await Task.WhenAll(blocking, stale).WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { release.TrySetResult(); }
            Assert.Null(room.PirateAssaultResult);
            Assert.Empty(waves.To(rig.Host));
            Assert.Empty(rig.Ended.To(rig.Guest));
            Assert.Equal(RoomPhase.Playing, room.Phase);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }
}
