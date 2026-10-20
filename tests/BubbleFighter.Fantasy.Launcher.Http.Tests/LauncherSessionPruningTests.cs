using BubbleFighter.Fantasy.Contracts.Launcher;
using BubbleFighter.Fantasy.Persistence.Memory;
using Xunit;

namespace BubbleFighter.Fantasy.Launcher.Http.Tests;

public sealed class LauncherSessionPruningTests
{
    [Fact]
    public async Task HttpStartupPrunesAndSignInPrunesAtMostOncePerHour()
    {
        var clock = new Clock();
        var store = new CountingStore(new MemoryLauncherSessions(32));
        await store.CreateAsync("expired", "alice", "old", clock.Now.UtcDateTime, clock.Now.AddHours(-1).UtcDateTime, default);
        var options = new LauncherOptions { Listen = "127.0.0.1:0", PasswordIterations = 100000 };
        var sessions = new LauncherSessions(options, store, clock);
        await using var server = await LauncherServer.StartAsync(options, new MemoryLauncherAccounts(4), sessions);
        Assert.Equal(1, store.Prunes);
        Assert.Null(await store.FindAsync("old", default));
        await sessions.InitializeAsync(default);
        await sessions.SignInAsync("alice", default);
        clock.Now = clock.Now.AddMinutes(59);
        await sessions.SignInAsync("alice", default);
        Assert.Equal(1, store.Prunes);
        clock.Now = clock.Now.AddMinutes(1);
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => sessions.SignInAsync("alice", default).AsTask()));
        Assert.Equal(2, store.Prunes);
        clock.Now = clock.Now.AddMinutes(59);
        await sessions.SignInAsync("alice", default);
        Assert.Equal(2, store.Prunes);
    }

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = new(2026, 10, 19, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class CountingStore(ILauncherSessionStore inner) : ILauncherSessionStore
    {
        internal int Prunes { get; private set; }
        public ValueTask<int> PruneExpiredAsync(DateTime now, CancellationToken ct)
        {
            Prunes++;
            return inner.PruneExpiredAsync(now, ct);
        }
        public ValueTask CreateAsync(string id, string user, string hash, DateTime expires, DateTime now, CancellationToken ct) =>
            inner.CreateAsync(id, user, hash, expires, now, ct);
        public ValueTask<IReadOnlyList<string>> CreateLimitedAsync(string id, string user, string hash, DateTime expires,
            DateTime now, int maximum, CancellationToken ct) => inner.CreateLimitedAsync(id, user, hash, expires, now, maximum, ct);
        public ValueTask<LauncherRefreshSession?> FindAsync(string hash, CancellationToken ct) => inner.FindAsync(hash, ct);
        public ValueTask<bool> TryRotateAsync(string id, string current, string next, DateTime expires, DateTime now,
            CancellationToken ct) => inner.TryRotateAsync(id, current, next, expires, now, ct);
        public ValueTask RevokeAsync(string id, DateTime now, CancellationToken ct) => inner.RevokeAsync(id, now, ct);
    }
}
