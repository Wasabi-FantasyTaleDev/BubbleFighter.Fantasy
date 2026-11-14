using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Tests.Shared;

namespace BubbleFighter.Fantasy.Persistence.Memory.Tests;

public sealed class OwnedInventorySelectionTests : OwnedInventorySelectionContractTests
{
    protected override IInventoryLoadoutStore Store { get; } = new MemoryInventoryRepository(new(4));
    protected override long UserNum => 1;
}
