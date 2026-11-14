using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Sessions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    /// <summary>Deferred cleanup checks its original ledger before closing another connection after a replacement.</summary>
    [Fact]
    public async Task FinalZoneInventoryCleanupPreservesReplacementLedgerConnections()
    {
        var rig = new Rig();
        var deferred = new OneKillDeferredCloseConnection(908);
        Task? failed = null;
        try
        {
            var room = await PrepareFinalZonePublicationRoomAsync(rig);
            await LoadFinalZonePublicationRoomAsync(rig, room);
            await room.CountdownTask.WaitAsync(OneKillFailureTimeout);
            var host = room.FindByGame(rig.Host.Id)!;
            rig.Registry.BindRelay(new OneKillConnectionSession(new RecordingSession(deferred.Id), deferred), host.Key);
            rig.Round.FinalZoneItemPicked = new Sink<RoundFinalZoneItemPicked>
            {
                OnSending = (session, _, _) =>
                {
                    if (ReferenceEquals(session, rig.Guest)) throw new IOException("inventory publication");
                },
            };
            failed = rig.Round.HandleFinalZoneItemPickupAsync(rig.Host, new(65536, RoomRegistry.SceneEntity(host.Slot)), default).AsTask();
            await deferred.Entered.Task.WaitAsync(OneKillFailureTimeout);
            lock (room.Gate)
                room.FinalZone!.Items = FinalZoneFixtureLedger(RoomRegistry.SceneEntity(host.Slot));
            deferred.Release.TrySetResult();
            await Assert.ThrowsAsync<IOException>(() => failed.WaitAsync(OneKillFailureTimeout));
            Assert.False(rig.Guest.Connection.IsClosed);
            Assert.Equal(1, deferred.CloseCalls);
        }
        finally
        {
            deferred.Release.TrySetResult();
            if (failed is not null)
                try { await failed.WaitAsync(OneKillFailureTimeout); }
                catch (IOException) { }
            await rig.Registry.StopAllAsync();
        }
    }

    /// <summary>Supply and trap publications use the same guarded ledger transition for game and admitted relay requests.</summary>
    [Theory]
    [InlineData("drone-box")]
    [InlineData("golden")]
    [InlineData("trap-place")]
    [InlineData("trap-link-game")]
    [InlineData("trap-link-relay")]
    public async Task FinalZoneSupplyAndTrapFailureCancelsAcceptedTransition(string operation)
    {
        var rig = new Rig();
        try
        {
            var room = await PrepareFinalZonePublicationRoomAsync(rig);
            var state = room.FinalZone!;
            var host = room.FindByGame(rig.Host.Id)!;
            var actor = RoomRegistry.SceneEntity(host.Slot);
            var ledger = new FinalZoneItemLedger(new Dictionary<int, FinalZoneItemSpec>
            {
                [560] = new("Rod", 560, "Weapon", "Rod", 5921, true, true, true),
            }, room.Seats.Select(seat => RoomRegistry.SceneEntity(seat.Slot)), 65536, 32, 999);
            ledger.Initialize([], [new(560, 5921, 2, new([], []))]);
            state.Items = ledger;
            state.OpeningItems = [];
            state.Supplies = new(new(0, 0), new(0, 0), [], []);
            await LoadFinalZonePublicationRoomAsync(rig, room);
            await room.CountdownTask.WaitAsync(OneKillFailureTimeout);
            var sends = 0;
            void FailSecond(ISession session)
            {
                sends++;
                if (ReferenceEquals(session, rig.Guest)) throw new IOException("supply or trap publication");
            }
            rig.Round.FinalZoneDroneBoxSpawned = new Sink<RoundFinalZoneDroneBoxSpawned> { OnSending = (session, _, _) => FailSecond(session) };
            rig.Round.FinalZoneGoldenSupplySpawned = new Sink<RoundFinalZoneGoldenSupplySpawned> { OnSending = (session, _, _) => FailSecond(session) };
            rig.Round.FinalZoneTrapSpawned = new Sink<RoundFinalZoneTrapSpawned> { OnSending = (session, _, _) => FailSecond(session) };
            rig.Round.FinalZoneTrapLinked = new Sink<RoundFinalZoneTrapLinked> { OnSending = (session, _, _) => FailSecond(session) };
            var droneId = operation == "drone-box" ? ledger.TryCreateDrone()!.Value : 0;
            RoundFinalZoneTrapLinked? link = null;
            ISession requester = rig.Host;
            if (operation.StartsWith("trap-link", StringComparison.Ordinal))
            {
                var first = ledger.TryPlaceTrap(new(actor, 560, 1, 1, 2, 3))!;
                var second = ledger.TryPlaceTrap(new(actor, 560, 3, 4, 5, 6))!;
                link = new(actor, first.ObjectId, second.ObjectId, 5921, []);
                if (operation == "trap-link-relay")
                {
                    requester = new RecordingSession(906);
                    rig.Registry.BindRelay(requester, host.Key);
                }
            }
            await Assert.ThrowsAsync<IOException>(() => operation switch
            {
                "drone-box" => rig.Round.HandleFinalZoneDroneDestroyedAsync(rig.Host, new(droneId, 1, 2, 3, 0), default).AsTask(),
                "golden" => rig.Round.HandleFinalZoneGoldenSupplyAsync(rig.Host, new(1, 1, 2, 3), default).AsTask(),
                "trap-place" => rig.Round.HandleFinalZoneTrapRequestedAsync(rig.Host, new(actor, 560, 1, 1, 2, 3), default).AsTask(),
                _ => rig.Round.HandleFinalZoneTrapLinkedAsync(requester, link!, default).AsTask(),
            });
            Assert.Equal(2, sends);
            Assert.True(room.RoundCancelled);
            Assert.True(room.FinalZoneCleanupTask.IsCompletedSuccessfully);
            if (operation == "drone-box")
            {
                Assert.Equal(0, ledger.DroneCount);
                Assert.Equal(1, ledger.BackpackCount);
                Assert.Equal(2, ledger.IssuedObjects);
            }
            if (operation == "golden") Assert.Contains(1, state.GoldenSupplyIndexes);
            if (operation == "trap-place") Assert.Equal(1, Assert.Single(ledger.Held(actor)).Quantity);
            if (link is not null) Assert.False(ledger.TryLinkTrap(link));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A real nonterminal cleanup remains the start admission barrier even after the room returns to waiting.</summary>
    [Fact]
    public async Task FinalZoneInventoryCleanupBlocksNextStartUntilClosed()
    {
        var rig = new Rig();
        var deferred = new OneKillDeferredCloseConnection(907);
        var third = new OneKillConnectionSession(CaptainPlayer(deferred.Id, "third"), deferred);
        Task? failed = null;
        try
        {
            var room = await PrepareFinalZonePublicationRoomAsync(rig, third);
            await LoadFinalZonePublicationRoomAsync(rig, room);
            await room.CountdownTask.WaitAsync(OneKillFailureTimeout);
            var state = room.FinalZone!;
            var actor = RoomRegistry.SceneEntity(room.FindByGame(rig.Host.Id)!.Slot);
            rig.Round.FinalZoneItemPicked = new Sink<RoundFinalZoneItemPicked>
            {
                OnSending = (session, _, _) =>
                {
                    if (ReferenceEquals(session, rig.Guest)) throw new IOException("inventory publication");
                },
            };
            failed = rig.Round.HandleFinalZoneItemPickupAsync(rig.Host, new(65536, actor), default).AsTask();
            await deferred.Entered.Task.WaitAsync(OneKillFailureTimeout);
            var cleanup = room.FinalZoneCleanupTask;
            await room.Executor.ExecuteAsync(_ => { room.ResetToWaiting(); return ValueTask.CompletedTask; });
            foreach (var seat in room.Seats.Where(seat => seat.Slot != room.MasterSlot))
                await rig.Rooms.HandleReadyAsync(seat.Game, new(true), default);
            rig.Rooms.ValidateRoundMap = rig.Round.ValidateModeMap;
            var generation = room.StartGeneration;
            var starts = rig.Starting.To(rig.Host).Length;
            Assert.True(room.CanStart);
            await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Rooms.HandleStartAsync(rig.Host, new("map"), default).AsTask());
            Assert.Equal(generation, room.StartGeneration);
            Assert.Equal(starts, rig.Starting.To(rig.Host).Length);
            Assert.Same(cleanup, state.CleanupTask);
            Assert.Same(cleanup, room.FinalZoneCleanupTask);
            Assert.False(failed.IsCompleted);
            deferred.Release.TrySetResult();
            await Assert.ThrowsAsync<IOException>(() => failed.WaitAsync(OneKillFailureTimeout));
            await rig.Rooms.HandleStartAsync(rig.Host, new("map"), default);
            Assert.Equal(generation + 1, room.StartGeneration);
            Assert.Equal(starts + 1, rig.Starting.To(rig.Host).Length);
            Assert.Equal(1, deferred.CloseCalls);
        }
        finally
        {
            deferred.Release.TrySetResult();
            if (failed is not null)
                try { await failed.WaitAsync(OneKillFailureTimeout); }
                catch (IOException) { }
            await rig.Registry.StopAllAsync();
        }
    }
}
