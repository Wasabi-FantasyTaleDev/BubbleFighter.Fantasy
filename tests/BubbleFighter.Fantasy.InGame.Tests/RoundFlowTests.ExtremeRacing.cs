using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Handlers;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Tests.Shared;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    private const int ExtremeRacingPause = 1100;

    /// <summary>A round counts each seat's goal once, only for the current round, and closes once at whichever clock wins.</summary>
    [Fact]
    public void ExtremeRacingStateCountsEachGoalOnceAndClosesOnce()
    {
        var race = new ExtremeRacingRoundState();
        Assert.False(race.Begin(2));
        Assert.Equal(ExtremeRacingFinish.Rejected, race.Finish(0, 1));
        Assert.True(race.Begin(1));
        Assert.False(race.Begin(1));
        Assert.Equal(ExtremeRacingFinish.Rejected, race.Finish(0, 2));
        Assert.Equal(ExtremeRacingFinish.First, race.Finish(3, 1));
        Assert.Equal(ExtremeRacingFinish.Rejected, race.Finish(3, 1));
        Assert.Equal(ExtremeRacingFinish.Later, race.Finish(1, 1));
        Assert.Null(race.Close(2, [5, 3], rounds: 2));
        var awarded = race.Close(1, [5], rounds: 2)!;
        Assert.Equal(5, awarded[3]);
        Assert.Equal(0, awarded[1]);
        Assert.Null(race.Close(1, [5], rounds: 2));
        Assert.Equal(ExtremeRacingPhase.Intermission, race.Phase);
        Assert.Equal(ExtremeRacingFinish.Rejected, race.Finish(2, 1));

        Assert.True(race.Begin(2));
        Assert.Empty(race.Finishers);
        Assert.Equal(ExtremeRacingFinish.First, race.Finish(1, 2));
        Assert.Equal(7, race.Close(2, [7], rounds: 2)![1]);
        Assert.Equal(ExtremeRacingPhase.Over, race.Phase);
        Assert.Equal(5, race.TotalOf(3));
        Assert.Equal(7, race.TotalOf(1));
        Assert.False(race.Begin(3));
    }

    /// <summary>Markers increase within one active round; reset, duplicate, late and invalid markers never regress progress.</summary>
    [Fact]
    public void ExtremeRacingCheckpointsAreBoundedAndMonotonic()
    {
        var race = new ExtremeRacingRoundState();
        Assert.False(race.Checkpoint(0, 1, 0));
        Assert.True(race.Begin(1));
        Assert.False(race.Checkpoint(0, 2, 0));
        Assert.False(race.Checkpoint(0, 1, -1));
        Assert.False(race.Checkpoint(0, 1, 10));
        Assert.True(race.Checkpoint(0, 1, 0));
        Assert.False(race.Checkpoint(0, 1, 0));
        Assert.True(race.Checkpoint(0, 1, 4));
        Assert.False(race.Checkpoint(0, 1, 3));
        Assert.True(race.Checkpoint(1, 1, 0));
        Assert.Equal(ExtremeRacingFinish.First, race.Finish(0, 1));
        Assert.False(race.Checkpoint(0, 1, 5));
        Assert.NotNull(race.Close(1, [10], 2));
        Assert.False(race.Checkpoint(1, 1, 5));
        Assert.True(race.Begin(2));
        Assert.True(race.Checkpoint(0, 2, 0));
        Assert.False(race.Checkpoint(0, 1, 9));
    }

    /// <summary>The mounted shared feed validates checkpoint ownership and race state before forwarding even to the sender.</summary>
    [Fact]
    public async Task ExtremeRacingCheckpointFeedRejectsSpoofingAndStaleProgress()
    {
        var (rig, extremeRacing) = ExtremeRacingRig(rounds: 1, countdownMs: 60_000);
        try
        {
            var room = await StartExtremeRacingRoundAsync(rig);
            await WaitForAsync(() => extremeRacing.Timers.To(rig.Host).Length == 1);
            int actor, otherActor;
            lock (room.Gate)
            {
                actor = RoomRegistry.SceneEntity(room.FindByGame(rig.Host.Id)!.Slot);
                otherActor = RoomRegistry.SceneEntity(room.FindByGame(rig.Guest.Id)!.Slot);
            }
            RoundFeedSent Feed(int namedActor, int round, int marker) => new(1, new byte[] { 1, 2, 3 })
            {
                IncludeSender = true, ReportedActors = [namedActor], ExtremeRacingCheckpoint = new(namedActor, round, marker),
            };
            await rig.Round.HandleFeedAsync(CaptainPlayer(rig.Host.Id, "impostor"), Feed(actor, 1, 0), default);
            await rig.Round.HandleFeedAsync(rig.Host, Feed(otherActor, 1, 0), default);
            await rig.Round.HandleFeedAsync(rig.Host, Feed(int.MaxValue, 1, 0), default);
            await rig.Round.HandleFeedAsync(rig.Host, Feed(actor, 2, 0), default);
            Assert.Empty(rig.Feed.To(rig.Guest));
            await rig.Round.HandleFeedAsync(rig.Host, Feed(actor, 1, 0), default);
            Assert.Single(rig.Feed.To(rig.Guest));
            Assert.Single(rig.Feed.To(rig.Host));
            await rig.Round.HandleFeedAsync(rig.Host, Feed(actor, 1, 0), default);
            await rig.Round.HandleFeedAsync(rig.Host, Feed(actor, 1, -1), default);
            await rig.Round.HandleFeedAsync(rig.Host, Feed(actor, 1, 10), default);
            Assert.Single(rig.Feed.To(rig.Guest));
            await rig.Round.HandleFeedAsync(rig.Host, Feed(actor, 1, 3), default);
            Assert.Equal(2, rig.Feed.To(rig.Guest).Length);
            await rig.Round.HandleExtremeRacingGoalAsync(rig.Host, new(1), default);
            await rig.Round.HandleFeedAsync(rig.Host, Feed(actor, 1, 4), default);
            Assert.Equal(2, rig.Feed.To(rig.Guest).Length);
            lock (room.Gate) room.RoundCancelled = true;
            await rig.Round.HandleFeedAsync(rig.Guest, Feed(otherActor, 1, 0), default);
            Assert.Equal(2, rig.Feed.To(rig.Guest).Length);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>
    /// A two-round race: the clock starts each round, the first goal starts the finish countdown, the last finisher
    /// closes the round at once, and the countdown closes a round nobody else finishes. The match result follows the
    /// last pause once, with the higher point total winning.
    /// </summary>
    [Fact]
    public async Task ExtremeRacingRoundsRunTheirClocksAndSettleOnce()
    {
        var (rig, extremeRacing) = ExtremeRacingRig(rounds: 2, countdownMs: 150);
        try
        {
            var room = await StartExtremeRacingRoundAsync(rig);
            Assert.True(Assert.Single(rig.Started.To(rig.Host)).Unlimited);
            await WaitForAsync(() => extremeRacing.Timers.To(rig.Guest).Length == 1);
            Assert.Equal(new RoundExtremeRacingTimerChanged(RoundExtremeRacingTimer.RaceRunning, 60_000, true), extremeRacing.Timers.To(rig.Guest)[0]);
            Assert.Empty(extremeRacing.Rounds.To(rig.Guest));

            await rig.Round.HandleExtremeRacingGoalAsync(rig.Host, new(2), default);
            Assert.Single(extremeRacing.Timers.To(rig.Guest));
            await rig.Round.HandleExtremeRacingGoalAsync(rig.Host, new(1), default);
            await rig.Round.HandleExtremeRacingGoalAsync(rig.Host, new(1), default);
            Assert.Equal(new RoundExtremeRacingTimerChanged(RoundExtremeRacingTimer.FinishCountdown, 150, true), extremeRacing.Timers.To(rig.Guest)[^1]);
            Assert.Equal(2, extremeRacing.Timers.To(rig.Guest).Length);

            await rig.Round.HandleExtremeRacingGoalAsync(rig.Guest, new(1), default);
            Assert.Equal(new RoundExtremeRacingTimerChanged[]
                {
                    new(RoundExtremeRacingTimer.FinishCountdown, 0, false), new(RoundExtremeRacingTimer.RoundOver, ExtremeRacingPause, true),
                }, extremeRacing.Timers.To(rig.Host)[2..4]);
            var first = Assert.Single(extremeRacing.Records.To(rig.Host));
            Assert.Equal(Points(10, 10), Values(first, room, rig.Host));
            Assert.Equal(Points(8, 8), Values(first, room, rig.Guest));

            await WaitForAsync(() => extremeRacing.Rounds.To(rig.Host).Length == 1);
            Assert.Equal(new RoundExtremeRacingRoundChanged(2), extremeRacing.Rounds.To(rig.Host)[0]);
            await WaitForAsync(() => extremeRacing.Timers.To(rig.Host).Length == 5);
            Assert.Equal(RoundExtremeRacingTimer.RaceRunning, extremeRacing.Timers.To(rig.Host)[4].Timer);

            await rig.Round.HandleExtremeRacingGoalAsync(rig.Host, new(2), default);
            await WaitForAsync(() => extremeRacing.Records.To(rig.Guest).Length == 2);
            Assert.Equal(Points(0, 8), Values(extremeRacing.Records.To(rig.Guest)[1], room, rig.Guest));
            Assert.Equal(Points(10, 20), Values(extremeRacing.Records.To(rig.Guest)[1], room, rig.Host));
            Assert.Empty(rig.Ended.To(rig.Host));

            await WaitForAsync(() => rig.Ended.To(rig.Host).Length == 1);
            Assert.Equal(RoundOutcome.Won, Assert.Single(rig.Ended.To(rig.Host)).Outcome);
            Assert.Equal(RoundOutcome.Lost, Assert.Single(rig.Ended.To(rig.Guest)).Outcome);
            await rig.Round.HandleExtremeRacingGoalAsync(rig.Guest, new(2), default);
            await Task.Delay(ExtremeRacingPause + 200);
            Assert.Single(rig.Ended.To(rig.Host));
            Assert.Equal(2, extremeRacing.Records.To(rig.Host).Length);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>When the only unfinished player leaves during the countdown, the round closes without waiting.</summary>
    [Fact]
    public async Task DepartureOfTheLastRacerClosesTheRound()
    {
        var (rig, extremeRacing) = ExtremeRacingRig(rounds: 2, countdownMs: 60_000);
        try
        {
            await StartExtremeRacingRoundAsync(rig);
            await WaitForAsync(() => extremeRacing.Timers.To(rig.Host).Length == 1);
            await rig.Round.HandleExtremeRacingGoalAsync(rig.Host, new(1), default);
            Assert.Empty(extremeRacing.Records.To(rig.Host));
            await rig.Rooms.HandleLeaveAsync(rig.Guest, new(), default);
            await WaitForAsync(() => extremeRacing.Records.To(rig.Host).Length == 1);
            Assert.Equal(new RoundExtremeRacingTimerChanged(RoundExtremeRacingTimer.RoundOver, ExtremeRacingPause, true), extremeRacing.Timers.To(rig.Host)[^1]);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Another connection object, a cancelled round and a mode other than 24 cannot report a goal.</summary>
    [Fact]
    public async Task GoalsNeedTheOwningSeatAndALiveRaceRound()
    {
        var (rig, extremeRacing) = ExtremeRacingRig(rounds: 1, countdownMs: 60_000);
        try
        {
            var room = await StartExtremeRacingRoundAsync(rig);
            await WaitForAsync(() => extremeRacing.Timers.To(rig.Host).Length == 1);
            await rig.Round.HandleEndRequestedAsync(rig.Host, new() { Reason = RoundEndReason.StageReported }, default);
            await rig.Round.HandleEndRequestedAsync(rig.Host, new() { Reason = RoundEndReason.WavesCleared }, default);
            Assert.Empty(rig.Ended.To(rig.Host));
            await rig.Round.HandleExtremeRacingGoalAsync(CaptainPlayer(rig.Host.Id, "impostor"), new(1), default);
            lock (room.Gate) Assert.Empty(room.ExtremeRacing.Finishers);
            lock (room.Gate) room.RoundCancelled = true;
            await room.ExtremeRacingClock!.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            await rig.Round.HandleExtremeRacingGoalAsync(rig.Host, new(1), default);
            lock (room.Gate) Assert.Empty(room.ExtremeRacing.Finishers);
            Assert.Single(extremeRacing.Timers.To(rig.Host));
        }
        finally { await rig.Registry.StopAllAsync(); }

        var plain = new Rig();
        try
        {
            await plain.StartAsync();
            await plain.Round.HandleLoadReportedAsync(plain.Host, new(), default);
            await plain.Round.HandleLoadReportedAsync(plain.Guest, new(), default);
            await plain.WaitStartedAsync(plain.Guest);
            await plain.Round.HandleExtremeRacingGoalAsync(plain.Host, new(1), default);
            Assert.False(Assert.Single(plain.Started.To(plain.Host)).Unlimited);
        }
        finally { await plain.Registry.StopAllAsync(); }
    }

    /// <summary>Race tunables outside what the client accepts are refused when the exchange is attached.</summary>
    [Theory]
    [InlineData(0, 1100, 10)]
    [InlineData(6, 1100, 10)]
    [InlineData(3, 1000, 10)]
    [InlineData(3, 1100, -1)]
    [InlineData(5, 1100, int.MaxValue)]
    public void ExtremeRacingOptionsAreValidated(int rounds, int pauseMs, int firstPoints)
    {
        var rig = new Rig(configure: options =>
        {
            options.ExtremeRacingRounds = rounds;
            options.ExtremeRacingIntermissionMs = pauseMs;
            options.ExtremeRacingPlacePoints = [firstPoints];
        });
        Assert.Throws<ConfigException>(() => rig.Round.ExtremeRacing = new ExtremeRacingSinks().Senders);
    }

    private sealed class ExtremeRacingSinks
    {
        public Sink<RoundExtremeRacingTimerChanged> Timers { get; } = new();
        public Sink<RoundExtremeRacingRoundChanged> Rounds { get; } = new();
        public Sink<RoundExtremeRacingRecordsUpdated> Records { get; } = new();
        public ExtremeRacingSenders Senders => new(Timers, Rounds, Records);
    }

    /// <summary>A reset on stage one clears the native wait, and the same player can relocate on stage two.</summary>
    [Fact]
    public async Task ExtremeRacingResetCompletesBeforeTheNextStageRelocates()
    {
        var (rig, racing) = ExtremeRacingRig(2, 60_000, nativeLife: true);
        try
        {
            var room = await StartExtremeRacingRoundAsync(rig);
            var seat = room.FindByGame(rig.Host.Id)!;
            var relay = new RecordingSession(91);
            rig.Registry.BindRelay(relay, seat.Key);
            var actor = RoomRegistry.SceneEntity(seat.Slot);
            byte[] body = [.. BitConverter.GetBytes(actor), 0xBA, 0xBA, 0, 0, 0, 0];
            var request = new RoundSpawnAccepted(actor, 0)
            {
                ActorSeat = seat.Slot, Request = body,
                Rules = new(RoundRespawnAudience.Requester, RoundRespawnCompletion.MatchConfirmation, RoundRespawnAdmission.CleanedDeath),
            };
            await rig.Round.HandleSpawnAcceptedAsync(rig.Host, request, default);
            Assert.Single(rig.Respawned.To(rig.Host));
            Assert.Equal(SeatLife.ResetPending, seat.Life.State);
            await rig.Round.HandleLifeReportedAsync(relay, new(RoundLifeEvent.ResetConfirmed, actor), default);
            Assert.Equal(SeatLife.Alive, seat.Life.State);
            await rig.Round.HandleExtremeRacingGoalAsync(rig.Host, new(1), default);
            await rig.Round.HandleExtremeRacingGoalAsync(rig.Guest, new(1), default);
            await WaitForAsync(() => racing.Rounds.To(rig.Host).Length == 1);
            await rig.Round.HandleSpawnAcceptedAsync(CaptainPlayer(rig.Host.Id, "stale"), request, default);
            await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Round.HandleSpawnAcceptedAsync(rig.Guest, request, default).AsTask());
            Assert.Single(rig.Respawned.To(rig.Host));
            Assert.Empty(rig.Respawned.To(rig.Guest));

            Assert.Equal(SeatLife.Alive, seat.Life.State);
            await rig.Round.HandleSpawnAcceptedAsync(rig.Host, request, default);
            Assert.Equal(body, rig.Respawned.To(rig.Host)[1].Request.ToArray());
            Assert.Empty(rig.Respawned.To(rig.Guest));
            Assert.Equal(SeatLife.ResetPending, seat.Life.State);
            await rig.Round.HandleSpawnAcceptedAsync(rig.Host, request, default);
            Assert.Equal(2, rig.Respawned.To(rig.Host).Length);
            await rig.Round.HandleLifeReportedAsync(relay, new(RoundLifeEvent.ResetConfirmed, actor), default);
            Assert.Equal(SeatLife.Alive, seat.Life.State);
            Assert.False(seat.Life.CreditDeath());
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A reset queued for a stage cannot authorize a different life or stage when the queue resumes.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExtremeRacingQueuedRelocationStaysWithItsStageAndLife(bool nextStage)
    {
        var (rig, racing) = ExtremeRacingRig(3, 60_000, nativeLife: true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? blocker = null;
        try
        {
            var room = await StartExtremeRacingRoundAsync(rig);
            var seat = room.FindByGame(rig.Host.Id)!;
            rig.Registry.BindRelay(new RecordingSession(91), seat.Key);
            var actor = RoomRegistry.SceneEntity(seat.Slot);
            await rig.Round.HandleExtremeRacingGoalAsync(rig.Host, new(1), default);
            await rig.Round.HandleExtremeRacingGoalAsync(rig.Guest, new(1), default);
            await WaitForAsync(() => racing.Rounds.To(rig.Host).Length == 1);
            blocker = room.Executor.ExecuteAsync(async _ =>
            {
                entered.SetResult();
                await release.Task;
                lock (room.Gate)
                {
                    if (nextStage)
                    {
                        room.ExtremeRacing.Close(2, [10], 3);
                        Assert.True(room.ExtremeRacing.Begin(3));
                        room.ExtremeRacing.ArmRoundReset(seat.Slot, seat.Life.Generation);
                    }
                    else seat.Life.Revive();
                }
            }, default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var request = new RoundSpawnAccepted(actor, 0)
            {
                ActorSeat = seat.Slot,
                Rules = new(RoundRespawnAudience.Requester, RoundRespawnCompletion.MatchConfirmation, RoundRespawnAdmission.CleanedDeath),
            };
            var pending = rig.Round.HandleSpawnAcceptedAsync(rig.Host, request, default).AsTask();
            Assert.False(pending.IsCompleted);
            release.SetResult();
            await Task.WhenAll(blocker, pending);
            Assert.Empty(rig.Respawned.To(rig.Host));
        }
        finally
        {
            release.TrySetResult();
            if (blocker is not null) await blocker;
            await rig.Registry.StopAllAsync();
        }
    }

    private static (Rig Rig, ExtremeRacingSinks ExtremeRacing) ExtremeRacingRig(int rounds, int countdownMs, bool nativeLife = false)
    {
        var rig = new Rig(configure: options =>
        {
            options.NativeLifeEveryMode = nativeLife;
            options.ExtremeRacingRounds = rounds;
            options.ExtremeRacingRoundMs = 60_000;
            options.ExtremeRacingFinishCountdownMs = countdownMs;
            options.ExtremeRacingIntermissionMs = ExtremeRacingPause;
            options.ExtremeRacingPlacePoints = [10, 8];
        });
        var extremeRacing = new ExtremeRacingSinks();
        rig.Round.ExtremeRacing = extremeRacing.Senders;
        return (rig, extremeRacing);
    }

    private static async Task<PlayRoom> StartExtremeRacingRoundAsync(Rig rig)
    {
        await rig.Rooms.HandleCreateAsync(rig.Host, new RoomCreateRequested("race room", "", (int)GameModeId.ExtremeRacing, "map"), default);
        var roomId = rig.Entered.To(rig.Host)[0].Room.RoomId;
        await rig.Rooms.HandleJoinAsync(rig.Guest, new RoomJoinRequested(roomId, ""), default);
        await rig.Rooms.HandleReadyAsync(rig.Guest, new RoomReadyRequested(true), default);
        await rig.Rooms.HandleStartAsync(rig.Host, new RoomStartRequested("map"), default);
        var room = rig.Registry.Find(roomId)!;
        await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
        await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
        await rig.WaitStartedAsync(rig.Host);
        await rig.WaitStartedAsync(rig.Guest);
        return room;
    }

    private static RoundExtremeRacingRecordValue[] Points(int round, int total) =>
        [new(RoundExtremeRacingRecordField.RoundFirst, round), new(RoundExtremeRacingRecordField.TotalFirst, total)];

    private static RoundExtremeRacingRecordValue[] Values(RoundExtremeRacingRecordsUpdated update, PlayRoom room, ISession member)
    {
        int slot;
        lock (room.Gate) slot = room.FindByGame(member.Id)?.Slot ?? -1;
        return [.. Assert.Single(update.Records, record => record.Seat == slot).Values];
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }
}
