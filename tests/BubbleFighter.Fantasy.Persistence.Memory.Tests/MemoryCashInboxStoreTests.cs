using BubbleFighter.Fantasy.Contracts.CashInbox;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Wallet;
using BubbleFighter.Fantasy.Persistence.Tests;

namespace BubbleFighter.Fantasy.Persistence.Memory.Tests;

public sealed class MemoryCashInboxStoreTests : CashInboxStoreContractTests
{
    protected override ValueTask<ICashInboxFixture> CreateFixtureAsync() =>
        ValueTask.FromResult<ICashInboxFixture>(new MemoryFixture());

    private sealed class MemoryFixture : ICashInboxFixture
    {
        private readonly MemoryPlayerTable _table = new(32);
        private readonly MemoryPlayerRepository _players;
        private readonly MemoryInventoryRepository _inventory;
        private readonly MemoryPlayerWalletRepository _wallets;

        public MemoryFixture()
        {
            _players = new(_table);
            _inventory = new(_table);
            _wallets = new(_table);
            Store = new MemoryCashInboxStore(_table);
        }

        public ICashInboxStore Store { get; }

        public async ValueTask<long> CreatePlayerAsync(string username, CancellationToken ct) =>
            (await _players.CreateAsync(username, username, ct)).UserNum;

        public ValueTask<IReadOnlyList<InventorySlotRecord>> ReadCharacterBagAsync(long userNum, CancellationToken ct) =>
            _inventory.ListSlotsAsync(userNum, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, ct);

        public ValueTask ReplaceCharacterBagAsync(long userNum, IReadOnlyList<InventorySlotRecord> slots, CancellationToken ct) =>
            _inventory.ReplaceSlotsAsync(userNum, InventoryBag.CharacterBag, slots, ct);

        public ValueTask<PlayerWallet?> ReadWalletAsync(long userNum, CancellationToken ct) =>
            _wallets.FindByUserNumAsync(userNum, ct);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
