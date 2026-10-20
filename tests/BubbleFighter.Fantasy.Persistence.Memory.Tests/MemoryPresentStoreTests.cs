using BubbleFighter.Fantasy.Persistence.Memory;
using BubbleFighter.Fantasy.Persistence.Tests;

namespace BubbleFighter.Fantasy.Persistence.Memory.Tests;

/// <summary>Runs the shared gift store contract over the process-local store.</summary>
public sealed class MemoryPresentStoreTests : PresentStoreContractTests
{
    /// <inheritdoc />
    protected override Stores Create()
    {
        var table = new MemoryPlayerTable(16);
        var players = new MemoryPlayerRepository(table);
        var presents = new MemoryPresentStore(table, players);
        return new Stores(presents, players, new MemoryShopPurchaseStore(table, presents), new MemoryInventoryRepository(table),
            new MemoryPlayerWalletRepository(table));
    }
}
