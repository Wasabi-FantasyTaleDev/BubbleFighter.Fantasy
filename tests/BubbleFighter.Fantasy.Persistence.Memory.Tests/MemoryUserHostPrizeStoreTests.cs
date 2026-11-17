using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Wallet;
using BubbleFighter.Fantasy.Persistence.Tests;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Memory.Tests;

public sealed class MemoryUserHostPrizeStoreTests : UserHostPrizeStoreContractTests
{
    protected override ValueTask<IUserHostPrizeFixture> CreateFixtureAsync() =>
        ValueTask.FromResult<IUserHostPrizeFixture>(new MemoryFixture());

    [Fact]
    public async Task ConcurrentBeginEscrowCommitsAtMostOnceForSameMatch()
    {
        await using var fixture = await CreateFixtureAsync();
        var host = await fixture.CreatePlayerAsync("race_host", default);
        await fixture.SetWalletAsync(host, 20_000, 0, default);
        await fixture.ReplaceCharacterBagAsync(host,
        [
            new InventorySlotRecord(host, InventoryBag.CharacterBag, 1, 4100101, 2, 0)
            {
                Lifetime = InventoryLifetime.Permanent,
            },
        ], default);

        var attempts = Enumerable.Range(0, 8)
            .Select(_ => fixture.Store.BeginEscrowAsync(
                new UserHostEscrowBeginCommand("kr-812", "race-match", 1, host, 15_000, 10, 4100101, 1_000),
                default).AsTask())
            .ToArray();

        var results = await Task.WhenAll(attempts);
        Assert.Single(results, r => r.Outcome == UserHostEscrowBeginOutcome.Committed);
        Assert.Equal(7, results.Count(r => r.Outcome == UserHostEscrowBeginOutcome.AlreadyCommitted));
        Assert.Equal(5_000, (await fixture.ReadWalletAsync(host, default))?.Lucci);
        Assert.Equal(1, Assert.Single(await fixture.ReadCharacterBagAsync(host, default)).Quantity);
    }

    private sealed class MemoryFixture : IUserHostPrizeFixture
    {
        private readonly MemoryPlayerTable _table = new(32);
        private readonly MemoryPlayerRepository _players;
        private readonly MemoryPlayerWalletRepository _wallets;
        private readonly MemoryInventoryRepository _inventory;

        public MemoryFixture()
        {
            _players = new(_table);
            _wallets = new(_table);
            _inventory = new(_table);
            Store = new MemoryUserHostPrizeStore(_table);
        }

        public IUserHostPrizeStore Store { get; }

        public async ValueTask<long> CreatePlayerAsync(string username, CancellationToken ct) =>
            (await _players.CreateAsync(username, username, ct)).UserNum;

        public ValueTask SetWalletAsync(long userNum, int lucci, int cash, CancellationToken ct) =>
            _wallets.UpsertAsync(new PlayerWallet(userNum, lucci, cash), ct);

        public ValueTask<PlayerWallet?> ReadWalletAsync(long userNum, CancellationToken ct) =>
            _wallets.FindByUserNumAsync(userNum, ct);

        public ValueTask ReplaceCharacterBagAsync(long userNum, IReadOnlyList<InventorySlotRecord> slots, CancellationToken ct) =>
            _inventory.ReplaceSlotsAsync(userNum, InventoryBag.CharacterBag, slots, ct);

        public ValueTask<IReadOnlyList<InventorySlotRecord>> ReadCharacterBagAsync(long userNum, CancellationToken ct) =>
            _inventory.ListSlotsAsync(userNum, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, ct);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
