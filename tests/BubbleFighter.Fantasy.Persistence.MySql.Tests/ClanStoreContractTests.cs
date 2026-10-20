using BubbleFighter.Fantasy.Abstractions.Persistence;
using BubbleFighter.Fantasy.Contracts.Clans;
using BubbleFighter.Fantasy.Persistence.MySql;
using BubbleFighter.Fantasy.Persistence.Tests;

namespace BubbleFighter.Fantasy.Persistence.MySql.Tests;

public sealed class ClanStoreContractTests : ClanStoreContractGuardTests
{
    protected override IClanStore CreateStore() => new MySqlClanStore(new UnusedFactory());

    private sealed class UnusedFactory : IDbConnectionFactory
    {
        public ValueTask<System.Data.Common.DbConnection> OpenAsync(CancellationToken ct) =>
            throw new InvalidOperationException("Contract guards must run before opening a connection.");
    }
}
