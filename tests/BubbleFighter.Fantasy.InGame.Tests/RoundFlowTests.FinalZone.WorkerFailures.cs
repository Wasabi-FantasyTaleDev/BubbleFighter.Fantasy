using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Sessions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    /// <summary>A voluntary departure updates each later worker recipient set while the remaining contested round continues.</summary>
    [Theory]
    [InlineData("water")]
    [InlineData("drone")]
    public async Task FinalZoneWorkersContinueAfterParticipantLeaves(string operation)
    {
        var rig = new Rig(configure: options => options.FinalZoneMaximumDrones = 32);
        var entered = FinalZoneSignal();
        var release = FinalZoneSignal();
        var afterDeparture = FinalZoneSignal();
        Task? leaving = null;
        try
        {
            var third = CaptainPlayer(33, "third");
            var room = await PrepareFinalZonePublicationRoomAsync(rig, third);
            var state = room.FinalZone!;
            ConfigureFinalZonePublicationWorker(state, operation, 0);
            if (operation == "water") state.WaterRules = new(true, 0, 1, 0, 1, 1, 1, 1, 60_000);
            else state.DroneRules = new(true, new(0, 0), new(1, 1));
            var sends = 0;
            var departedSends = 0;
            async ValueTask SendAsync(ISession session)
            {
                if (Interlocked.Increment(ref sends) == 1)
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(OneKillFailureTimeout);
                }
                lock (room.Gate)
                    if (room.Seats.Count == 2)
                    {
                        if (ReferenceEquals(session, third)) departedSends++;
                        if (ReferenceEquals(session, rig.Guest)) afterDeparture.TrySetResult();
                    }
            }
            rig.Round.FinalZoneWaterTimer = new FinalZoneAsyncSender<RoundFinalZoneWaterTimerChanged>((session, _, _) => SendAsync(session));
            rig.Round.FinalZoneDroneSpawned = new FinalZoneAsyncSender<RoundFinalZoneDroneSpawned>((session, _, _) => SendAsync(session));
            await LoadFinalZonePublicationRoomAsync(rig, room);
            await entered.Task.WaitAsync(OneKillFailureTimeout);
            leaving = rig.Rooms.HandleLeaveAsync(third, new(), default).AsTask();
            release.TrySetResult();
            await leaving.WaitAsync(OneKillFailureTimeout);
            await afterDeparture.Task.WaitAsync(OneKillFailureTimeout);
            await room.Executor.ExecuteAsync(_ => ValueTask.CompletedTask).WaitAsync(OneKillFailureTimeout);
            Assert.Equal(2, room.Seats.Count);
            Assert.Same(state, room.FinalZone);
            Assert.False(state.Finished);
            Assert.False(room.RoundCancelled);
            Assert.Equal(RoomPhase.Playing, room.Phase);
            Assert.Equal(0, departedSends);
            Assert.Null(state.CleanupTask);
            Assert.Empty(rig.Ended.To(rig.Host));
        }
        finally
        {
            release.TrySetResult();
            if (leaving is not null) await leaving.WaitAsync(OneKillFailureTimeout);
            await rig.Registry.StopAllAsync();
        }
    }

    /// <summary>Water stop, water start and drone failures retain their worker until the shared connection cleanup completes.</summary>
    [Theory]
    [InlineData("water-stop")]
    [InlineData("water-start")]
    [InlineData("drone")]
    public async Task FinalZoneWorkerPublicationFailureAwaitsCleanupAndShutdown(string operation)
    {
        var rig = new Rig();
        var deferred = new OneKillDeferredCloseConnection(904);
        var third = new OneKillConnectionSession(CaptainPlayer(deferred.Id, "third"), deferred);
        Task? shutdown = null;
        try
        {
            var room = await PrepareFinalZonePublicationRoomAsync(rig, third);
            var state = room.FinalZone!;
            ConfigureFinalZonePublicationWorker(state, operation, 0);
            state.WaterPhase = operation == "water-stop" ? 4 : 0;
            var water = new Sink<RoundFinalZoneWaterTimerChanged>
            {
                OnSending = (session, _, _) =>
                {
                    if (ReferenceEquals(session, rig.Guest)) throw new IOException("water publication");
                },
            };
            var drones = new Sink<RoundFinalZoneDroneSpawned>
            {
                OnSending = (session, _, _) =>
                {
                    if (ReferenceEquals(session, rig.Guest)) throw new IOException("drone publication");
                },
            };
            rig.Round.FinalZoneWaterTimer = water;
            rig.Round.FinalZoneDroneSpawned = drones;
            await LoadFinalZonePublicationRoomAsync(rig, room);
            await room.CountdownTask.WaitAsync(OneKillFailureTimeout);
            await deferred.Entered.Task.WaitAsync(OneKillFailureTimeout);
            var clock = Assert.IsType<RoomDeadlineClock>(operation == "drone" ? room.FinalZoneDroneClock : room.FinalZoneWaterClock);
            Assert.True(room.RoundCancelled);
            Assert.False(clock.Completion.IsCompleted);
            Assert.False(room.FinalZoneCleanupTask.IsCompleted);
            Assert.Equal(operation == "water-stop" ? 4 : 0, state.WaterPhase);
            if (operation == "drone")
            {
                Assert.Single(drones.To(rig.Host));
                Assert.Equal(1, state.Items!.DroneCount);
                Assert.Equal(2, state.Items.IssuedObjects);
            }
            else Assert.Equal(operation != "water-stop", Assert.Single(water.To(rig.Host)).Active);
            Assert.Empty(water.To(rig.Guest));
            Assert.Empty(drones.To(rig.Guest));
            shutdown = rig.Registry.StopAllAsync();
            Assert.False(shutdown.IsCompleted);
            deferred.Release.TrySetResult();
            await Task.WhenAll(shutdown, clock.Completion).WaitAsync(OneKillFailureTimeout);
            Assert.Equal(1, deferred.CloseCalls);
            Assert.Empty(rig.Ended.To(rig.Host));
        }
        finally
        {
            deferred.Release.TrySetResult();
            if (shutdown is not null) await shutdown.WaitAsync(OneKillFailureTimeout);
            await rig.Registry.StopAllAsync();
        }
    }

    /// <summary>Rejected opening, inventory and worker queue entries await cleanup without requiring another room queue slot.</summary>
    [Theory]
    [InlineData("opening")]
    [InlineData("inventory")]
    [InlineData("water")]
    [InlineData("drone")]
    public async Task FinalZoneNonterminalQueueRejectionAwaitsOwnedCleanup(string operation)
    {
        var rig = new Rig(configure: options => options.RoomQueueCapacity = 1);
        var deferred = new OneKillDeferredCloseConnection(905);
        var third = new OneKillConnectionSession(CaptainPlayer(deferred.Id, "third"), deferred);
        var countdownEntered = FinalZoneSignal();
        var releaseCountdown = FinalZoneSignal();
        if (operation == "opening")
            rig.Round.CountdownDelay = (_, ct) =>
            {
                countdownEntered.TrySetResult();
                return releaseCountdown.Task.WaitAsync(ct);
            };
        var entered = FinalZoneSignal();
        var release = FinalZoneSignal();
        Task running = Task.CompletedTask, queued = Task.CompletedTask;
        Task? rejected = null;
        try
        {
            var room = await PrepareFinalZonePublicationRoomAsync(rig, third);
            if (operation is "water" or "drone") ConfigureFinalZonePublicationWorker(room.FinalZone!, operation, 60_000);
            await LoadFinalZonePublicationRoomAsync(rig, room);
            if (operation == "opening") await countdownEntered.Task.WaitAsync(OneKillFailureTimeout);
            else await room.CountdownTask.WaitAsync(OneKillFailureTimeout);
            running = room.Executor.ExecuteAsync(async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); });
            await entered.Task.WaitAsync(OneKillFailureTimeout);
            queued = room.Executor.ExecuteAsync(_ => ValueTask.CompletedTask);
            var stepCalls = 0;
            if (operation == "opening")
            {
                rejected = room.CountdownTask;
                releaseCountdown.TrySetResult();
            }
            else if (operation == "inventory")
                rejected = rig.Round.HandleFinalZoneItemPickupAsync(rig.Host,
                    new(65536, RoomRegistry.SceneEntity(room.FindByGame(rig.Host.Id)!.Slot)), default).AsTask();
            else
            {
                var clock = Assert.IsType<RoomDeadlineClock>(operation == "water" ? room.FinalZoneWaterClock : room.FinalZoneDroneClock);
                rejected = clock.Completion;
                clock.Schedule(0, _ => { stepCalls++; return ValueTask.CompletedTask; });
            }
            await deferred.Entered.Task.WaitAsync(OneKillFailureTimeout);
            Assert.True(room.RoundCancelled);
            Assert.False(rejected.IsCompleted);
            Assert.False(running.IsCompleted);
            Assert.False(queued.IsCompleted);
            Assert.Equal(0, stepCalls);
            Assert.Equal(1, room.FinalZone!.Items!.GroundCount);
            Assert.Equal(1, room.FinalZone.Items.IssuedObjects);
            Assert.Same(room.FinalZone.CleanupTask, room.FinalZoneCleanupTask);
            deferred.Release.TrySetResult();
            if (operation == "inventory")
                await Assert.ThrowsAsync<InvalidOperationException>(() => rejected.WaitAsync(OneKillFailureTimeout));
            else await rejected.WaitAsync(OneKillFailureTimeout);
            Assert.False(running.IsCompleted);
            Assert.False(queued.IsCompleted);
            Assert.Equal(1, deferred.CloseCalls);
            Assert.Empty(rig.Ended.To(rig.Host));
        }
        finally
        {
            releaseCountdown.TrySetResult();
            deferred.Release.TrySetResult();
            release.TrySetResult();
            await Task.WhenAll(running, queued).WaitAsync(OneKillFailureTimeout);
            if (rejected is not null)
                try { await rejected.WaitAsync(OneKillFailureTimeout); }
                catch (InvalidOperationException) { }
            await rig.Registry.StopAllAsync();
        }
    }

    /// <summary>Old opening and worker sends stop after cancellation, shutdown or replacement without advancing their state.</summary>
    [Theory]
    [InlineData("opening", "state")]
    [InlineData("water", "state")]
    [InlineData("drone", "state")]
    [InlineData("opening", "generation")]
    [InlineData("water", "generation")]
    [InlineData("drone", "generation")]
    [InlineData("opening", "cancel")]
    [InlineData("water", "cancel")]
    [InlineData("drone", "cancel")]
    [InlineData("opening", "stop")]
    [InlineData("water", "stop")]
    [InlineData("drone", "stop")]
    public async Task FinalZoneOpeningAndWorkerPostSendFenceOwnership(string operation, string boundary)
    {
        var rig = new Rig();
        var entered = FinalZoneSignal();
        var release = FinalZoneSignal();
        RoomDeadlineClock? clock = null;
        try
        {
            var room = await PrepareFinalZonePublicationRoomAsync(rig);
            var state = room.FinalZone!;
            if (operation != "opening") ConfigureFinalZonePublicationWorker(state, operation, 0);
            var sends = 0;
            async ValueTask BlockAsync()
            {
                sends++;
                entered.TrySetResult();
                await release.Task.WaitAsync(OneKillFailureTimeout);
            }
            if (operation == "opening") rig.Round.FinalZoneItemsSpawned = new FinalZoneAsyncSender<RoundFinalZoneItemsSpawned>((_, _, _) => BlockAsync());
            if (operation == "water") rig.Round.FinalZoneWaterTimer = new FinalZoneAsyncSender<RoundFinalZoneWaterTimerChanged>((_, _, _) => BlockAsync());
            if (operation == "drone") rig.Round.FinalZoneDroneSpawned = new FinalZoneAsyncSender<RoundFinalZoneDroneSpawned>((_, _, _) => BlockAsync());
            await LoadFinalZonePublicationRoomAsync(rig, room);
            await entered.Task.WaitAsync(OneKillFailureTimeout);
            clock = operation == "water" ? room.FinalZoneWaterClock : room.FinalZoneDroneClock;
            lock (room.Gate)
            {
                if (boundary == "generation")
                {
                    room.ResetToWaiting();
                    room.MarkStarting();
                    room.MarkPlaying();
                }
                if (boundary is "state" or "generation") room.FinalZone = new(room.StartGeneration, room.Seats);
                if (boundary == "cancel") room.RoundCancelled = true;
                if (boundary == "stop") room.IsStopping = true;
            }
            release.TrySetResult();
            await room.CountdownTask.WaitAsync(OneKillFailureTimeout);
            if (clock is not null) await clock.Completion.WaitAsync(OneKillFailureTimeout);
            Assert.Equal(1, sends);
            Assert.Equal(boundary == "cancel", room.RoundCancelled);
            Assert.Equal(0, state.WaterPhase);
            if (operation == "opening") Assert.NotNull(state.OpeningItems);
            if (boundary is "state" or "generation")
            {
                Assert.Null(state.CleanupTask);
                Assert.All(room.Seats, seat => Assert.False(seat.Game.Connection.IsClosed));
            }
        }
        finally
        {
            release.TrySetResult();
            clock?.Cancel();
            if (clock is not null) await clock.Completion.WaitAsync(OneKillFailureTimeout);
            await rig.Registry.StopAllAsync();
        }
    }

    private static void ConfigureFinalZonePublicationWorker(FinalZoneRoundState state, string operation, int delayMs)
    {
        if (operation.StartsWith("water", StringComparison.Ordinal))
            state.WaterRules = new(true, delayMs, 60_000, 0, 60_000, 60_000, 60_000, 60_000, 600_000);
        else
        {
            state.DroneRules = new(true, new(delayMs / 1000, delayMs / 1000), new(60, 60));
            state.Supplies = new(new(0, 0), new(0, 0), [], []);
        }
    }
}
