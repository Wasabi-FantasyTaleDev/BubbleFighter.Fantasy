using BubbleFighter.Fantasy.Contracts.Match;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    /// <summary>A native box requested once while full can be collided with again after an accepted use.</summary>
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public async Task FullSlotBoxIsRecreatedWithoutPickupCredit(bool mapPoint, int capacity)
    {
        var rig = new Rig(randomSupplyItemKinds: [101, 109, 112], nativeItemSlotCount: capacity,
            configure: options => options.RenewFullSlotSupplies = true);
        try
        {
            await rig.StartAsync();
            if (mapPoint)
                await rig.Round.HandleSuppliesReportedAsync(rig.Host, new([new(1, 2, 3, 112, 10, 10, 1)]), default);
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            RoundSupplyCreated? waiting = null;
            if (mapPoint)
            {
                waiting = (await WaitSuppliesAsync(rig, rig.Host, 1))[0];
                var room = rig.Registry.Find(rig.RoomId)!;
                lock (room.Gate) room.Supplies!.Points[0].NextAt = long.MaxValue;
            }
            foreach (var kind in new[] { 101, 109 }.Take(capacity))
            {
                await rig.Round.HandleSupplyDropAsync(rig.Guest, new(new(10, 20, 30, kind, 0, 0), 10000), default);
                await rig.Round.HandleSupplyPickupAsync(rig.Host, new(rig.SupplyCreated.To(rig.Host).Last().ObjectId, kind), default);
            }
            if (waiting is null)
            {
                await rig.Round.HandleSupplyDropAsync(rig.Guest, new(new(1, 2, 3, 112, 0, 0, 1), 20000), default);
                waiting = rig.SupplyCreated.To(rig.Host).Last();
            }
            // cn-279 sub_89DD80 and kr-812 sub_6378A0 latch each box before the first pickup request.
            HashSet<int> attempted = [];
            async Task Collide(int objectId)
            {
                if (attempted.Add(objectId))
                    await rig.Round.HandleSupplyPickupAsync(rig.Host, new(objectId, 112), default);
            }
            await Collide(waiting.ObjectId);
            await Collide(waiting.ObjectId);
            Assert.Equal(capacity, rig.SupplyPicked.To(rig.Host).Length);
            var createdBefore = rig.SupplyCreated.To(rig.Host).Length;
            await rig.Round.HandleItemUseAsync(rig.Host, new(), default);
            var refreshed = Assert.Single(rig.SupplyCreated.To(rig.Host).Skip(createdBefore));
            Assert.NotEqual(waiting.ObjectId, refreshed.ObjectId);
            Assert.Equal(waiting.Point, refreshed.Point);
            Assert.Equal(waiting.CreationClass, refreshed.CreationClass);
            foreach (var member in new[] { rig.Host, rig.Guest })
            {
                var removed = Assert.Single(rig.SupplyPicked.To(member), value => value.Seat == -1);
                Assert.Equal(new RoundSupplyPicked(-1, waiting.ObjectId), removed);
                Assert.Equal(refreshed, rig.SupplyCreated.To(member).Last());
            }
            await Collide(waiting.ObjectId);
            Assert.Equal(capacity, rig.SupplyPicked.To(rig.Host).Count(value => value.Seat >= 0));
            await Collide(refreshed.ObjectId);
            Assert.Equal(new RoundSupplyPicked(0, refreshed.ObjectId) { ItemKind = 112, ItemSlot = 0 },
                rig.SupplyPicked.To(rig.Host).Last());
            await rig.Round.HandleSupplyPickupAsync(rig.Guest, new(waiting.ObjectId, 112), default);
            await rig.Round.HandleSupplyPickupAsync(rig.Guest, new(refreshed.ObjectId, 112), default);
            Assert.Equal(capacity + 1, rig.SupplyPicked.To(rig.Host).Count(value => value.Seat >= 0));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A box consumed by another player while the first player's slots are full is never recreated.</summary>
    [Fact]
    public async Task OtherPlayerConsumptionCancelsTheFullSlotRetry()
    {
        var rig = new Rig(randomSupplyItemKinds: [101], nativeItemSlotCount: 2,
            configure: options => options.RenewFullSlotSupplies = true);
        try
        {
            await rig.StartAsync();
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            for (var index = 0; index < 3; index++)
            {
                await rig.Round.HandleSupplyDropAsync(rig.Guest, new(new(1, 2, 3, 101, 0, 0), 10000), default);
                await rig.Round.HandleSupplyPickupAsync(rig.Host, new(rig.SupplyCreated.To(rig.Host).Last().ObjectId, 101), default);
            }
            var waiting = rig.SupplyCreated.To(rig.Host).Last();
            await rig.Round.HandleSupplyPickupAsync(rig.Guest, new(waiting.ObjectId, 101), default);
            await rig.Round.HandleItemUseAsync(rig.Host, new(), default);
            Assert.Equal(3, rig.SupplyCreated.To(rig.Host).Length);
            Assert.DoesNotContain(rig.SupplyPicked.To(rig.Host), item => item.Seat == -1);
            Assert.Empty(rig.Registry.Find(rig.RoomId)!.Supplies!.FullSlotRefusals);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Clearing held items on death also releases one-shot boxes without requiring another item-use request.</summary>
    [Fact]
    public async Task DeathReleasesPendingBoxesThroughTheRoundWorker()
    {
        var rig = new Rig(randomSupplyItemKinds: [101], nativeItemSlotCount: 2,
            configure: options => options.RenewFullSlotSupplies = true);
        try
        {
            await rig.StartAsync();
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            for (var index = 0; index < 3; index++)
            {
                await rig.Round.HandleSupplyDropAsync(rig.Guest, new(new(1, 2, 3, 101, 0, 0), 10000), default);
                await rig.Round.HandleSupplyPickupAsync(rig.Host, new(rig.SupplyCreated.To(rig.Host).Last().ObjectId, 101), default);
            }
            var waiting = rig.SupplyCreated.To(rig.Host).Last();
            await rig.Round.HandleDeathReportedAsync(rig.Host, new(1000000, 0) { ActorSeat = 0 }, default);
            var created = await WaitSuppliesAsync(rig, rig.Host, 4);
            Assert.Equal(4, created.Length);
            Assert.NotEqual(waiting.ObjectId, created.Last().ObjectId);
            Assert.Equal(waiting.Point, created.Last().Point);
            Assert.Contains(new RoundSupplyPicked(-1, waiting.ObjectId), rig.SupplyPicked.To(rig.Host));
            Assert.Equal(2, rig.SupplyPicked.To(rig.Host).Count(item => item.Seat >= 0));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A full inventory cannot retain forged object ids, mismatched kinds or opposing-team boxes.</summary>
    [Fact]
    public async Task IneligibleObjectsNeverEnterFullSlotRetryState()
    {
        var rig = new Rig(randomSupplyItemKinds: [101, 109], nativeItemSlotCount: 2,
            configure: options => { options.RenewFullSlotSupplies = true; options.ItemModeIds = [3]; options.ItemModeKinds = [101, 109]; });
        try
        {
            await rig.StartAsync();
            var room = rig.Registry.Find(rig.RoomId)!;
            lock (room.Gate)
            {
                room.ModeId = 3;
                room.FindByGame(rig.Host.Id)!.Team = 1;
            }
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            for (var index = 0; index < 2; index++)
            {
                await rig.Round.HandleSupplyDropAsync(rig.Guest, new(new(1, 2, 3, 101, 0, 0), 10000), default);
                await rig.Round.HandleSupplyPickupAsync(rig.Host, new(rig.SupplyCreated.To(rig.Host).Last().ObjectId, 101), default);
            }
            await rig.Round.HandleSupplyDropAsync(rig.Guest, new(new(1, 2, 3, 109, 0, 0, 2), 20000), default);
            var opposing = rig.SupplyCreated.To(rig.Host).Last();
            await rig.Round.HandleSupplyPickupAsync(rig.Host, new(opposing.ObjectId, 109), default);
            await rig.Round.HandleSupplyPickupAsync(rig.Host, new(int.MaxValue, 101), default);
            await rig.Round.HandleSupplyPickupAsync(rig.Host, new(opposing.ObjectId, 101), default);
            await rig.Round.HandleItemUseAsync(rig.Host, new(), default);
            Assert.Empty(room.Supplies!.FullSlotRefusals);
            Assert.Equal(3, rig.SupplyCreated.To(rig.Host).Length);
            Assert.DoesNotContain(rig.SupplyPicked.To(rig.Host), item => item.Seat == -1);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }
}
