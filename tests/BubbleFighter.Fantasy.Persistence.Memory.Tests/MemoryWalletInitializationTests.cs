using BubbleFighter.Fantasy.Contracts.Wallet;
using BubbleFighter.Fantasy.Persistence.Memory;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Memory.Tests;

public sealed class MemoryWalletInitializationTests
{
    [Fact]
    public async Task ConcurrentRepositoriesShareOneInitialWalletAndPreserveLaterUpdates()
    {
        var table = new MemoryPlayerTable(16);
        var repositories = Enumerable.Range(0, 16).Select(_ => new MemoryPlayerWalletRepository(table)).ToArray();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = repositories.Select(async (repository, index) =>
        {
            await start.Task;
            return await repository.GetOrCreateAsync(new(7, 100 + index, index), default);
        }).ToArray();
        start.SetResult();
        var wallets = await Task.WhenAll(calls);
        Assert.All(wallets, wallet => Assert.Equal(wallets[0], wallet));

        var committed = new PlayerWallet(7, 50, 2);
        await repositories[0].UpsertAsync(committed, default);
        foreach (var repository in repositories)
            Assert.Equal(committed, await repository.GetOrCreateAsync(new(7, 9999, 9999), default));
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(7, -1, 1)]
    [InlineData(7, 1, -1)]
    public async Task InvalidInitializationCannotCreateState(long user, int lucci, int cash)
    {
        var repository = new MemoryPlayerWalletRepository(new MemoryPlayerTable(16));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            repository.GetOrCreateAsync(new(user, lucci, cash), default).AsTask());
        Assert.Null(await repository.FindByUserNumAsync(7, default));
    }
}
