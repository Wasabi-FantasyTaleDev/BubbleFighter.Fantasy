using BubbleFighter.Fantasy.Contracts.Messenger;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Tests;

/// <summary>
/// The behaviour every <see cref="IMessengerStore"/> shares: accounts are keyed by tenant and subject, friendships are
/// always stored and ended for both accounts together, and every bound is enforced in the same change it limits.
/// </summary>
public abstract class MessengerStoreContractTests
{
    private static readonly MessengerLimits Limits = new(MaxFriends: 3, MaxCategories: 2, MaxPendingRequests: 2);
    private static readonly DateTime Now = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
    private const uint GameCode = 0x0001220F;

    /// <summary>Creates an empty store for one test.</summary>
    protected abstract IMessengerStore CreateStore();

    private static ValueTask<MessengerUser> Upsert(IMessengerStore store, string subject, string nickname, string tenant = "local") =>
        store.UpsertUserAsync(new(tenant, subject, nickname, GameCode), CancellationToken.None);

    private static async Task<(MessengerUser A, MessengerUser B)> FriendsAsync(IMessengerStore store)
    {
        var a = await Upsert(store, "alice", "Alice");
        var b = await Upsert(store, "bob", "Bob");
        var (created, request) = await store.CreateRequestAsync(a.Id, b.Id, "hi", Now, Limits, CancellationToken.None);
        Assert.Equal(MessengerResult.Ok, created);
        var (resolved, _) = await store.ResolveRequestAsync(request!.Handle, b.Id, accept: true, Limits, CancellationToken.None);
        Assert.Equal(MessengerResult.Ok, resolved);
        return (a, b);
    }

    [Fact]
    public async Task UpsertKeepsTheAccountAndRefreshesItsNickname()
    {
        var store = CreateStore();
        var first = await Upsert(store, "alice", "Alice");
        var again = await store.UpsertUserAsync(new("local", "alice", "Alicia", 0x0107220F), CancellationToken.None);
        Assert.Equal(first.Id, again.Id);
        Assert.Equal("Alicia", again.Nickname);
        Assert.Equal(0x0107220Fu, again.GameCode);
        Assert.Equal(again, await store.FindUserAsync(first.Id, CancellationToken.None));
        var otherTenant = await Upsert(store, "alice", "Alice", tenant: "other");
        Assert.NotEqual(first.Id, otherTenant.Id);
    }

    [Fact]
    public async Task NicknameLookupIgnoresCaseStaysInItsTenantAndRefusesAmbiguity()
    {
        var store = CreateStore();
        var alice = await Upsert(store, "alice", "Alice");
        await Upsert(store, "alice2", "Alice", tenant: "other");
        Assert.Equal(alice.Id, (await store.FindUserByNicknameAsync("local", "ALICE", CancellationToken.None))?.Id);
        Assert.Null(await store.FindUserByNicknameAsync("local", "nobody", CancellationToken.None));
        Assert.Null(await store.FindUserByNicknameAsync("third", "Alice", CancellationToken.None));
        await Upsert(store, "alice3", "alice");
        Assert.Null(await store.FindUserByNicknameAsync("local", "Alice", CancellationToken.None));
    }

    [Fact]
    public async Task AcceptedRequestCreatesTheFriendshipForBothAccounts()
    {
        var store = CreateStore();
        var (a, b) = await FriendsAsync(store);
        var ofA = await store.LoadRelationsAsync(a.Id, CancellationToken.None);
        var ofB = await store.LoadRelationsAsync(b.Id, CancellationToken.None);
        Assert.Equal(b.Id, Assert.Single(ofA.Relations).Friend.Id);
        Assert.Equal(a.Id, Assert.Single(ofB.Relations).Friend.Id);
        Assert.Equal(0, ofA.Relations[0].CategoryId);
        Assert.Empty(await store.ListIncomingRequestsAsync(b.Id, CancellationToken.None));
        var (again, _) = await store.CreateRequestAsync(b.Id, a.Id, "", Now, Limits, CancellationToken.None);
        Assert.Equal(MessengerResult.AlreadyFriends, again);
    }

    [Fact]
    public async Task PendingRequestsAreUniquePerPairAndBoundedPerRecipient()
    {
        var store = CreateStore();
        var a = await Upsert(store, "a", "A");
        var b = await Upsert(store, "b", "B");
        var c = await Upsert(store, "c", "C");
        var d = await Upsert(store, "d", "D");
        var (first, request) = await store.CreateRequestAsync(a.Id, b.Id, "first", Now, Limits, CancellationToken.None);
        Assert.Equal(MessengerResult.Ok, first);
        var (reverse, pending) = await store.CreateRequestAsync(b.Id, a.Id, "back", Now, Limits, CancellationToken.None);
        Assert.Equal(MessengerResult.AlreadyPending, reverse);
        Assert.Equal(request!.Handle, pending!.Handle);
        Assert.Equal(MessengerResult.Ok, (await store.CreateRequestAsync(c.Id, b.Id, "", Now, Limits, CancellationToken.None)).Result);
        Assert.Equal(MessengerResult.LimitReached, (await store.CreateRequestAsync(d.Id, b.Id, "", Now, Limits, CancellationToken.None)).Result);
        var incoming = await store.ListIncomingRequestsAsync(b.Id, CancellationToken.None);
        Assert.Equal([a.Id, c.Id], incoming.Select(r => r.From));
        Assert.Equal("first", incoming[0].Message);
        Assert.Equal(Now, incoming[0].CreatedAt);
    }

    [Fact]
    public async Task OnlyTheRecipientResolvesARequestAndDeclineCreatesNothing()
    {
        var store = CreateStore();
        var a = await Upsert(store, "a", "A");
        var b = await Upsert(store, "b", "B");
        var (_, request) = await store.CreateRequestAsync(a.Id, b.Id, "", Now, Limits, CancellationToken.None);
        Assert.Equal(MessengerResult.NotFound, (await store.ResolveRequestAsync(request!.Handle, a.Id, true, Limits, CancellationToken.None)).Result);
        Assert.Equal(MessengerResult.Ok, (await store.ResolveRequestAsync(request.Handle, b.Id, false, Limits, CancellationToken.None)).Result);
        Assert.Empty((await store.LoadRelationsAsync(a.Id, CancellationToken.None)).Relations);
        Assert.Equal(MessengerResult.NotFound, (await store.ResolveRequestAsync(request.Handle, b.Id, true, Limits, CancellationToken.None)).Result);
    }

    [Fact]
    public async Task AcceptAtTheFriendBoundChangesNothing()
    {
        var store = CreateStore();
        var hub = await Upsert(store, "hub", "Hub");
        for (var i = 0; i < Limits.MaxFriends; i++)
        {
            var friend = await Upsert(store, "f" + i, "F" + i);
            var (_, r) = await store.CreateRequestAsync(friend.Id, hub.Id, "", Now, Limits with { MaxPendingRequests = 10 }, CancellationToken.None);
            Assert.Equal(MessengerResult.Ok, (await store.ResolveRequestAsync(r!.Handle, hub.Id, true, Limits, CancellationToken.None)).Result);
        }
        var late = await Upsert(store, "late", "Late");
        var (_, request) = await store.CreateRequestAsync(late.Id, hub.Id, "", Now, Limits, CancellationToken.None);
        Assert.Equal(MessengerResult.LimitReached, (await store.ResolveRequestAsync(request!.Handle, hub.Id, true, Limits, CancellationToken.None)).Result);
        Assert.Single(await store.ListIncomingRequestsAsync(hub.Id, CancellationToken.None));
        Assert.Empty((await store.LoadRelationsAsync(late.Id, CancellationToken.None)).Relations);
    }

    [Fact]
    public async Task BlockingIsOneSidedAndVisibleToTheOther()
    {
        var store = CreateStore();
        var (a, b) = await FriendsAsync(store);
        Assert.True(await store.SetBlockedAsync(a.Id, b.Id, true, CancellationToken.None));
        var ofA = Assert.Single((await store.LoadRelationsAsync(a.Id, CancellationToken.None)).Relations);
        var ofB = Assert.Single((await store.LoadRelationsAsync(b.Id, CancellationToken.None)).Relations);
        Assert.True(ofA.Blocked);
        Assert.False(ofA.BlockedByFriend);
        Assert.False(ofB.Blocked);
        Assert.True(ofB.BlockedByFriend);
        Assert.True(await store.SetBlockedAsync(a.Id, b.Id, false, CancellationToken.None));
        Assert.False(Assert.Single((await store.LoadRelationsAsync(b.Id, CancellationToken.None)).Relations).BlockedByFriend);
        var stranger = await Upsert(store, "s", "S");
        Assert.False(await store.SetBlockedAsync(a.Id, stranger.Id, true, CancellationToken.None));
    }

    [Fact]
    public async Task DeletingAFriendshipEndsItForBoth()
    {
        var store = CreateStore();
        var (a, b) = await FriendsAsync(store);
        Assert.True(await store.DeleteFriendshipAsync(b.Id, a.Id, CancellationToken.None));
        Assert.Empty((await store.LoadRelationsAsync(a.Id, CancellationToken.None)).Relations);
        Assert.Empty((await store.LoadRelationsAsync(b.Id, CancellationToken.None)).Relations);
        Assert.False(await store.DeleteFriendshipAsync(a.Id, b.Id, CancellationToken.None));
    }

    [Fact]
    public async Task CategoriesTakeTheLowestFreeIdAndOnlyEmptyOnesAreDeleted()
    {
        var store = CreateStore();
        var (a, b) = await FriendsAsync(store);
        var (r1, one) = await store.AddCategoryAsync(a.Id, "Team", GameCode, Limits, CancellationToken.None);
        var (r2, two) = await store.AddCategoryAsync(a.Id, "Guild", GameCode, Limits, CancellationToken.None);
        Assert.Equal((MessengerResult.Ok, 1, MessengerResult.Ok, 2), (r1, one!.Id, r2, two!.Id));
        Assert.Equal(MessengerResult.LimitReached, (await store.AddCategoryAsync(a.Id, "Third", GameCode, Limits, CancellationToken.None)).Result);
        Assert.Equal(MessengerResult.Ok, await store.SetFriendCategoryAsync(a.Id, b.Id, 0, 2, CancellationToken.None));
        Assert.Equal(MessengerResult.NotFound, await store.SetFriendCategoryAsync(a.Id, b.Id, 0, 1, CancellationToken.None));
        Assert.Equal(MessengerResult.NotFound, await store.SetFriendCategoryAsync(a.Id, b.Id, 2, 9, CancellationToken.None));
        Assert.Equal(MessengerResult.NotEmpty, await store.DeleteCategoryAsync(a.Id, 2, CancellationToken.None));
        Assert.Equal(MessengerResult.Ok, await store.DeleteCategoryAsync(a.Id, 1, CancellationToken.None));
        Assert.Equal(MessengerResult.Ok, await store.RenameCategoryAsync(a.Id, 2, "Friends", CancellationToken.None));
        Assert.Equal(MessengerResult.NotFound, await store.RenameCategoryAsync(a.Id, 1, "Gone", CancellationToken.None));
        var (r3, reused) = await store.AddCategoryAsync(a.Id, "Again", GameCode, Limits, CancellationToken.None);
        Assert.Equal((MessengerResult.Ok, 1), (r3, reused!.Id));
        var loaded = await store.LoadRelationsAsync(a.Id, CancellationToken.None);
        Assert.Equal([(1, "Again"), (2, "Friends")], loaded.Categories.Select(c => (c.Id, c.Name)));
        Assert.Equal(2, Assert.Single(loaded.Relations).CategoryId);
        Assert.Empty((await store.LoadRelationsAsync(b.Id, CancellationToken.None)).Categories);
    }

    [Fact]
    public async Task PruneRemovesRequestsAtOrBeforeTheCutoff()
    {
        var store = CreateStore();
        var a = await Upsert(store, "a", "A");
        var b = await Upsert(store, "b", "B");
        var c = await Upsert(store, "c", "C");
        await store.CreateRequestAsync(a.Id, c.Id, "", Now.AddHours(-2), Limits, CancellationToken.None);
        await store.CreateRequestAsync(b.Id, c.Id, "", Now, Limits, CancellationToken.None);
        Assert.Equal(1, await store.PruneRequestsAsync(Now.AddHours(-1), CancellationToken.None));
        Assert.Equal(b.Id, Assert.Single(await store.ListIncomingRequestsAsync(c.Id, CancellationToken.None)).From);
    }
}
