using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Persistence.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Lobby.Tests;

/// <summary>Separates targeted removal availability from automatic expiry policy and checks activation cleanup.</summary>
public sealed class InventoryRemovalRegistrationTests
{
    [Theory]
    [InlineData("mysql", false)]
    [InlineData("mysql", true)]
    [InlineData("reference", false)]
    public async Task RemovalAndSweepHaveIndependentPublicationPolicies(string source, bool automatic)
    {
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        var store = new MemoryInventoryRepository(new MemoryPlayerTable(4));
        using var inventory = services.Register<IInventoryRepository>(store);
        using var binding = services.RegisterMany<IInboundMessageBinding<LoginRequested>>(new Wire<LoginRequested>());
        using var success = services.RegisterMany<IMessageSender<LoginSucceeded>>(new Wire<LoginSucceeded>());
        using var rejected = services.RegisterMany<IMessageSender<LoginRejected>>(new Wire<LoginRejected>());
        var router = Context("core.message-router");
        var lobby = Context("lobby", new()
        {
            ["protocol"] = "inventory-removal/game",
            ["inventory_source"] = source,
            ["expire_items"] = automatic,
        });
        try
        {
            await new MessageRouterPlugin().ActivateAsync(router, default);
            await new LobbyPlugin().ActivateAsync(lobby, default);
            Assert.Equal(source == "mysql", services.TryGet<IInventoryItemRemoval>(out var removal));
            Assert.Equal(automatic, services.TryGet<IInventoryExpiry>(out var sweep));
            if (source == "mysql")
            {
                Assert.NotNull(removal);
                if (automatic) Assert.Same(removal, sweep);
                foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
                    await store.ReplaceSlotsAsync(42, bag,
                    [
                        new(42, bag, 1, 202003, 1, 0) { Lifetime = InventoryLifetime.Until(DateTimeOffset.UtcNow.AddDays(-1)) },
                        new(42, bag, 2, 202004, 1, 0) { Lifetime = InventoryLifetime.Until(DateTimeOffset.UtcNow.AddDays(-1)) },
                    ], default);
                Assert.Equal(2, (await removal.RemoveItemAsync(42, 202003, default))!.Count);
                foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
                    Assert.Equal(202004, Assert.Single(await store.ListSlotsAsync(42, bag, 512, default)).ItemId);
            }
        }
        finally
        {
            await lobby.UnwindAsync();
            await router.UnwindAsync();
        }
        Assert.False(services.TryGet<IInventoryItemRemoval>(out _));
        Assert.False(services.TryGet<IInventoryExpiry>(out _));

        PluginContext Context(string id, Dictionary<string, object?>? config = null) =>
            new(new PluginRow { Id = id, Plugin = id, Source = "inventory-removal-test", Config = config },
                services, events, NullLogger.Instance);
    }

    private sealed class Wire<T> : IInboundMessageBinding<T>, IMessageSender<T> where T : IMessage
    {
        public string Protocol => "inventory-removal/game";
        public ValueTask SendAsync(ISession session, T message, CancellationToken ct) =>
            throw new InvalidOperationException("This registration check must not send a packet.");
    }
}
