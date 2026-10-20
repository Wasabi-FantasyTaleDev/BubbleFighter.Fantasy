using BubbleFighter.Fantasy.Contracts.Shop;
using Xunit;

namespace BubbleFighter.Fantasy.Shop.Tests;

public sealed class ShopOpenScopeTests
{
    private static readonly ShopOpenRecord ItemOpen = new(7, 42, 1, ShopKind.ItemShop);
    private static readonly ShopOpenRecord CashOpen = new(7, 99, 0, ShopKind.CashShop);

    [Fact]
    public void RememberStoresByConnectionAndReplacesOnSameConnection()
    {
        using var scope = new ShopOpenScope(2);
        scope.Remember(1, ItemOpen);
        Assert.True(scope.TryGet(1, out var first));
        Assert.Equal(ItemOpen, first);

        scope.Remember(1, CashOpen);
        Assert.Equal(1, scope.Count);
        Assert.True(scope.TryGet(1, out var second));
        Assert.Equal(CashOpen, second);
    }

    [Fact]
    public void RememberRefusesANewConnectionPastCapacity()
    {
        using var scope = new ShopOpenScope(1);
        scope.Remember(1, ItemOpen);
        var error = Assert.Throws<InvalidOperationException>(() => scope.Remember(2, CashOpen));
        Assert.Contains("cap 1", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, scope.Count);
        Assert.True(scope.TryGet(1, out _));
        Assert.False(scope.TryGet(2, out _));
    }

    [Fact]
    public void RemoveAndDisposeDropEntries()
    {
        var scope = new ShopOpenScope(4);
        scope.Remember(1, ItemOpen);
        scope.Remember(2, CashOpen);

        scope.Remove(1);
        Assert.False(scope.TryGet(1, out _));
        Assert.True(scope.TryGet(2, out _));

        scope.Dispose();
        Assert.Equal(0, scope.Count);
        Assert.False(scope.TryGet(2, out _));
    }

    [Fact]
    public void ConstructorRejectsNonPositiveCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ShopOpenScope(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ShopOpenScope(-1));
    }
}
