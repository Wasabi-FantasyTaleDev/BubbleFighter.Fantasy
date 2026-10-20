using BubbleFighter.Fantasy.Contracts.Shop;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Tests;

public abstract class ShopSessionStoreContractGuardTests
{
    protected abstract IShopSessionStore CreateStore();

    [Fact]
    public async Task AnonymousOpenDoesNotCreateStorageState()
    {
        var store = CreateStore();
        await store.RememberOpenAsync(new(0, 42, 0, ShopKind.ItemShop), default);
        Assert.Null(await store.FindLastOpenAsync(0, default));
    }

    [Fact]
    public async Task InvalidIdentitiesAndKindsFailBeforeStorageAccess()
    {
        var store = CreateStore();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.FindLastOpenAsync(-1, default).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.RememberOpenAsync(new(-1, 42, 0, ShopKind.ItemShop), default).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.RememberOpenAsync(new(1, 42, 0, (ShopKind)2), default).AsTask());
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.RememberOpenAsync(null!, default).AsTask());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task CancellationIsObservedBeforeAccessEvenForAnonymousPlayers(long userNum)
    {
        var store = CreateStore();
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.RememberOpenAsync(new(userNum, 42, 0, ShopKind.CashShop), source.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.FindLastOpenAsync(userNum, source.Token).AsTask());
    }
}
