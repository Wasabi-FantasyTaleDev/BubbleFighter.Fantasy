using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Contracts.Wallet;
using BubbleFighter.Fantasy.Persistence.Memory;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Memory.Tests;

public class MemoryPlayerProgressTests
{
    private static RoundSettlementCommand Add(string match, long user, long exp, int lucci) =>
        new(match, user, lucci, stored => stored with { Level = stored.Level + 1, Exp = stored.Exp + exp });

    [Fact]
    public async Task ARoundCreditStoresProgressAndAddsLucciToTheWallet()
    {
        var table = new MemoryPlayerTable(16);
        var wallets = new MemoryPlayerWalletRepository(table);
        var progress = new MemoryPlayerProgressRepository(table);
        Assert.Null(await progress.FindAsync(7, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            progress.SettleRoundAsync(Add("m1", 7, 40, 10), CancellationToken.None).AsTask());

        await wallets.UpsertAsync(new PlayerWallet(7, 100, 5), CancellationToken.None);
        Assert.Equal(new PlayerProgress(7, 1, 0), await progress.FindAsync(7, CancellationToken.None));
        var settled = await progress.SettleRoundAsync(Add("m1", 7, 40, 10), CancellationToken.None);
        Assert.Equal(new RoundSettlementResult(RoundSettlementOutcome.Settled, new(7, 1, 0), new(7, 2, 40), 10, 110), settled);
        Assert.Equal(new PlayerProgress(7, 2, 40), await progress.FindAsync(7, CancellationToken.None));
        Assert.Equal(new PlayerWallet(7, 110, 5), await wallets.FindByUserNumAsync(7, CancellationToken.None));
        await wallets.UpsertAsync(new PlayerWallet(7, 120, 5), CancellationToken.None);
        Assert.Equal(new PlayerProgress(7, 2, 40), await progress.FindAsync(7, CancellationToken.None));
    }

    [Fact]
    public async Task ARepeatedSettlementOfTheSameMatchChangesNothingAndReturnsTheFirstResult()
    {
        var table = new MemoryPlayerTable(16);
        var wallets = new MemoryPlayerWalletRepository(table);
        var progress = new MemoryPlayerProgressRepository(table);
        await wallets.UpsertAsync(new PlayerWallet(7, 100, 5), CancellationToken.None);
        await progress.SettleRoundAsync(Add("m1", 7, 40, 10), CancellationToken.None);
        var repeated = await progress.SettleRoundAsync(Add("m1", 7, 999, 999), CancellationToken.None);
        Assert.Equal(new RoundSettlementResult(RoundSettlementOutcome.AlreadySettled, new(7, 1, 0), new(7, 2, 40), 10, 110), repeated);
        Assert.Equal(new PlayerProgress(7, 2, 40), await progress.FindAsync(7, CancellationToken.None));
        Assert.Equal(110, (await wallets.FindByUserNumAsync(7, CancellationToken.None))!.Lucci);
        // Another match applies on top of the stored progress.
        var next = await progress.SettleRoundAsync(Add("m2", 7, 5, 1), CancellationToken.None);
        Assert.Equal((new PlayerProgress(7, 2, 40), new PlayerProgress(7, 3, 45), 111), (next.Before, next.After, next.LucciBalance));
    }

    [Fact]
    public async Task ConcurrentSettlementsOfOneMatchCreditOnce()
    {
        var table = new MemoryPlayerTable(16);
        var wallets = new MemoryPlayerWalletRepository(table);
        var progress = new MemoryPlayerProgressRepository(table);
        await wallets.UpsertAsync(new PlayerWallet(7, 0, 0), CancellationToken.None);
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            Task.Run(() => progress.SettleRoundAsync(Add("m1", 7, 40, 10), CancellationToken.None).AsTask())));
        Assert.Single(results, result => result.Outcome == RoundSettlementOutcome.Settled);
        Assert.Equal(10, (await wallets.FindByUserNumAsync(7, CancellationToken.None))!.Lucci);
        Assert.Equal(new PlayerProgress(7, 2, 40), await progress.FindAsync(7, CancellationToken.None));
    }

    [Fact]
    public async Task AnInvalidProgressStoresNothing()
    {
        var table = new MemoryPlayerTable(16);
        var wallets = new MemoryPlayerWalletRepository(table);
        var progress = new MemoryPlayerProgressRepository(table);
        await wallets.UpsertAsync(new PlayerWallet(7, 100, 0), CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => progress.SettleRoundAsync(
            new("m1", 7, 10, stored => stored with { UserNum = 8 }), CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => progress.SettleRoundAsync(
            new("bad id", 7, 10, stored => stored), CancellationToken.None).AsTask());
        Assert.Equal(100, (await wallets.FindByUserNumAsync(7, CancellationToken.None))!.Lucci);
        // The failed match left no receipt, so its retry settles.
        Assert.Equal(RoundSettlementOutcome.Settled, (await progress.SettleRoundAsync(Add("m1", 7, 1, 1), CancellationToken.None)).Outcome);
    }
}
