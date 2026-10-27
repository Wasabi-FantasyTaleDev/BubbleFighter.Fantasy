using BubbleFighter.Fantasy.Contracts.Messenger;
using Xunit;

namespace BubbleFighter.Fantasy.Messenger.Tests;

/// <summary>The messenger core over the in-memory store: tickets, relations, isolation, replay and events.</summary>
public sealed class MessengerServiceTests
{
    [Fact]
    public async Task TicketSignInIsSingleUseAndRejectsTamperingExpiryAndUnknownTenants()
    {
        using var fixture = new MessengerFixture();
        var ticket = fixture.Ticket("ann");
        Assert.NotNull(await fixture.Service.LoginAuthAsync(ticket, null, default));
        Assert.Null(await fixture.Service.LoginAuthAsync(ticket, null, default));

        var tampered = fixture.Ticket("bob");
        Assert.Null(await fixture.Service.LoginAuthAsync(tampered[..^2] + (tampered[^2] == 'A' ? "BA" : "AA"), null, default));
        Assert.Null(await fixture.Service.LoginAuthAsync("v1.not-a-ticket", null, default));

        var late = fixture.Ticket("cid");
        fixture.Clock.Advance(TimeSpan.FromSeconds(301));
        Assert.Null(await fixture.Service.LoginAuthAsync(late, null, default));

        using var other = new MessengerFixture();
        Assert.Null(await fixture.Service.LoginAuthAsync(other.Ticket("dan"), null, default));
    }

    [Fact]
    public async Task RequestAcceptCreatesMutualFriendshipWithPresenceAndEvents()
    {
        using var fixture = new MessengerFixture();
        var ann = await fixture.SignInAsync("ann");
        var bob = await fixture.SignInAsync("bob", "Bob");

        Assert.Equal(MessengerResult.Ok, await fixture.Service.RequestNewFriendAsync(ann.Token, null, "bob", "hello", default));
        var request = Assert.IsType<MessengerFriendRequestEvent>(Assert.Single(await fixture.DrainAsync(bob))).Request;
        Assert.Equal(("ann", "Bob", "hello"), (request.From.Nickname, request.To.Nickname, request.Message));
        Assert.Equal(MessengerResult.AlreadyPending, await fixture.Service.RequestNewFriendAsync(bob.Token, null, "ann", "", default));

        Assert.Equal(MessengerResult.Ok, await fixture.Service.ConfirmNewFriendAsync(bob.Token, null, request.Handle, MessengerFriendDecision.Accept, default));
        Assert.Contains(await fixture.DrainAsync(ann), e => e is MessengerFriendListChangedEvent);
        var annList = await fixture.Service.GetFriendListAsync(ann.Token, 0, default);
        var friend = Assert.Single(annList.Friends);
        Assert.Equal(("Bob", 0, MessengerPresenceStatus.Online, MessengerFixture.BubbleFighterKr), (friend.User.Nickname, friend.CategoryId, friend.Presence.Status, friend.User.GameCode));
        Assert.Equal("ann", Assert.Single((await fixture.Service.GetFriendListAsync(bob.Token, 0, default)).Friends).User.Nickname);
        Assert.Equal(MessengerResult.AlreadyFriends, await fixture.Service.RequestNewFriendAsync(ann.Token, null, "Bob", "", default));
        Assert.Equal(MessengerResult.NotFound, await fixture.Service.ConfirmNewFriendAsync(bob.Token, null, request.Handle, MessengerFriendDecision.Accept, default));
    }

    [Fact]
    public async Task PresenceChangesReachFriendsAndTimeOut()
    {
        using var fixture = new MessengerFixture();
        var (ann, bob) = await fixture.FriendsAsync();
        var cursor = (await fixture.Service.WaitEventsAsync(ann.Token, 0, TimeSpan.Zero, default)).Next;

        await fixture.Service.ChangeMyLevelAsync(bob.Token, 42, default);
        var level = Assert.IsType<MessengerPresenceChangedEvent>(Assert.Single(await fixture.DrainAsync(ann, cursor)));
        Assert.Equal((bob.User.Id, MessengerPresenceStatus.Online, 42), (level.Friend, level.Presence.Status, level.Presence.Level));
        cursor = level.Sequence;

        await fixture.Service.LogoutMessengerAsync(bob.Token, default);
        var offline = Assert.IsType<MessengerPresenceChangedEvent>(Assert.Single(await fixture.DrainAsync(ann, cursor)));
        Assert.Equal(MessengerPresenceStatus.Offline, offline.Presence.Status);
        cursor = offline.Sequence;

        await fixture.Service.LoginMessengerAsync(bob.Token, default);
        fixture.Clock.Advance(TimeSpan.FromSeconds(fixture.Options.PresenceTimeoutSeconds + 1));
        await fixture.Service.WaitEventsAsync(ann.Token, cursor, TimeSpan.Zero, default);
        await fixture.Service.SweepAsync(default);
        var events = await fixture.DrainAsync(ann, cursor);
        Assert.Equal([MessengerPresenceStatus.Online, MessengerPresenceStatus.Offline],
            events.OfType<MessengerPresenceChangedEvent>().Select(e => e.Presence.Status));
        Assert.Equal(MessengerPresenceStatus.Offline, Assert.Single((await fixture.Service.GetFriendListAsync(ann.Token, 0, default)).Friends).Presence.Status);
    }

    [Fact]
    public async Task TenantsAreIsolated()
    {
        using var fixture = new MessengerFixture();
        var ann = await fixture.SignInAsync("ann");
        var mallory = await fixture.SignInAsync("mallory", "ann2", tenant: "beta");
        await fixture.SignInAsync("ann-beta", "AnnBeta", tenant: "beta");
        Assert.Equal(MessengerResult.NotFound, await fixture.Service.RequestNewFriendAsync(ann.Token, null, "AnnBeta", "", default));
        Assert.Equal(MessengerResult.NotFound, await fixture.Service.RequestNewFriendAsync(mallory.Token, null, "ann", "", default));
        Assert.Equal(MessengerResult.NotFound, await fixture.Service.BlockFriendAsync(mallory.Token, null, ann.User.Id, true, default));
        Assert.Equal(MessengerResult.NotFound, await fixture.Service.DeleteFriendFromCategoryAsync(mallory.Token, null, ann.User.Id, 0, default));
        Assert.NotEqual(ann.User.Id, (await fixture.SignInAsync("ann", tenant: "beta")).User.Id);
    }

    [Fact]
    public async Task RequestKeysReplayTheFirstResultWithoutRepeatingTheChange()
    {
        using var fixture = new MessengerFixture();
        var ann = await fixture.SignInAsync("ann");
        var bob = await fixture.SignInAsync("bob");
        Assert.Equal(MessengerResult.Ok, await fixture.Service.RequestNewFriendAsync(ann.Token, "k1", "bob", "", default));
        Assert.Equal(MessengerResult.Ok, await fixture.Service.RequestNewFriendAsync(ann.Token, "k1", "bob", "", default));
        Assert.Single(await fixture.DrainAsync(bob));
        Assert.Equal(MessengerResult.AlreadyPending, await fixture.Service.RequestNewFriendAsync(ann.Token, "k2", "bob", "", default));
        await Assert.ThrowsAsync<ArgumentException>(async () => await fixture.Service.RequestNewFriendAsync(ann.Token, "bad key", "bob", "", default));
    }

    [Fact]
    public async Task CategoriesHoldFriendsAndOnlyEmptyOnesCanBeDeleted()
    {
        using var fixture = new MessengerFixture(o => o.MaxCategories = 2);
        var (ann, bob) = await fixture.FriendsAsync();
        var (created, category) = await fixture.Service.AddCategoryAsync(ann.Token, null, "Team", default);
        Assert.Equal(MessengerResult.Ok, created);
        Assert.Equal((1, MessengerFixture.BubbleFighterKr), (category!.Id, category.GameCode));
        Assert.Equal(MessengerResult.Ok, (await fixture.Service.AddCategoryAsync(ann.Token, null, "Other", default)).Result);
        Assert.Equal(MessengerResult.LimitReached, (await fixture.Service.AddCategoryAsync(ann.Token, null, "Third", default)).Result);
        Assert.Equal(MessengerResult.Invalid, (await fixture.Service.AddCategoryAsync(ann.Token, null, new string('x', 33), default)).Result);

        Assert.Equal(MessengerResult.Ok, await fixture.Service.AddFriendToCategoryAsync(ann.Token, null, bob.User.Id, 1, default));
        Assert.Equal(MessengerResult.NotFound, await fixture.Service.AddFriendToCategoryAsync(ann.Token, null, bob.User.Id, 2, default));
        Assert.Equal(MessengerResult.NotEmpty, await fixture.Service.DeleteCategoryAsync(ann.Token, null, 1, default));
        Assert.Equal(MessengerResult.Ok, await fixture.Service.MoveFriendCategoryAsync(ann.Token, null, bob.User.Id, 1, 2, default));
        Assert.Equal(MessengerResult.Ok, await fixture.Service.ChangeCategoryNameAsync(ann.Token, null, 2, "Renamed", default));
        Assert.Equal(MessengerResult.Ok, await fixture.Service.DeleteCategoryAsync(ann.Token, null, 1, default));
        var list = await fixture.Service.GetFriendListAsync(ann.Token, 0, default);
        Assert.Equal(("Renamed", 2), (Assert.Single(list.Categories).Name, Assert.Single(list.Friends).CategoryId));
        Assert.Equal(0, Assert.Single((await fixture.Service.GetFriendListAsync(bob.Token, 0, default)).Friends).CategoryId);
    }

    [Fact]
    public async Task DeletingAFriendEndsTheFriendshipForBoth()
    {
        using var fixture = new MessengerFixture();
        var (ann, bob) = await fixture.FriendsAsync();
        Assert.Equal(MessengerResult.NotFound, await fixture.Service.DeleteFriendFromCategoryAsync(ann.Token, null, bob.User.Id, 3, default));
        Assert.Equal(MessengerResult.Ok, await fixture.Service.DeleteFriendFromCategoryAsync(ann.Token, null, bob.User.Id, 0, default));
        Assert.Empty((await fixture.Service.GetFriendListAsync(ann.Token, 0, default)).Friends);
        Assert.Empty((await fixture.Service.GetFriendListAsync(bob.Token, 0, default)).Friends);
        Assert.Contains(await fixture.DrainAsync(bob), e => e is MessengerFriendListChangedEvent);
    }

    [Fact]
    public async Task BlockingHidesPresenceFromTheBlockedFriend()
    {
        using var fixture = new MessengerFixture();
        var (ann, bob) = await fixture.FriendsAsync();
        Assert.Equal(MessengerResult.Ok, await fixture.Service.BlockFriendAsync(ann.Token, null, bob.User.Id, true, default));
        var seen = Assert.Single((await fixture.Service.GetFriendListAsync(bob.Token, 0, default)).Friends);
        Assert.Equal(MessengerPresenceStatus.Offline, seen.Presence.Status);
        Assert.True(Assert.Single((await fixture.Service.GetFriendListAsync(ann.Token, 0, default)).Friends).Blocked);
        Assert.Equal(MessengerPresenceStatus.Offline,
            (await fixture.DrainAsync(bob)).OfType<MessengerPresenceChangedEvent>().Last().Presence.Status);
        Assert.Equal(MessengerResult.Ok, await fixture.Service.BlockFriendAsync(ann.Token, null, bob.User.Id, false, default));
        Assert.Equal(MessengerPresenceStatus.Online, Assert.Single((await fixture.Service.GetFriendListAsync(bob.Token, 0, default)).Friends).Presence.Status);
    }

    [Fact]
    public async Task DismissedRequestsStayPendingAndReturnAtTheNextSignIn()
    {
        using var fixture = new MessengerFixture();
        var ann = await fixture.SignInAsync("ann");
        var bob = await fixture.SignInAsync("bob");
        await fixture.Service.RequestNewFriendAsync(ann.Token, null, "bob", "", default);
        var handle = Assert.IsType<MessengerFriendRequestEvent>(Assert.Single(await fixture.DrainAsync(bob))).Request.Handle;
        Assert.Equal(MessengerResult.Ok, await fixture.Service.ConfirmNewFriendAsync(bob.Token, null, handle, MessengerFriendDecision.Dismiss, default));
        await fixture.Service.LogoutAuthAsync(bob.Token, default);

        var again = await fixture.SignInAsync("bob");
        Assert.Equal(handle, Assert.IsType<MessengerFriendRequestEvent>(Assert.Single(await fixture.DrainAsync(again))).Request.Handle);
        Assert.Equal(MessengerResult.Ok, await fixture.Service.ConfirmNewFriendAsync(again.Token, null, handle, MessengerFriendDecision.Decline, default));
        Assert.Empty((await fixture.Service.GetFriendListAsync(ann.Token, 0, default)).Friends);
    }

    [Fact]
    public async Task BoundsAreEnforced()
    {
        using var fixture = new MessengerFixture(o => { o.MaxFriends = 1; o.EventQueue = 8; o.PageSize = 1; });
        var (ann, _) = await fixture.FriendsAsync();
        var cid = await fixture.SignInAsync("cid");
        await fixture.Service.RequestNewFriendAsync(cid.Token, null, "ann", "", default);
        var handle = (await fixture.DrainAsync(ann)).OfType<MessengerFriendRequestEvent>().Single().Request.Handle;
        Assert.Equal(MessengerResult.LimitReached, await fixture.Service.ConfirmNewFriendAsync(ann.Token, null, handle, MessengerFriendDecision.Accept, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await fixture.Service.GetFriendListAsync(ann.Token, 1, default));

        for (var i = 0; i < 10; i++) await fixture.Service.AddCategoryAsync(ann.Token, null, $"c{i}", default);
        var batch = await fixture.Service.WaitEventsAsync(ann.Token, 1, TimeSpan.Zero, default);
        Assert.True(batch.Reset);
        Assert.Equal(8, batch.Events.Count);
        Assert.True((await fixture.Service.WaitEventsAsync(ann.Token, 999, TimeSpan.Zero, default)).Reset);
        Assert.Equal(MessengerResult.Invalid, await fixture.Service.RequestNewFriendAsync(ann.Token, null, "ann", "", default));
        Assert.Equal(MessengerResult.Invalid, await fixture.Service.RequestNewFriendAsync(ann.Token, null, "bob", new string('m', 257), default));
    }

    [Fact]
    public async Task ExpiredAndEndedSessionsAreUnauthorized()
    {
        using var fixture = new MessengerFixture();
        var ann = await fixture.SignInAsync("ann");
        fixture.Clock.Advance(TimeSpan.FromSeconds(fixture.Options.SessionSeconds + 1));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await fixture.Service.GetFriendListAsync(ann.Token, 0, default));
        var bob = await fixture.SignInAsync("bob");
        await fixture.Service.LogoutAuthAsync(bob.Token, default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await fixture.Service.GetFriendListAsync(bob.Token, 0, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await fixture.Service.GetFriendListAsync("forged", 0, default));
    }
}
