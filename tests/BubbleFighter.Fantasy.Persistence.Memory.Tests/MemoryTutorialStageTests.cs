using BubbleFighter.Fantasy.Contracts.Progress;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Memory.Tests;

public sealed class MemoryTutorialStageTests
{
    [Fact]
    public async Task FirstClearIsReportedOnceAndTheBestRecordsAreKept()
    {
        var store = new MemoryTutorialStages(maximumPlayers: 2);
        Assert.True(await store.RecordClearAsync(7, new TutorialStageClear(16, 0, 95), default));
        Assert.False(await store.RecordClearAsync(7, new TutorialStageClear(16, 3, 120), default));
        Assert.False(await store.RecordClearAsync(7, new TutorialStageClear(16, 1, 80), default));
        Assert.True(await store.RecordClearAsync(7, new TutorialStageClear(4, 25, 0), default));
        Assert.Equal([new TutorialStageClear(4, 25, 0), new TutorialStageClear(16, 3, 80)], await store.ListAsync(7, default));
        Assert.Empty(await store.ListAsync(8, default));
    }

    [Fact]
    public async Task StorageIsBoundedByPlayers()
    {
        var store = new MemoryTutorialStages(maximumPlayers: 1);
        await store.RecordClearAsync(1, new TutorialStageClear(1, 0, 10), default);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.RecordClearAsync(2, new TutorialStageClear(1, 0, 10), default));
    }
}
