using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Persistence.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    /// <summary>Even a pending server-timed death cannot spend a revival item after its mode explicitly denies respawn.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitRespawnDenialDoesNotConsumeAnEnabledRevivalItem(bool denied)
    {
        var services = new ServiceRegistry();
        var context = new PluginContext(new PluginRow { Id = "revival-policy", Plugin = "test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        var inventory = new MemoryInventoryRepository(new MemoryPlayerTable(2));
        var push = new RevivalInventoryPush();
        context.Effect(services.Register<IInventoryRepository>(inventory));
        context.Effect(services.Register<IInventoryPushService>(push));
        var rig = new Rig(resultContext: context, observation: true, configure: options =>
        {
            options.RespawnDisabledModeIds = denied ? [4] : [];
            options.InstantRevivalItemId = 500;
        });
        try
        {
            await rig.StartAsync();
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Guest);
            var room = rig.Registry.Find(rig.RoomId)!;
            var seat = room.FindByGame(rig.Guest.Id)!;
            await inventory.ReplaceSlotsAsync(seat.UserNum, InventoryBag.CharacterBag,
                [new(seat.UserNum, InventoryBag.CharacterBag, 1, 500, 2, 0)], default);
            lock (room.Gate) seat.Life.RecordDeath(1_100_000, true);
            await rig.Round.HandleInstantRevivalAsync(rig.Guest, new(1_100_000, 1), default);
            var item = Assert.Single(await inventory.ListSlotsAsync(seat.UserNum, InventoryBag.CharacterBag, 2, default));
            Assert.Equal(denied ? 2 : 1, item.Quantity);
            Assert.Equal(denied ? 0 : 1, push.Count);
            Assert.Equal(denied ? 0 : 1, rig.SpawnOffered.To(rig.Guest).Length);
            Assert.Equal(denied ? SeatLife.Dead : SeatLife.ResetPending, seat.Life.State);
        }
        finally
        {
            await rig.Registry.StopAllAsync();
            await context.UnwindAsync();
        }
    }

    private sealed class RevivalInventoryPush : IInventoryPushService
    {
        internal int Count { get; private set; }

        /// <summary>Counts the full inventory refresh following a consumed revival item.</summary>
        public ValueTask PushInventoryAsync(ISession session, long userNum, CancellationToken ct)
        {
            Count++;
            return ValueTask.CompletedTask;
        }

        /// <summary>Rejects a purchase refresh, which is outside the revival path.</summary>
        public ValueTask PushPurchaseAsync(ISession session, long userNum, IReadOnlyList<InventorySlotRecord> grants, CancellationToken ct) =>
            throw new InvalidOperationException("Revival cannot publish a purchase.");

        /// <summary>Rejects a partial refresh where revival requires the inventory snapshot.</summary>
        public ValueTask PushSlotUpdateAsync(ISession session, InventorySlotRecord slot, CancellationToken ct) =>
            throw new InvalidOperationException("Revival publishes a full inventory snapshot.");
    }
}
