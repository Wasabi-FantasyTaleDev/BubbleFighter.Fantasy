using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Sessions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    /// <summary>A failed opening exchange retains its manifest and awaits cleanup instead of starting its world clocks.</summary>
    [Theory]
    [InlineData("io")]
    [InlineData("disposed")]
    [InlineData("cancelled")]
    [InlineData("closed")]
    public async Task FinalZoneOpeningPublicationFailureAwaitsCleanup(string failure)
    {
        var rig = new Rig();
        var deferred = new OneKillDeferredCloseConnection(903);
        var third = new OneKillConnectionSession(CaptainPlayer(deferred.Id, "third"), deferred);
        try
        {
            var room = await PrepareFinalZonePublicationRoomAsync(rig, third);
            var state = room.FinalZone!;
            var opening = new Sink<RoundFinalZoneItemsSpawned>
            {
                OnSending = (session, _, _) =>
                {
                    if (!ReferenceEquals(session, rig.Guest)) return;
                    throw failure switch
                    {
                        "disposed" => new ObjectDisposedException("opening"),
                        "cancelled" => new OperationCanceledException("opening"),
                        _ => new IOException("opening"),
                    };
                },
            };
            rig.Round.FinalZoneItemsSpawned = opening;
            if (failure == "closed")
                rig.Round.RoundPlaying = (_, _) => rig.Guest.Connection.CloseAsync("closed before opening").GetAwaiter().GetResult();
            await LoadFinalZonePublicationRoomAsync(rig, room);
            await deferred.Entered.Task.WaitAsync(OneKillFailureTimeout);
            Assert.True(room.RoundCancelled);
            Assert.NotNull(state.OpeningItems);
            Assert.Null(room.FinalZoneWaterClock);
            Assert.Null(room.FinalZoneDroneClock);
            Assert.Empty(opening.To(rig.Guest));
            Assert.Equal(failure == "closed" ? 0 : 1, opening.To(rig.Host).Length);
            Assert.False(room.CountdownTask.IsCompleted);
            Assert.Same(state.CleanupTask, room.FinalZoneCleanupTask);
            deferred.Release.TrySetResult();
            await room.CountdownTask.WaitAsync(OneKillFailureTimeout);
            Assert.Equal(1, deferred.CloseCalls);
            Assert.Empty(rig.Ended.To(rig.Host));
        }
        finally
        {
            deferred.Release.TrySetResult();
            await rig.Registry.StopAllAsync();
        }
    }

    /// <summary>Every core inventory transfer cancels its round after an accepted mutation reaches only one recipient.</summary>
    [Theory]
    [InlineData("pickup")]
    [InlineData("drop")]
    [InlineData("backpack-drop")]
    [InlineData("backpack-pickup")]
    public async Task FinalZoneInventoryPublicationFailureCancelsWithoutReplay(string operation)
    {
        var rig = new Rig();
        try
        {
            var room = await PrepareFinalZonePublicationRoomAsync(rig, CaptainPlayer(33, "third"));
            await LoadFinalZonePublicationRoomAsync(rig, room);
            await room.CountdownTask.WaitAsync(OneKillFailureTimeout);
            var state = room.FinalZone!;
            var ledger = state.Items!;
            var host = room.FindByGame(rig.Host.Id)!;
            var actor = RoomRegistry.SceneEntity(host.Slot);
            var sends = 0;
            void FailSecond(ISession session)
            {
                sends++;
                if (ReferenceEquals(session, rig.Guest)) throw new IOException("inventory publication");
            }
            rig.Round.FinalZoneItemPicked = new Sink<RoundFinalZoneItemPicked> { OnSending = (session, _, _) => FailSecond(session) };
            rig.Round.FinalZoneItemsSpawned = new Sink<RoundFinalZoneItemsSpawned> { OnSending = (session, _, _) => FailSecond(session) };
            rig.Round.FinalZoneBackpackSpawned = new Sink<RoundFinalZoneBackpackSpawned> { OnSending = (session, _, _) => FailSecond(session) };
            rig.Round.FinalZoneBackpackPicked = new Sink<RoundFinalZoneBackpackPicked> { OnSending = (session, _, _) => FailSecond(session) };
            if (operation is "drop" or "backpack-drop") Assert.True(ledger.TryPickUp(actor, 65536));
            if (operation == "backpack-drop") host.Life.Die();
            var backpackId = operation == "backpack-pickup"
                ? ledger.TryCreateSupply([new(9001, -1, 2, new([], [])), new(9001, -1, 3, new([], []))])!.Value : 0;
            Task ApplyAsync() => operation switch
            {
                "pickup" => rig.Round.HandleFinalZoneItemPickupAsync(rig.Host, new(65536, actor), default).AsTask(),
                "drop" => rig.Round.HandleFinalZoneItemsDroppedAsync(rig.Host, new([FinalZoneFixtureItem(0, 5)]), default).AsTask(),
                "backpack-drop" => rig.Round.HandleFinalZoneBackpackDroppedAsync(rig.Host,
                    new(actor, 1, 2, 3, 0, 0, 0, "", [new(9001, -1, 9, new([], []))]), default).AsTask(),
                _ => rig.Round.HandleFinalZoneBackpackPickupAsync(rig.Host, new(backpackId, 9001), default).AsTask(),
            };
            await Assert.ThrowsAsync<IOException>(ApplyAsync);
            Assert.True(room.RoundCancelled);
            Assert.True(room.FinalZoneCleanupTask.IsCompletedSuccessfully);
            Assert.Equal(2, sends);
            await ApplyAsync();
            Assert.Equal(2, sends);
            if (operation == "pickup") Assert.Equal(12, Assert.Single(ledger.Held(actor)).Quantity);
            if (operation == "drop") Assert.Equal(7, Assert.Single(ledger.Held(actor)).Quantity);
            if (operation == "backpack-drop") Assert.Empty(ledger.Held(actor));
            if (operation == "backpack-pickup")
            {
                Assert.Equal(2, Assert.Single(ledger.Held(actor)).Quantity);
                Assert.Equal(1, ledger.BackpackCount);
            }
            Assert.Empty(rig.Ended.To(rig.Host));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A missing game recipient is detected before the ground item moves into held inventory.</summary>
    [Fact]
    public async Task FinalZoneInventoryClosedRecipientPreventsMutation()
    {
        var rig = new Rig();
        try
        {
            var room = await PrepareFinalZonePublicationRoomAsync(rig);
            await LoadFinalZonePublicationRoomAsync(rig, room);
            await room.CountdownTask.WaitAsync(OneKillFailureTimeout);
            var actor = RoomRegistry.SceneEntity(room.FindByGame(rig.Host.Id)!.Slot);
            await rig.Guest.Connection.CloseAsync("missing recipient");
            await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Round.HandleFinalZoneItemPickupAsync(
                rig.Host, new(65536, actor), default).AsTask());
            Assert.Equal(1, room.FinalZone!.Items!.GroundCount);
            Assert.Empty(room.FinalZone.Items.Held(actor));
            Assert.True(room.RoundCancelled);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Queued inventory work cannot acquire a replacement state, ledger, actor life or game session.</summary>
    [Theory]
    [InlineData("state", false)]
    [InlineData("ledger", false)]
    [InlineData("life", false)]
    [InlineData("seat", false)]
    [InlineData("state", true)]
    [InlineData("ledger", true)]
    [InlineData("life", true)]
    public async Task FinalZoneQueuedInventoryPreservesReplacement(string replacement, bool cancel)
    {
        var rig = new Rig();
        var entered = FinalZoneSignal();
        var release = FinalZoneSignal();
        using var cancellation = new CancellationTokenSource();
        Task running = Task.CompletedTask;
        try
        {
            var room = await PrepareFinalZonePublicationRoomAsync(rig);
            await LoadFinalZonePublicationRoomAsync(rig, room);
            await room.CountdownTask.WaitAsync(OneKillFailureTimeout);
            var state = room.FinalZone!;
            var ledger = state.Items!;
            var host = room.FindByGame(rig.Host.Id)!;
            var actor = RoomRegistry.SceneEntity(host.Slot);
            running = room.Executor.ExecuteAsync(async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); });
            await entered.Task.WaitAsync(OneKillFailureTimeout);
            var pending = rig.Round.HandleFinalZoneItemPickupAsync(rig.Host, new(65536, actor), cancellation.Token).AsTask();
            lock (room.Gate)
            {
                if (replacement == "state") room.FinalZone = new(room.StartGeneration, room.Seats) { Items = ledger };
                if (replacement == "ledger") state.Items = FinalZoneFixtureLedger(actor);
                if (replacement == "life") host.Life.Reset();
                if (replacement == "seat")
                {
                    room.RemoveSession(host.Game.Id);
                    room.Add(CaptainPlayer(host.Game.Id, "replacement"), host.Key, host.UserNum, "replacement");
                }
            }
            if (cancel)
            {
                await cancellation.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(OneKillFailureTimeout));
            }
            release.TrySetResult();
            await running.WaitAsync(OneKillFailureTimeout);
            if (!cancel) await pending.WaitAsync(OneKillFailureTimeout);
            Assert.Equal(1, ledger.GroundCount);
            Assert.Empty(ledger.Held(actor));
            Assert.Null(state.CleanupTask);
            Assert.False(room.RoundCancelled);
            Assert.All(room.Seats, seat => Assert.False(seat.Game.Connection.IsClosed));
        }
        finally
        {
            release.TrySetResult();
            await running.WaitAsync(OneKillFailureTimeout);
            await rig.Registry.StopAllAsync();
        }
    }

    /// <summary>A completed send after cancellation or ownership loss cannot publish the remaining inventory recipients.</summary>
    [Theory]
    [InlineData("cancel")]
    [InlineData("stop")]
    [InlineData("token")]
    [InlineData("state")]
    [InlineData("ledger")]
    [InlineData("life")]
    public async Task FinalZoneInventoryPostSendRevalidatesOwnership(string boundary)
    {
        var rig = new Rig();
        var entered = FinalZoneSignal();
        var release = FinalZoneSignal();
        using var cancellation = new CancellationTokenSource();
        Task? pending = null;
        try
        {
            var room = await PrepareFinalZonePublicationRoomAsync(rig);
            await LoadFinalZonePublicationRoomAsync(rig, room);
            await room.CountdownTask.WaitAsync(OneKillFailureTimeout);
            var state = room.FinalZone!;
            var host = room.FindByGame(rig.Host.Id)!;
            var actor = RoomRegistry.SceneEntity(host.Slot);
            var sends = 0;
            rig.Round.FinalZoneItemPicked = new FinalZoneAsyncSender<RoundFinalZoneItemPicked>(async (_, _, _) =>
            {
                sends++;
                entered.TrySetResult();
                await release.Task.WaitAsync(OneKillFailureTimeout);
            });
            pending = rig.Round.HandleFinalZoneItemPickupAsync(rig.Host, new(65536, actor), cancellation.Token).AsTask();
            await entered.Task.WaitAsync(OneKillFailureTimeout);
            lock (room.Gate)
            {
                if (boundary == "cancel") room.RoundCancelled = true;
                if (boundary == "stop") room.IsStopping = true;
                if (boundary == "state") room.FinalZone = new(room.StartGeneration, room.Seats);
                if (boundary == "ledger") state.Items = FinalZoneFixtureLedger(actor);
                if (boundary == "life") host.Life.Reset();
            }
            if (boundary == "token") await cancellation.CancelAsync();
            release.TrySetResult();
            if (boundary == "token")
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(OneKillFailureTimeout));
            else await Assert.ThrowsAsync<InvalidOperationException>(() => pending.WaitAsync(OneKillFailureTimeout));
            Assert.Equal(1, sends);
            if (boundary is "state" or "ledger")
            {
                Assert.Null(state.CleanupTask);
                Assert.False(room.RoundCancelled);
                Assert.False(rig.Guest.Connection.IsClosed);
            }
            if (boundary is "life" or "token")
            {
                Assert.True(room.RoundCancelled);
                Assert.NotNull(state.CleanupTask);
                Assert.True(rig.Guest.Connection.IsClosed);
            }
        }
        finally
        {
            release.TrySetResult();
            if (pending is not null)
                try { await pending.WaitAsync(OneKillFailureTimeout); }
                catch (InvalidOperationException) { }
                catch (OperationCanceledException) { }
            await rig.Registry.StopAllAsync();
        }
    }

    private static async Task<PlayRoom> PrepareFinalZonePublicationRoomAsync(Rig rig, params ISession[] extra)
    {
        rig.Round.FinalZoneEliminated = new Sink<RoundFinalZonePlayerEliminated>();
        rig.Round.FinalZoneItemsSpawned = new Sink<RoundFinalZoneItemsSpawned>();
        rig.Round.FinalZoneItemPicked = new Sink<RoundFinalZoneItemPicked>();
        await rig.Rooms.HandleCreateAsync(rig.Host, new RoomCreateRequested("FinalZone", "", (int)GameModeId.Area32, "map"), default);
        var room = rig.Registry.Find(rig.Entered.To(rig.Host)[0].Room.RoomId)!;
        foreach (var member in extra.Prepend(rig.Guest))
        {
            await rig.Rooms.HandleJoinAsync(member, new(room.Id, ""), default);
            await rig.Rooms.HandleReadyAsync(member, new(true), default);
        }
        await rig.Rooms.HandleStartAsync(rig.Host, new("map"), default);
        var ledger = FinalZoneFixtureLedger(room.Seats.Select(seat => RoomRegistry.SceneEntity(seat.Slot)).ToArray());
        var manifest = new[] { FinalZoneFixtureItem(65536, 12) };
        ledger.Initialize(manifest);
        room.FinalZone = new(room.StartGeneration, room.Seats) { Items = ledger, OpeningItems = manifest };
        return room;
    }

    private static async Task LoadFinalZonePublicationRoomAsync(Rig rig, PlayRoom room)
    {
        foreach (var seat in room.Seats.ToArray()) await rig.Round.HandleLoadReportedAsync(seat.Game, new(), default);
    }

    private static TaskCompletionSource FinalZoneSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class FinalZoneAsyncSender<T>(Func<ISession, T, CancellationToken, ValueTask> send) : IMessageSender<T> where T : IMessage
    {
        /// <summary>Identifies the game connection used by the round publication fixture.</summary>
        public string Protocol => "test/game";
        /// <summary>Invokes the configured asynchronous publication step with the supplied cancellation token.</summary>
        public ValueTask SendAsync(ISession session, T message, CancellationToken ct) => send(session, message, ct);
    }
}
