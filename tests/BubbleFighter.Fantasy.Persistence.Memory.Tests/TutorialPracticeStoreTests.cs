using BubbleFighter.Fantasy.Contracts.Progress;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Memory.Tests;

public sealed class TutorialPracticeStoreTests
{
    [Fact]
    public async Task ClearIdentityIsOnceOnlyBoundedAndIsolatedByPlayerBuildAndMap()
    {
        var store = new MemoryTutorialPractice(1);
        var key = new TutorialPracticeKey("cn-279", 7, "rel_Tutorial_01");
        Assert.False(await store.HasClearedAsync(key, default));
        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => store.RecordClearAsync(key, default).AsTask()));
        Assert.Single(results, result => result);
        Assert.True(await store.HasClearedAsync(new("CN-279", 7, "REL_TUTORIAL_01"), default));
        Assert.False(await store.HasClearedAsync(new("cn-279", 8, "rel_Tutorial_01"), default));
        Assert.False(await store.HasClearedAsync(new("cn-324", 7, "rel_Tutorial_01"), default));
        Assert.False(await store.HasClearedAsync(new("cn-279", 7, "rel_prologue_01"), default));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.RecordClearAsync(new("cn-279", 8, "rel_Tutorial_01"), default));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await store.HasClearedAsync(key, canceled.Token));
    }
}
