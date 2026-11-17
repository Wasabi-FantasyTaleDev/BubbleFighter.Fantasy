using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Contracts.Wallet;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Memory.Tests;

/// <summary>The development store of the plaza booths' day record and its wallet settlement.</summary>
public sealed class MemoryPlazaGameRecordTests
{
    private static readonly DateOnly Today = new(2026, 11, 15);

    /// <summary>Limits, one paper a day and a fresh next day; every refusal leaves record and wallet unchanged.</summary>
    [Fact]
    public async Task SettlementsChangeRecordAndWalletTogether()
    {
        var table = new MemoryPlayerTable(1);
        var store = new MemoryPlazaGameRecords(table);
        var wallets = new MemoryPlayerWalletRepository(table);
        var seed = new PlayerWallet(7, 100, 0);
        var fish = await store.SettleAsync(new(seed, Today) { Credit = 200, FishLimit = 1 }, default);
        Assert.Equal((PlazaGameSettlementStatus.Settled, 1, 300L), (fish.Status, fish.Record.FishingCount, fish.Wallet.Lucci));
        Assert.Equal(PlazaGameSettlementStatus.LimitReached,
            (await store.SettleAsync(new(seed, Today) { Credit = 200, FishLimit = 1 }, default)).Status);
        Assert.Equal(PlazaGameSettlementStatus.ShortBalance,
            (await store.SettleAsync(new(seed, Today) { Charge = 301, FortunePaper = 9 }, default)).Status);
        Assert.Equal(0UL, (await store.ReadAsync(7, Today, default)).FortunePaper);
        Assert.Equal(PlazaGameSettlementStatus.Settled,
            (await store.SettleAsync(new(seed, Today) { Charge = 300, FortunePaper = 9 }, default)).Status);
        var taken = await store.SettleAsync(new(seed, Today) { FortunePaper = 10 }, default);
        Assert.Equal((PlazaGameSettlementStatus.PaperTaken, 9UL), (taken.Status, taken.Record.FortunePaper));
        Assert.Equal(new PlazaGameRecord(Today, 1, 9), await store.ReadAsync(7, Today, default));
        Assert.Equal(0, (await wallets.FindByUserNumAsync(7, default))!.Lucci);
        Assert.Equal(PlazaGameRecord.Empty(Today.AddDays(1)), await store.ReadAsync(7, Today.AddDays(1), default));
        Assert.Throws<InvalidOperationException>(() =>
            store.SettleAsync(new(new PlayerWallet(8, 0, 0), Today) { FishLimit = 1 }, default).GetAwaiter().GetResult());
    }
}
