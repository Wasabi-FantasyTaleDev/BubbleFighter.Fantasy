using BubbleFighter.Fantasy.Contracts.Clans;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Tests;

public abstract class ClanStoreContractGuardTests
{
    protected abstract IClanStore CreateStore();

    [Fact]
    public async Task AnonymousIdentityDoesNotCreatePersistentState()
    {
        var store = CreateStore();
        await store.RememberOpenAsync(new ClanOpenRecord(0, ClanOpenKind.MainOpen), CancellationToken.None);
        Assert.Null(await store.FindLastOpenAsync(0, CancellationToken.None));
        Assert.Equal(new ClanMembership(0, 0), await store.GetMembershipAsync(0, CancellationToken.None));
    }

    [Fact]
    public async Task InvalidIdentitiesAndOpenKindsFailBeforeStorageAccess()
    {
        var store = CreateStore();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.GetMembershipAsync(-1, default).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.FindLastOpenAsync(-1, default).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.RememberOpenAsync(new(-1, ClanOpenKind.MainOpen), default).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.RememberOpenAsync(new(1, (ClanOpenKind)3), default).AsTask());
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.RememberOpenAsync(null!, default).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ListMemberUserNumsAsync(0, 1, default).AsTask());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public async Task InvalidSnapshotBoundsFailBeforeStorageAccess(int maxCount)
    {
        var store = CreateStore();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ListClanIdsAsync(maxCount, default).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ListMemberUserNumsAsync(1, maxCount, default).AsTask());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task CancellationIsObservedEvenForAnonymousIdentity(long userNum)
    {
        var store = CreateStore();
        using var source = new CancellationTokenSource();
        source.Cancel();
        var ct = source.Token;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.RememberOpenAsync(new(userNum, ClanOpenKind.MainOpen), ct).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.FindLastOpenAsync(userNum, ct).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.GetMembershipAsync(userNum, ct).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ListClanIdsAsync(1, ct).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ListMemberUserNumsAsync(1, 1, ct).AsTask());
    }
}
