using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.MysteryStore;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Shop.Tests;

public sealed class MysteryStoreFlowTests
{
    [Fact]
    public async Task DetachedOriginDoesNotReceiveCommittedInventoryOrReceipt()
    {
        var session = new Session();
        var player = session.Features.Register<IPlayerSession>(new PlayerSession(new(7, "u", "p", 1)));
        var store = new DelayedStore();
        var replies = new Sender<MysteryStoreReplied>();
        var inventory = new Sender<InventoryReplaced>();
        var scope = new InventoryLoadoutScope("kr-1256", [100385, .. Enumerable.Range(101, 6)]);
        var offers = Enumerable.Range(0, 6).Select(i => new MysteryStoreOffer(2000001 + i, 3, 0, "",
            [new(101 + i, InventoryGrantKind.Count, 1, 99, false)])).ToArray();
        var flow = new MysteryStoreFlow(new() { CoinItemId = 100385 }, scope, offers, new string('a', 64), store,
            replies, inventory, TimeProvider.System, _ => 0, NullLogger.Instance);
        var pending = flow.HandleAsync(session, new(MysteryStoreAction.Purchase, 1, 0, 3, 2000001), default).AsTask();
        player.Dispose();
        store.Result.SetResult(new(MysteryStoreOutcome.Purchased, null, new InventorySnapshot([], []), [], 2000001));
        await pending;
        Assert.Empty(replies.Messages);
        Assert.Empty(inventory.Messages);
    }

    private sealed class DelayedStore : IMysteryStore
    {
        public TaskCompletionSource<MysteryStoreResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<MysteryStoreResult> EnsureAsync(MysteryStoreEnsureCommand command, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<MysteryStoreResult> RefreshAsync(MysteryStoreRefreshCommand command, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<MysteryStoreResult> PurchaseAsync(MysteryStorePurchaseCommand command, CancellationToken ct) => new(Result.Task);
    }
    private sealed class Sender<T> : IMessageSender<T> where T : IMessage
    {
        public string Protocol => "kr-1256/game";
        public List<T> Messages { get; } = [];
        public ValueTask SendAsync(ISession session, T message, CancellationToken ct) { Messages.Add(message); return ValueTask.CompletedTask; }
    }
    private sealed class Session : ISessionWithFeatures
    {
        public ISessionFeatures Features { get; } = new TestSessionFeatures();
        public long Id => 1;
        public IConnection Connection => throw new NotSupportedException();
        public ClientProfile Profile { get; } = new("test", "kr", 1256, 0, 0, "test");
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) => throw new NotSupportedException();
    }
}
