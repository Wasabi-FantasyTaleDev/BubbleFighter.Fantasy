using BubbleFighter.Fantasy.Contracts.Messenger;
using BubbleFighter.Fantasy.Persistence.Memory;
using BubbleFighter.Fantasy.Persistence.Tests;

namespace BubbleFighter.Fantasy.Persistence.Memory.Tests;

/// <summary>Runs the shared messenger store contract over the process-local store.</summary>
public sealed class MemoryMessengerStoreTests : MessengerStoreContractTests
{
    /// <inheritdoc />
    protected override IMessengerStore CreateStore() => new MemoryMessengerStore(64);
}
