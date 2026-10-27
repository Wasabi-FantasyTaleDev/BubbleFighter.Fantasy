using BubbleFighter.Fantasy.Contracts.Messenger;
using Xunit;

namespace BubbleFighter.Fantasy.Messenger.Tests;

/// <summary>Friend chats: opening, delivery to both members, reopening closed windows, blocks and limits.</summary>
public sealed class MessengerChatTests
{
    [Fact]
    public async Task OpeningShowsTheCallerAWindowAndTheFirstLineOpensTheFriends()
    {
        using var fixture = new MessengerFixture();
        var (ann, bob) = await fixture.FriendsAsync();
        var annCursor = (await fixture.Service.WaitEventsAsync(ann.Token, 0, TimeSpan.Zero, default)).Next;
        var bobCursor = (await fixture.Service.WaitEventsAsync(bob.Token, 0, TimeSpan.Zero, default)).Next;

        var (result, chat) = await fixture.Service.RequestChatSessionAsync(ann.Token, bob.User.Id, default);
        Assert.Equal(MessengerResult.Ok, result);
        Assert.Equal(["ann", "bob"], chat!.Members.Select(m => m.User.Nickname).Order());
        Assert.All(chat.Members, m => Assert.Equal(MessengerPresenceStatus.Online, m.Presence.Status));
        var opened = Assert.IsType<MessengerChatOpenedEvent>(Assert.Single(await fixture.DrainAsync(ann, annCursor)));
        Assert.Equal(chat.Id, opened.Chat.Id);
        Assert.Empty(await fixture.DrainAsync(bob, bobCursor));

        // Asking again reuses the live chat.
        Assert.Equal(chat.Id, (await fixture.Service.RequestChatSessionAsync(bob.Token, ann.User.Id, default)).Chat!.Id);
        bobCursor = (await fixture.Service.WaitEventsAsync(bob.Token, bobCursor, TimeSpan.Zero, default)).Next;
        annCursor = (await fixture.Service.WaitEventsAsync(ann.Token, annCursor, TimeSpan.Zero, default)).Next;

        Assert.Equal(MessengerResult.Ok, await fixture.Service.SendChatMessageAsync(ann.Token, chat.Id, "hello", default));
        var line = Assert.IsType<MessengerChatMessageEvent>(Assert.Single(await fixture.DrainAsync(bob, bobCursor)));
        Assert.Equal((chat.Id, "ann", "hello"), (line.ChatId, line.From.Nickname, line.Text));
        // The sender's own client shows its line only from the echo.
        Assert.Equal("hello", Assert.IsType<MessengerChatMessageEvent>(Assert.Single(await fixture.DrainAsync(ann, annCursor))).Text);
    }

    [Fact]
    public async Task AClosedWindowOpensAgainWithTheNextLineAndTheChatEndsWhenNobodyShowsIt()
    {
        using var fixture = new MessengerFixture();
        var (ann, bob) = await fixture.FriendsAsync();
        var chat = (await fixture.Service.RequestChatSessionAsync(ann.Token, bob.User.Id, default)).Chat!;
        Assert.Equal(MessengerResult.Ok, await fixture.Service.SendChatMessageAsync(ann.Token, chat.Id, "one", default));
        var bobCursor = (await fixture.Service.WaitEventsAsync(bob.Token, 0, TimeSpan.Zero, default)).Next;

        Assert.Equal(MessengerResult.Ok, await fixture.Service.CloseChatSessionAsync(bob.Token, chat.Id, default));
        Assert.Equal(MessengerResult.Ok, await fixture.Service.SendChatMessageAsync(ann.Token, chat.Id, "two", default));
        var events = await fixture.DrainAsync(bob, bobCursor);
        Assert.IsType<MessengerChatOpenedEvent>(events[0]);
        Assert.Equal("two", Assert.IsType<MessengerChatMessageEvent>(events[1]).Text);

        Assert.Equal(MessengerResult.Ok, await fixture.Service.CloseChatSessionAsync(bob.Token, chat.Id, default));
        Assert.Equal(MessengerResult.Ok, await fixture.Service.CloseChatSessionAsync(ann.Token, chat.Id, default));
        Assert.Null(await fixture.Service.GetChatSessionAsync(ann.Token, chat.Id, default));
        Assert.Equal(MessengerResult.NotFound, await fixture.Service.SendChatMessageAsync(ann.Token, chat.Id, "three", default));
    }

    [Fact]
    public async Task OnlyFriendsWithoutBlocksMayChatAndOutsidersCannotReadTheChat()
    {
        using var fixture = new MessengerFixture(o => o.MaxChatMessage = 8);
        var (ann, bob) = await fixture.FriendsAsync();
        var cid = await fixture.SignInAsync("cid");

        Assert.Equal(MessengerResult.NotFound, (await fixture.Service.RequestChatSessionAsync(ann.Token, cid.User.Id, default)).Result);
        Assert.Equal(MessengerResult.Invalid, (await fixture.Service.RequestChatSessionAsync(ann.Token, ann.User.Id, default)).Result);
        var chat = (await fixture.Service.RequestChatSessionAsync(ann.Token, bob.User.Id, default)).Chat!;
        Assert.Null(await fixture.Service.GetChatSessionAsync(cid.Token, chat.Id, default));
        Assert.Equal(MessengerResult.NotFound, await fixture.Service.SendChatMessageAsync(cid.Token, chat.Id, "hi", default));
        Assert.Equal(MessengerResult.Invalid, await fixture.Service.SendChatMessageAsync(ann.Token, chat.Id, "too long!", default));
        Assert.Equal(MessengerResult.Invalid, await fixture.Service.SendChatMessageAsync(ann.Token, chat.Id, " ", default));

        Assert.Equal(MessengerResult.Ok, await fixture.Service.BlockFriendAsync(bob.Token, null, ann.User.Id, true, default));
        Assert.Equal(MessengerResult.Refused, await fixture.Service.SendChatMessageAsync(ann.Token, chat.Id, "hi", default));
        Assert.Equal(MessengerResult.Refused, (await fixture.Service.RequestChatSessionAsync(ann.Token, bob.User.Id, default)).Result);
    }

    [Fact]
    public async Task ChatsAreBoundedAndEndWhenTheirAccountsSignOut()
    {
        using var fixture = new MessengerFixture(o => o.MaxChats = 1);
        var (ann, bob) = await fixture.FriendsAsync();
        var (cid, dan) = await fixture.FriendsAsync("cid", "dan");
        var chat = (await fixture.Service.RequestChatSessionAsync(ann.Token, bob.User.Id, default)).Chat!;
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await fixture.Service.RequestChatSessionAsync(cid.Token, dan.User.Id, default));

        await fixture.Service.LogoutAuthAsync(ann.Token, default);
        Assert.Null(await fixture.Service.GetChatSessionAsync(bob.Token, chat.Id, default));
        Assert.Equal(MessengerResult.Ok, (await fixture.Service.RequestChatSessionAsync(cid.Token, dan.User.Id, default)).Result);
    }

    [Fact]
    public async Task ChatRecordsCarryEachViewersGameCode()
    {
        using var fixture = new MessengerFixture();
        var ann = await fixture.SignInAsync("ann");
        var bob = await fixture.SignInAsync("bob", gameCode: MessengerFixture.BubbleFighterCn2);
        Assert.Equal(MessengerResult.Ok, await fixture.Service.RequestNewFriendAsync(ann.Token, null, "bob", "", default));
        var request = (await fixture.DrainAsync(bob)).OfType<MessengerFriendRequestEvent>().Single();
        Assert.Equal(MessengerResult.Ok, await fixture.Service.ConfirmNewFriendAsync(bob.Token, null, request.Request.Handle, MessengerFriendDecision.Accept, default));
        var cursor = (await fixture.Service.WaitEventsAsync(bob.Token, 0, TimeSpan.Zero, default)).Next;

        var chat = (await fixture.Service.RequestChatSessionAsync(ann.Token, bob.User.Id, default)).Chat!;
        Assert.All(chat.Members, m => Assert.Equal(MessengerFixture.BubbleFighterKr, m.User.GameCode));
        Assert.All((await fixture.Service.GetChatSessionAsync(bob.Token, chat.Id, default))!.Members, m => Assert.Equal(MessengerFixture.BubbleFighterCn2, m.User.GameCode));
        Assert.Equal(MessengerResult.Ok, await fixture.Service.SendChatMessageAsync(ann.Token, chat.Id, "hi", default));
        var events = await fixture.DrainAsync(bob, cursor);
        Assert.All(Assert.IsType<MessengerChatOpenedEvent>(events[0]).Chat.Members, m => Assert.Equal(MessengerFixture.BubbleFighterCn2, m.User.GameCode));
        Assert.Equal(MessengerFixture.BubbleFighterCn2, Assert.IsType<MessengerChatMessageEvent>(events[1]).From.GameCode);
    }
}
