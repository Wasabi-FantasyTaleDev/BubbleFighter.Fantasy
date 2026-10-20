using BubbleFighter.Fantasy.Contracts.Clans;
using BubbleFighter.Fantasy.Persistence.Memory;
using BubbleFighter.Fantasy.Persistence.Tests;

namespace BubbleFighter.Fantasy.Persistence.Memory.Tests;

public sealed class ClanStoreContractTests : ClanStoreContractGuardTests
{
    protected override IClanStore CreateStore() => new MemoryClanStore(new MemoryPlayerTable(4));
}
