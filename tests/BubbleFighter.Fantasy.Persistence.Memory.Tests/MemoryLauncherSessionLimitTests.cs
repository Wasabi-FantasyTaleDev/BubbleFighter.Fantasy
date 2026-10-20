using BubbleFighter.Fantasy.Contracts.Launcher;
using BubbleFighter.Fantasy.Persistence.Memory;
using BubbleFighter.Fantasy.Tests.Shared;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Memory.Tests;

public sealed class MemoryLauncherSessionLimitTests : LauncherSessionLimitTests
{
    protected override ILauncherSessionStore CreateStore() => new MemoryLauncherSessions(32);

    [Fact]
    public async Task ExpiredSignInsDoNotFillTheStorageBound()
    {
        var store = new MemoryLauncherSessions(2);
        var now = new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc);
        await store.CreateLimitedAsync("a", "alice", "ha", now.AddMinutes(1), now, 8, default);
        await store.CreateLimitedAsync("b", "bob", "hb", now.AddMinutes(1), now, 8, default);
        Assert.Equal(["c"], await store.CreateLimitedAsync("c", "carol", "hc", now.AddDays(1), now.AddMinutes(2), 8, default));
        Assert.Null(await store.FindAsync("ha", default));
    }
}
