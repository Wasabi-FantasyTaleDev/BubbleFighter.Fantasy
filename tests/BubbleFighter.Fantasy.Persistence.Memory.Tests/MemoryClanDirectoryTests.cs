using BubbleFighter.Fantasy.Contracts.Clans;
using BubbleFighter.Fantasy.Persistence.Memory;
using BubbleFighter.Fantasy.Persistence.Tests;

namespace BubbleFighter.Fantasy.Persistence.Memory.Tests;

public sealed class MemoryClanDirectoryTests : ClanDirectoryContractTests
{
    protected override ValueTask<IClanDirectory> CreateEmptyAsync() => new(new MemoryClanDirectory());
}
