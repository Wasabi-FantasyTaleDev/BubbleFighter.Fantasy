using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Contracts.Titles;
using BubbleFighter.Fantasy.Contracts.Wallet;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Memory.Tests;

/// <summary>Checks that terminal medals, progress and wallet credits share a single account receipt.</summary>
public sealed class RoundTitleSettlementTests
{
    private static RoundSettlementCommand Command(string match = "round-one", int lucci = 2, int medals = 3) =>
        new(match, 1, lucci, stored => stored with { Exp = stored.Exp + 7 })
        { TitleMedals = new(1, "kr-1256", "round", "round:" + match, [new(30001, medals)]) };

    /// <summary>Competing terminal callbacks credit every reward once under the same account lock.</summary>
    [Fact]
    public async Task ConcurrentCallbacksCommitOneMedalAndProgressReceipt()
    {
        var table = new MemoryPlayerTable(1);
        var wallets = new MemoryPlayerWalletRepository(table);
        await wallets.UpsertAsync(new PlayerWallet(1, 10, 0), default);
        var progress = new MemoryPlayerProgressRepository(table);
        var inventory = new MemoryInventoryRepository(table);
        var results = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => Task.Run(async () => await progress.SettleRoundAsync(Command(), default))));
        Assert.Single(results, result => result.Outcome == RoundSettlementOutcome.Settled);
        Assert.Equal(7, (await progress.FindAsync(1, default))!.Exp);
        Assert.Equal(12, (await wallets.FindByUserNumAsync(1, default))!.Lucci);
        Assert.Equal(new[] { new TitleMedalCount(30001, 3) }, await inventory.ReadAsync(1, "kr-1256", default));
    }

    /// <summary>A wallet overflow leaves the staged medal event absent so a corrected retry can settle it.</summary>
    [Fact]
    public async Task WalletFailureDoesNotCommitMedalsOrTheirReceipt()
    {
        var table = new MemoryPlayerTable(1);
        var wallets = new MemoryPlayerWalletRepository(table);
        await wallets.UpsertAsync(new PlayerWallet(1, int.MaxValue, 0), default);
        var progress = new MemoryPlayerProgressRepository(table);
        var inventory = new MemoryInventoryRepository(table);
        await Assert.ThrowsAsync<OverflowException>(() => progress.SettleRoundAsync(Command(), default).AsTask());
        Assert.Empty(await inventory.ReadAsync(1, "kr-1256", default));
        Assert.Empty(table.Find(1)!.TitleReceipts);
        Assert.Empty(table.Find(1)!.BattleSettlements);
        var retried = await progress.SettleRoundAsync(Command(lucci: 0), default);
        Assert.Equal(RoundSettlementOutcome.Settled, retried.Outcome);
        Assert.Equal(3, Assert.Single(await inventory.ReadAsync(1, "kr-1256", default)).Count);
    }

    /// <summary>The permanent gameplay receipt outlives the small compatibility receipt window.</summary>
    [Fact]
    public async Task MedalRoundCannotCreditProgressAgainAfterCompatibilityReceiptsRotate()
    {
        var table = new MemoryPlayerTable(1);
        var wallets = new MemoryPlayerWalletRepository(table);
        await wallets.UpsertAsync(new PlayerWallet(1, 0, 0), default);
        var progress = new MemoryPlayerProgressRepository(table);
        for (var index = 0; index < 12; index++)
            await progress.SettleRoundAsync(Command("round-" + index), default);
        Assert.Equal(RoundSettlementOutcome.AlreadySettled,
            (await progress.SettleRoundAsync(Command("round-0"), default)).Outcome);
        Assert.Equal(84, (await progress.FindAsync(1, default))!.Exp);
        Assert.Equal(24, (await wallets.FindByUserNumAsync(1, default))!.Lucci);
        Assert.Equal(36, Assert.Single(await new MemoryInventoryRepository(table).ReadAsync(1, "kr-1256", default)).Count);
    }

    /// <summary>An existing event with another payload cannot hide behind the round's progression receipt.</summary>
    [Fact]
    public async Task ReplayWithDifferentMedalsFailsWithoutChangingCommittedRewards()
    {
        var table = new MemoryPlayerTable(1);
        var wallets = new MemoryPlayerWalletRepository(table);
        await wallets.UpsertAsync(new PlayerWallet(1, 0, 0), default);
        var progress = new MemoryPlayerProgressRepository(table);
        await progress.SettleRoundAsync(Command(), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => progress.SettleRoundAsync(Command(medals: 4), default).AsTask());
        Assert.Equal(7, (await progress.FindAsync(1, default))!.Exp);
        Assert.Equal(3, Assert.Single(await new MemoryInventoryRepository(table).ReadAsync(1, "kr-1256", default)).Count);
    }

    /// <summary>Preexisting standalone credits cannot be mistaken for the atomic outcome of an uncommitted round.</summary>
    [Fact]
    public async Task MedalReceiptWithoutItsRoundReceiptIsRejected()
    {
        var table = new MemoryPlayerTable(1);
        var wallets = new MemoryPlayerWalletRepository(table);
        await wallets.UpsertAsync(new PlayerWallet(1, 0, 0), default);
        var inventory = new MemoryInventoryRepository(table);
        await inventory.GrantAsync(Command().TitleMedals!, default);
        var progress = new MemoryPlayerProgressRepository(table);
        await Assert.ThrowsAsync<InvalidDataException>(() => progress.SettleRoundAsync(Command(), default).AsTask());
        Assert.Equal(0, (await wallets.FindByUserNumAsync(1, default))!.Lucci);
        Assert.Empty(table.Find(1)!.BattleSettlements);
    }

    /// <summary>Reward identity must match the enclosing authenticated account, producer and round.</summary>
    [Theory]
    [InlineData(2, "round", "round:round-one")]
    [InlineData(1, "admin", "round:round-one")]
    [InlineData(1, "round", "round:different")]
    public void ForeignMedalIdentityIsRejected(long user, string source, string key)
    {
        var command = Command() with { TitleMedals = new(user, "kr-1256", source, key, [new(30001, 3)]) };
        Assert.Throws<ArgumentException>(command.Validate);
    }
}
