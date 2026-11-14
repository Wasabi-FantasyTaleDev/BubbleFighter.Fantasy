using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Sessions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    private static readonly TimeSpan RacingFailureWait = TimeSpan.FromSeconds(5);

    /// <summary>A partial ordinary start or first race timer closes the generation even without optional object initialization.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RacingInitialPublicationFailureAwaitsGenerationCleanup(bool initialize, bool failTimer)
    {
        var (rig, racing, initialization) = RacingFailureRig(initialize);
        var room = await PrepareRacingInitializationRoundAsync(rig);
        var original = room.ExtremeRacing;
        var closing = BindDeferredRacingRelay(rig, room);
        if (failTimer)
            racing.Timers.OnSending = (session, _, _) => ThrowForRacingGuest(rig, session);
        else
            rig.Started.OnSending = (session, _, _) => ThrowForRacingGuest(rig, session);
        try
        {
            await LoadRacingFailureRoomAsync(rig);
            var countdown = room.CountdownTask;
            await closing.Entered.Task.WaitAsync(RacingFailureWait);
            Assert.True(room.RoundCancelled);
            Assert.False(countdown.IsCompleted);
            Assert.False(room.ExtremeRacingCleanupTask.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => rig.Round.ValidateModeMap(room, "map"));
            foreach (var member in new[] { rig.Host, rig.Guest })
                Assert.Equal(initialize ? 1 : 0, initialization.To(member).Length);
            Assert.Single(rig.Started.To(rig.Host));
            Assert.Equal(failTimer ? 1 : 0, rig.Started.To(rig.Guest).Length);
            Assert.Equal(failTimer ? 1 : 0, racing.Timers.To(rig.Host).Length);
            Assert.Empty(racing.Timers.To(rig.Guest));
            closing.Release.TrySetResult();
            await countdown.WaitAsync(RacingFailureWait);
            await AssertRacingCleanupAsync(rig, room, original, closing);
        }
        finally
        {
            closing.Release.TrySetResult();
            await rig.Registry.StopAllAsync();
        }
    }

    /// <summary>Failures in scheduled round-over, records or next-round publication drain the clock and await peer cleanup.</summary>
    [Theory]
    [InlineData("round-over")]
    [InlineData("records")]
    [InlineData("next-round")]
    public async Task RacingScheduledPublicationFailureCancelsItsClock(string publication)
    {
        var (rig, racing, _) = RacingFailureRig(configure: options =>
        {
            options.ExtremeRacingRoundMs = publication == "next-round" ? 60_000 : 200;
            options.ExtremeRacingIntermissionMs = 1001;
        });
        var room = await PrepareRacingInitializationRoundAsync(rig);
        var original = room.ExtremeRacing;
        var closing = BindDeferredRacingRelay(rig, room);
        racing.Timers.OnSending = (session, message, _) =>
        {
            if (publication == "round-over" && message.Timer == RoundExtremeRacingTimer.RoundOver)
                ThrowForRacingGuest(rig, session);
        };
        racing.Records.OnSending = (session, _, _) =>
        {
            if (publication == "records") ThrowForRacingGuest(rig, session);
        };
        racing.Rounds.OnSending = (session, _, _) =>
        {
            if (publication == "next-round") ThrowForRacingGuest(rig, session);
        };
        try
        {
            await LoadRacingFailureRoomAsync(rig);
            await room.CountdownTask.WaitAsync(RacingFailureWait);
            var clock = Assert.IsType<RoomDeadlineClock>(room.ExtremeRacingClock);
            if (publication == "next-round")
            {
                await rig.Round.HandleExtremeRacingGoalAsync(rig.Host, new(1), default);
                await rig.Round.HandleExtremeRacingGoalAsync(rig.Guest, new(1), default);
            }
            await closing.Entered.Task.WaitAsync(RacingFailureWait);
            Assert.True(room.RoundCancelled);
            Assert.False(clock.Completion.IsCompleted);
            Assert.False(room.ExtremeRacingCleanupTask.IsCompleted);
            closing.Release.TrySetResult();
            await AssertRacingCleanupAsync(rig, room, original, closing);
            Assert.True(clock.Completion.IsCompletedSuccessfully);
            if (publication == "next-round")
            {
                Assert.Single(racing.Rounds.To(rig.Host));
                Assert.Empty(racing.Rounds.To(rig.Guest));
            }
            else Assert.Empty(racing.Rounds.To(rig.Host));
        }
        finally
        {
            closing.Release.TrySetResult();
            await rig.Registry.StopAllAsync();
        }
    }

    /// <summary>A direct goal cannot return its publication error before the partially published race has closed its connections.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RacingDirectGoalFailureAwaitsCleanup(bool failRecords)
    {
        var (rig, racing, _) = RacingFailureRig();
        var room = await StartExtremeRacingRoundAsync(rig);
        await room.CountdownTask.WaitAsync(RacingFailureWait);
        var original = room.ExtremeRacing;
        var closing = BindDeferredRacingRelay(rig, room);
        if (failRecords)
        {
            await rig.Round.HandleExtremeRacingGoalAsync(rig.Host, new(1), default);
            racing.Records.OnSending = (session, _, _) => ThrowForRacingGuest(rig, session);
        }
        else
            racing.Timers.OnSending = (session, message, _) =>
            {
                if (message.Timer == RoundExtremeRacingTimer.FinishCountdown) ThrowForRacingGuest(rig, session);
            };
        Task? report = null;
        try
        {
            report = rig.Round.HandleExtremeRacingGoalAsync(failRecords ? rig.Guest : rig.Host, new(1), default).AsTask();
            await closing.Entered.Task.WaitAsync(RacingFailureWait);
            Assert.True(room.RoundCancelled);
            Assert.False(report.IsCompleted);
            closing.Release.TrySetResult();
            await Assert.ThrowsAsync<IOException>(() => report.WaitAsync(RacingFailureWait));
            await AssertRacingCleanupAsync(rig, room, original, closing);
        }
        finally
        {
            closing.Release.TrySetResult();
            if (report is not null) await ObserveRacingFailureAsync(report);
            await rig.Registry.StopAllAsync();
        }
    }

    /// <summary>A rejected start, goal or deadline closes its generation without enqueueing cleanup behind an occupied room.</summary>
    [Theory]
    [InlineData("start")]
    [InlineData("goal")]
    [InlineData("clock")]
    public async Task RacingQueueRejectionAwaitsCleanupOutsideTheRoomExecutor(string operation)
    {
        var (rig, _, _) = RacingFailureRig(configure: options => options.RoomQueueCapacity = 1);
        var countdownEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCountdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (operation == "start")
            rig.Round.CountdownDelay = (_, ct) =>
            {
                countdownEntered.TrySetResult();
                return releaseCountdown.Task.WaitAsync(ct);
            };
        var room = await PrepareRacingInitializationRoundAsync(rig);
        var original = room.ExtremeRacing;
        var closing = BindDeferredRacingRelay(rig, room);
        var runningEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task running = Task.CompletedTask, queued = Task.CompletedTask;
        Task? rejected = null;
        var queuedRan = false;
        var clockStepRan = false;
        try
        {
            await LoadRacingFailureRoomAsync(rig);
            if (operation == "start") await countdownEntered.Task.WaitAsync(RacingFailureWait);
            else await room.CountdownTask.WaitAsync(RacingFailureWait);
            running = room.Executor.ExecuteAsync(async ct =>
            {
                runningEntered.TrySetResult();
                await releaseRunning.Task.WaitAsync(ct);
            });
            await runningEntered.Task.WaitAsync(RacingFailureWait);
            queued = room.Executor.ExecuteAsync(_ => { queuedRan = true; return ValueTask.CompletedTask; });
            if (operation == "start")
            {
                rejected = room.CountdownTask;
                releaseCountdown.TrySetResult();
            }
            else if (operation == "goal")
                rejected = rig.Round.HandleExtremeRacingGoalAsync(rig.Host, new(1), default).AsTask();
            else
            {
                var clock = Assert.IsType<RoomDeadlineClock>(room.ExtremeRacingClock);
                rejected = clock.Completion;
                clock.Schedule(0, _ => { clockStepRan = true; return ValueTask.CompletedTask; });
            }
            await closing.Entered.Task.WaitAsync(RacingFailureWait);
            Assert.True(room.RoundCancelled);
            Assert.False(rejected.IsCompleted);
            Assert.False(running.IsCompleted);
            Assert.False(queued.IsCompleted);
            Assert.False(queuedRan);
            Assert.False(clockStepRan);
            closing.Release.TrySetResult();
            if (operation == "goal")
                await Assert.ThrowsAsync<InvalidOperationException>(() => rejected.WaitAsync(RacingFailureWait));
            else await rejected.WaitAsync(RacingFailureWait);
            await AssertRacingCleanupAsync(rig, room, original, closing);
            Assert.False(queuedRan);
            Assert.False(running.IsCompleted);
        }
        finally
        {
            releaseCountdown.TrySetResult();
            closing.Release.TrySetResult();
            releaseRunning.TrySetResult();
            await Task.WhenAll(running, queued).WaitAsync(RacingFailureWait);
            if (rejected is not null) await ObserveRacingFailureAsync(rejected);
            await rig.Registry.StopAllAsync();
        }
    }

    /// <summary>A failed old start, goal or clock step never closes a replacement race or cancels its clock.</summary>
    [Theory]
    [InlineData("start")]
    [InlineData("first-timer")]
    [InlineData("goal")]
    [InlineData("clock")]
    public async Task RacingPublicationFailureCannotCancelAReplacementGeneration(string operation)
    {
        var (rig, racing, _) = RacingFailureRig(configure: options =>
            options.ExtremeRacingRoundMs = operation == "clock" ? 200 : 60_000);
        var room = await PrepareRacingInitializationRoundAsync(rig);
        var original = room.ExtremeRacing;
        var generation = room.StartGeneration;
        RoomDeadlineClock? oldClock = null;
        RoomDeadlineClock? replacementClock = null;
        var replaced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementFailures = new List<Exception>();
        void ReplaceAndFail(ISession session)
        {
            if (!ReferenceEquals(session, rig.Guest)) return;
            lock (room.Gate)
            {
                oldClock = room.ExtremeRacingClock;
                room.ResetToWaiting();
                room.MarkStarting();
                room.RoundMapKey = room.RoundStageKey = "map";
                room.MarkPlaying();
                room.ExtremeRacingClock = replacementClock = new(room.Executor, default, replacementFailures.Add);
                replacementClock.Schedule(60_000, _ => ValueTask.CompletedTask);
            }
            replaced.TrySetResult();
            throw new IOException("Synthetic old racing publication failure.");
        }
        if (operation == "start") rig.Started.OnSending = (session, _, _) => ReplaceAndFail(session);
        racing.Timers.OnSending = (session, message, _) =>
        {
            if (operation == "first-timer" && message.Timer == RoundExtremeRacingTimer.RaceRunning
                || operation == "goal" && message.Timer == RoundExtremeRacingTimer.FinishCountdown
                || operation == "clock" && message.Timer == RoundExtremeRacingTimer.RoundOver)
                ReplaceAndFail(session);
        };
        try
        {
            await LoadRacingFailureRoomAsync(rig);
            var countdown = room.CountdownTask;
            if (operation == "goal")
            {
                await countdown.WaitAsync(RacingFailureWait);
                await Assert.ThrowsAsync<IOException>(() => rig.Round.HandleExtremeRacingGoalAsync(rig.Host, new(1), default).AsTask());
            }
            await replaced.Task.WaitAsync(RacingFailureWait);
            await countdown.WaitAsync(RacingFailureWait);
            if (oldClock is not null) await oldClock.Completion.WaitAsync(RacingFailureWait);
            Assert.Equal(generation + 1, room.StartGeneration);
            Assert.NotSame(original, room.ExtremeRacing);
            Assert.Null(original.CleanupTask);
            Assert.Null(room.ExtremeRacing.CleanupTask);
            Assert.True(room.ExtremeRacingCleanupTask.IsCompletedSuccessfully);
            Assert.False(room.RoundCancelled);
            Assert.False(rig.Host.Connection.IsClosed);
            Assert.False(rig.Guest.Connection.IsClosed);
            Assert.Same(replacementClock, room.ExtremeRacingClock);
            Assert.False(Assert.IsType<RoomDeadlineClock>(replacementClock).Completion.IsCompleted);
            Assert.Empty(replacementFailures);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Room shutdown waits for cleanup started by a failed report even after the race state was replaced.</summary>
    [Fact]
    public async Task RacingShutdownDrainsCleanupAcrossStateReset()
    {
        var (rig, racing, _) = RacingFailureRig();
        var room = await StartExtremeRacingRoundAsync(rig);
        await room.CountdownTask.WaitAsync(RacingFailureWait);
        var closing = BindDeferredRacingRelay(rig, room);
        racing.Timers.OnSending = (session, _, _) => ThrowForRacingGuest(rig, session);
        var report = rig.Round.HandleExtremeRacingGoalAsync(rig.Host, new(1), default).AsTask();
        Task? stopping = null;
        try
        {
            await closing.Entered.Task.WaitAsync(RacingFailureWait);
            var cleanup = room.ExtremeRacingCleanupTask;
            lock (room.Gate)
            {
                room.ResetToWaiting();
                room.MarkStarting();
            }
            stopping = rig.Registry.StopAllAsync();
            Assert.False(stopping.IsCompleted);
            Assert.Same(cleanup, room.ExtremeRacingCleanupTask);
            closing.Release.TrySetResult();
            await Assert.ThrowsAsync<IOException>(() => report.WaitAsync(RacingFailureWait));
            await stopping.WaitAsync(RacingFailureWait);
            Assert.True(cleanup.IsCompletedSuccessfully);
            Assert.Equal(1, closing.CloseCalls);
        }
        finally
        {
            closing.Release.TrySetResult();
            await ObserveRacingFailureAsync(report);
            if (stopping is not null) await stopping.WaitAsync(RacingFailureWait);
            else await rig.Registry.StopAllAsync();
        }
    }

    /// <summary>A retired initializer cannot cancel returned players or a different initializer in the same race generation.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RacingInitializerFailureRetainsItsExactStateFence(bool returnToWaiting)
    {
        var (rig, _, initialization) = RacingFailureRig(initialize: true);
        var room = await PrepareRacingInitializationRoundAsync(rig);
        var original = room.ExtremeRacing;
        var generation = room.StartGeneration;
        initialization.OnSending = (session, _) =>
        {
            if (!ReferenceEquals(session, rig.Guest)) return ValueTask.CompletedTask;
            lock (room.Gate)
            {
                var map = room.RacingInitialization!.Map;
                if (returnToWaiting) room.ResetToWaiting();
                else room.RacingInitialization = new(generation, map);
            }
            return ValueTask.FromException(new IOException("Synthetic retired racing initializer failure."));
        };
        try
        {
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await Assert.ThrowsAsync<IOException>(() => rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default).AsTask());
            Assert.Equal(generation, room.StartGeneration);
            Assert.Same(original, room.ExtremeRacing);
            Assert.Equal(returnToWaiting ? RoomPhase.Waiting : RoomPhase.Starting, room.Phase);
            Assert.False(room.RoundCancelled);
            Assert.Null(original.CleanupTask);
            Assert.True(room.ExtremeRacingCleanupTask.IsCompletedSuccessfully);
            Assert.False(rig.Host.Connection.IsClosed);
            Assert.False(rig.Guest.Connection.IsClosed);
            Assert.Single(initialization.To(rig.Host));
            Assert.Empty(initialization.To(rig.Guest));
            if (returnToWaiting) Assert.Null(room.RacingInitialization);
            else Assert.Empty(room.RacingInitialization!.Recipients);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Initializer cleanup checks the same state again after an asynchronous close before closing another participant.</summary>
    [Fact]
    public async Task RacingInitializerCleanupStopsWhenItsInitializerIsReplaced()
    {
        var (rig, _, initialization) = RacingFailureRig(initialize: true);
        var room = await PrepareRacingInitializationRoundAsync(rig);
        var closing = BindDeferredRacingRelay(rig, room, host: true);
        initialization.OnSending = (session, _) => ReferenceEquals(session, rig.Guest)
            ? ValueTask.FromException(new IOException("Synthetic racing initializer failure.")) : ValueTask.CompletedTask;
        Task? loading = null;
        try
        {
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            loading = rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default).AsTask();
            await closing.Entered.Task.WaitAsync(RacingFailureWait);
            Assert.True(rig.Host.Connection.IsClosed);
            Assert.False(rig.Guest.Connection.IsClosed);
            lock (room.Gate)
                room.RacingInitialization = new(room.StartGeneration, room.RacingInitialization!.Map);
            closing.Release.TrySetResult();
            await Assert.ThrowsAsync<IOException>(() => loading.WaitAsync(RacingFailureWait));
            Assert.False(rig.Guest.Connection.IsClosed);
            Assert.True(room.ExtremeRacingCleanupTask.IsCompletedSuccessfully);
            Assert.Equal(1, closing.CloseCalls);
        }
        finally
        {
            closing.Release.TrySetResult();
            if (loading is not null) await ObserveRacingFailureAsync(loading);
            await rig.Registry.StopAllAsync();
        }
    }

    /// <summary>An old direct or scheduled publication failure does not close players already returned to Waiting in the same generation.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RacingPublicationFailureLeavesReturnedPlayersConnected(bool scheduled)
    {
        var (rig, racing, _) = RacingFailureRig(configure: options => options.ExtremeRacingRoundMs = scheduled ? 200 : 60_000);
        var room = await PrepareRacingInitializationRoundAsync(rig);
        var original = room.ExtremeRacing;
        var generation = room.StartGeneration;
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        racing.Timers.OnSending = (session, message, _) =>
        {
            var target = scheduled ? RoundExtremeRacingTimer.RoundOver : RoundExtremeRacingTimer.FinishCountdown;
            if (!ReferenceEquals(session, rig.Guest) || message.Timer != target) return;
            lock (room.Gate) room.ResetToWaiting();
            returned.TrySetResult();
            throw new IOException("Synthetic publication failure after returning to the room.");
        };
        try
        {
            await LoadRacingFailureRoomAsync(rig);
            await room.CountdownTask.WaitAsync(RacingFailureWait);
            var clock = Assert.IsType<RoomDeadlineClock>(room.ExtremeRacingClock);
            if (!scheduled)
                await Assert.ThrowsAsync<IOException>(() => rig.Round.HandleExtremeRacingGoalAsync(rig.Host, new(1), default).AsTask());
            await returned.Task.WaitAsync(RacingFailureWait);
            await clock.Completion.WaitAsync(RacingFailureWait);
            Assert.Equal(generation, room.StartGeneration);
            Assert.Same(original, room.ExtremeRacing);
            Assert.Equal(RoomPhase.Waiting, room.Phase);
            Assert.False(room.RoundCancelled);
            Assert.Null(original.CleanupTask);
            Assert.False(rig.Host.Connection.IsClosed);
            Assert.False(rig.Guest.Connection.IsClosed);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    private static (Rig Rig, ExtremeRacingSinks Racing, RacingInitializationSink Initialization) RacingFailureRig(
        bool initialize = false, Action<InGameOptions>? configure = null)
    {
        var (rig, initialization) = RacingInitializationRig(mount: initialize, configure: options =>
        {
            options.ExtremeRacingInitialization = initialize;
            options.CancelRoundOnDisconnect = false;
            configure?.Invoke(options);
        });
        var racing = new ExtremeRacingSinks();
        rig.Round.ExtremeRacing = racing.Senders;
        return (rig, racing, initialization);
    }

    private static async Task LoadRacingFailureRoomAsync(Rig rig)
    {
        await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
        await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
    }

    private static void ThrowForRacingGuest(Rig rig, ISession session)
    {
        if (ReferenceEquals(session, rig.Guest)) throw new IOException("Synthetic racing publication failure.");
    }

    private static RacingDeferredCloseConnection BindDeferredRacingRelay(Rig rig, PlayRoom room, bool host = false)
    {
        var member = host ? rig.Host : rig.Guest;
        var inner = new RecordingSession(member.Id + 1000, member.Profile);
        var connection = new RacingDeferredCloseConnection(inner.Id);
        rig.Registry.BindRelay(new RacingConnectionSession(inner, connection), room.FindByGame(member.Id)!.Key);
        return connection;
    }

    private static async Task AssertRacingCleanupAsync(Rig rig, PlayRoom room, ExtremeRacingRoundState original,
        RacingDeferredCloseConnection connection)
    {
        await room.ExtremeRacingCleanupTask.WaitAsync(RacingFailureWait);
        if (room.ExtremeRacingClock is { } clock) await clock.Completion.WaitAsync(RacingFailureWait);
        Assert.Same(original.CleanupTask, room.ExtremeRacingCleanupTask);
        Assert.True(room.ExtremeRacingCleanupTask.IsCompletedSuccessfully);
        Assert.True(room.RoundCancelled);
        Assert.True(rig.Host.Connection.IsClosed);
        Assert.True(rig.Guest.Connection.IsClosed);
        Assert.True(connection.IsClosed);
        Assert.Equal(1, connection.CloseCalls);
    }

    private static async Task ObserveRacingFailureAsync(Task task)
    {
        try { await task.WaitAsync(RacingFailureWait); }
        catch (IOException) { }
        catch (InvalidOperationException) { }
    }

    private sealed class RacingConnectionSession(RecordingSession inner, IConnection connection) : ISessionWithFeatures
    {
        public long Id => inner.Id;
        public IConnection Connection { get; } = connection;
        public ClientProfile Profile => inner.Profile;
        public ConcurrentDictionary<string, object> Items => inner.Items;
        public ISessionFeatures Features => inner.Features;
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) => inner.SendAsync(opcode, payload, ct);
    }

    private sealed class RacingDeferredCloseConnection(long id) : IConnection
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int CloseCalls { get; private set; }
        public long Id { get; } = id;
        public EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 1);
        public PipeReader Input => throw new NotSupportedException();
        public PipeWriter Output => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public bool IsClosed { get; private set; }
        public async ValueTask CloseAsync(string reason)
        {
            CloseCalls++;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(RacingFailureWait);
            IsClosed = true;
        }
    }
}
