using BubbleFighter.Fantasy.Abstractions.Persistence;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Persistence.MySql;
using BubbleFighter.Fantasy.Persistence.Tests;

namespace BubbleFighter.Fantasy.Persistence.MySql.Tests;

public sealed class ShopSessionStoreContractTests : ShopSessionStoreContractGuardTests
{
    protected override IShopSessionStore CreateStore() => new MySqlShopSessionStore(new UnusedFactory());

    private sealed class UnusedFactory : IDbConnectionFactory
    {
        public ValueTask<System.Data.Common.DbConnection> OpenAsync(CancellationToken ct) =>
            throw new InvalidOperationException("Contract guards must run before opening a connection.");
    }
}
