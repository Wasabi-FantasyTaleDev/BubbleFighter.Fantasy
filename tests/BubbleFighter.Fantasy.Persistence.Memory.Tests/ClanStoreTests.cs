using BubbleFighter.Fantasy.Contracts.Clans;
using BubbleFighter.Fantasy.Persistence.Memory;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Memory.Tests;

public sealed class ClanStoreTests
{
    [Fact]
    public async Task MemoryStoreStartsWithEmptyListAndRoster()
    {
        var store = new MemoryClanStore(new MemoryPlayerTable(1));
        Assert.Empty(await store.ListClanIdsAsync(1, CancellationToken.None));
        Assert.Empty(await store.ListMemberUserNumsAsync(1, 1, CancellationToken.None));
        var membership = await store.GetMembershipAsync(7, CancellationToken.None);
        Assert.Equal(7, membership.UserNum);
        Assert.Equal(0, membership.ClanId);
    }

    [Fact]
    public async Task MemoryStoreRemembersOpenForNonZeroUser()
    {
        var store = new MemoryClanStore(new MemoryPlayerTable(1));
        await store.RememberOpenAsync(new ClanOpenRecord(9, ClanOpenKind.MainOpen), CancellationToken.None);
        Assert.Equal(new ClanOpenRecord(9, ClanOpenKind.MainOpen), await store.FindLastOpenAsync(9, CancellationToken.None));
        await store.RememberOpenAsync(new ClanOpenRecord(9, ClanOpenKind.Attendance), CancellationToken.None);
        Assert.Equal(new ClanOpenRecord(9, ClanOpenKind.Attendance), await store.FindLastOpenAsync(9, CancellationToken.None));
        Assert.Empty(await store.ListClanIdsAsync(1, CancellationToken.None));
        Assert.Equal(0, (await store.GetMembershipAsync(9, CancellationToken.None)).ClanId);
    }
    [Fact]
    public async Task ReadsAndAnonymousOpensDoNotConsumePlayerCapacity()
    {
        var store = new MemoryClanStore(new MemoryPlayerTable(1));
        Assert.Null(await store.FindLastOpenAsync(77, default));
        Assert.Equal(new ClanMembership(77, 0), await store.GetMembershipAsync(77, default));
        await store.RememberOpenAsync(new(0, ClanOpenKind.MainOpen), default);
        await store.RememberOpenAsync(new(1, ClanOpenKind.MainOpen), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RememberOpenAsync(new(2, ClanOpenKind.MainOpen), default).AsTask());
        Assert.Null(await store.FindLastOpenAsync(2, default));
    }

    [Fact]
    public async Task ConcurrentNewPlayersCannotOverrunCapacity()
    {
        var store = new MemoryClanStore(new MemoryPlayerTable(3));
        var attempts = Enumerable.Range(1, 20).Select(i => Task.Run(async () =>
        {
            try { await store.RememberOpenAsync(new(i, ClanOpenKind.MainOpen), default); return true; }
            catch (InvalidOperationException) { return false; }
        }));
        Assert.Equal(3, (await Task.WhenAll(attempts)).Count(success => success));
    }
}
