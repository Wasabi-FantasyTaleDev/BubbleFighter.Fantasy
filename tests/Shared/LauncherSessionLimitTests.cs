using BubbleFighter.Fantasy.Contracts.Launcher;
using Xunit;

namespace BubbleFighter.Fantasy.Tests.Shared;

public abstract class LauncherSessionLimitTests
{
    protected abstract ILauncherSessionStore CreateStore();
    private static readonly DateTime Now = new(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task PruningRemovesExpiredRowsIncludingTheBoundaryAndKeepsLiveRows()
    {
        var store = CreateStore();
        await store.CreateAsync("expired", "alice", "expired-hash", Now, Now.AddHours(-1), default);
        await store.CreateAsync("live", "alice", "live-hash", Now.AddDays(1), Now.AddHours(-1), default);
        Assert.Equal(1, await store.PruneExpiredAsync(Now, default));
        Assert.Null(await store.FindAsync("expired-hash", default));
        Assert.NotNull(await store.FindAsync("live-hash", default));
        Assert.Equal(0, await store.PruneExpiredAsync(Now, default));
    }

    [Fact]
    public async Task LimitRevokesOldestCreationRatherThanMostRecentlyRotatedAndKeepsOtherAccounts()
    {
        var store = CreateStore();
        await store.CreateLimitedAsync("one", "alice", "h1", Now.AddDays(1), Now, 2, default);
        await store.CreateLimitedAsync("other", "bob", "hb", Now.AddDays(1), Now, 2, default);
        await store.CreateLimitedAsync("two", "alice", "h2", Now.AddDays(1), Now.AddSeconds(1), 2, default);
        Assert.True(await store.TryRotateAsync("one", "h1", "rotated", Now.AddDays(2), Now.AddSeconds(2), default));
        var retained = await store.CreateLimitedAsync("three", "alice", "h3", Now.AddDays(1), Now.AddSeconds(3), 2, default);
        Assert.Equal(["two", "three"], retained.OrderBy(id => id, StringComparer.Ordinal).Reverse());
        Assert.False(await store.TryRotateAsync("one", "rotated", "replay", Now.AddDays(2), Now.AddSeconds(4), default));
        Assert.True(await store.TryRotateAsync("other", "hb", "bob-next", Now.AddDays(2), Now.AddSeconds(4), default));
    }

    [Fact]
    public async Task CapOneKeepsNewSessionEvenWhenCreationTimestampsTie()
    {
        var store = CreateStore();
        await store.CreateLimitedAsync("zzz-old", "alice", "old", Now.AddDays(1), Now, 1, default);
        Assert.Equal(["aaa-new"], await store.CreateLimitedAsync("aaa-new", "alice", "new", Now.AddDays(1), Now, 1, default));
        Assert.False(await store.TryRotateAsync("zzz-old", "old", "replay", Now.AddDays(2), Now, default));
        Assert.NotNull(await store.FindAsync("new", default));
    }

    [Fact]
    public async Task ConcurrentSignInsKeepTheConfiguredCount()
    {
        var store = CreateStore();
        await Task.WhenAll(Enumerable.Range(0, 10).Select(index =>
            store.CreateLimitedAsync("s" + index, "alice", "h" + index, Now.AddDays(1), Now.AddSeconds(index), 2, default).AsTask()));
        var rows = await Task.WhenAll(Enumerable.Range(0, 10).Select(index => store.FindAsync("h" + index, default).AsTask()));
        Assert.Equal(2, rows.Count(row => row is { RevokedAt: null }));
    }

    [Fact]
    public async Task InvalidLimitAndCancellationDoNotRevokeExistingSession()
    {
        var store = CreateStore();
        await store.CreateLimitedAsync("one", "alice", "h1", Now.AddDays(1), Now, 1, default);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.CreateLimitedAsync("two", "alice", "h2", Now.AddDays(1), Now, 0, default).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.CreateLimitedAsync("two", "alice", "h2", Now.AddDays(1), Now, 1, new CancellationToken(true)).AsTask());
        Assert.True(await store.TryRotateAsync("one", "h1", "next", Now.AddDays(2), Now, default));
    }
}
