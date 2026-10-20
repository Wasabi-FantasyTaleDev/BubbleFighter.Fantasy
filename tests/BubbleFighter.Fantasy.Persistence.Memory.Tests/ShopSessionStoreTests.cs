using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Persistence.Memory;
using BubbleFighter.Fantasy.Persistence.Tests;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Memory.Tests;

public sealed class ShopSessionStoreTests : ShopSessionStoreContractGuardTests
{
    protected override IShopSessionStore CreateStore() => new MemoryShopSessionStore(new MemoryPlayerTable(4));

    [Theory]
    [InlineData(0, 0, ShopKind.ItemShop)]
    [InlineData(long.MinValue, int.MinValue, ShopKind.ItemShop)]
    [InlineData(long.MaxValue, int.MaxValue, ShopKind.CashShop)]
    public async Task OpensRoundTripAndReplacementPreservesOtherPlayers(long token, int shopType, ShopKind kind)
    {
        var store = CreateStore();
        var other = new ShopOpenRecord(2, 12, 4, ShopKind.ItemShop);
        await store.RememberOpenAsync(other, default);
        await store.RememberOpenAsync(new(1, 0, 0, ShopKind.CashShop), default);
        var updated = new ShopOpenRecord(1, token, shopType, kind);
        await store.RememberOpenAsync(updated, default);
        Assert.Equal(updated, await store.FindLastOpenAsync(1, default));
        Assert.Equal(other, await store.FindLastOpenAsync(2, default));
    }

    [Fact]
    public async Task ReadsAndAnonymousOpensDoNotOccupyCapacityAndRepeatedOpensReuseEntry()
    {
        var store = new MemoryShopSessionStore(new MemoryPlayerTable(1));
        Assert.Null(await store.FindLastOpenAsync(12, default));
        await store.RememberOpenAsync(new(0, 42, 0, ShopKind.ItemShop), default);
        await store.RememberOpenAsync(new(1, 42, 0, ShopKind.ItemShop), default);
        await store.RememberOpenAsync(new(1, 43, 0, ShopKind.CashShop), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RememberOpenAsync(new(2, 42, 0, ShopKind.ItemShop), default).AsTask());
        Assert.Null(await store.FindLastOpenAsync(2, default));
        Assert.Equal(43, (await store.FindLastOpenAsync(1, default))!.Token);
    }

    [Fact]
    public async Task ConcurrentOpensCannotExceedSharedPlayerCapacity()
    {
        var store = new MemoryShopSessionStore(new MemoryPlayerTable(3));
        var attempts = Enumerable.Range(1, 20).Select(i => Task.Run(async () =>
        {
            try { await store.RememberOpenAsync(new(i, i, 0, ShopKind.ItemShop), default); return true; }
            catch (InvalidOperationException) { return false; }
        }));
        Assert.Equal(3, (await Task.WhenAll(attempts)).Count(success => success));
    }
}
