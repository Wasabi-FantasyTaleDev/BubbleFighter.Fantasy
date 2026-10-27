using BubbleFighter.Fantasy.Contracts.Messenger;
using Xunit;

namespace BubbleFighter.Fantasy.Messenger.Tests;

/// <summary>Regional game codes: one stored friendship, shown under the code each session's client announced.</summary>
public sealed class MessengerGameCodeTests
{
    [Fact]
    public async Task EachSessionSeesFriendsAndCategoriesUnderItsOwnCode()
    {
        using var fixture = new MessengerFixture();
        var ann = await fixture.SignInAsync("ann", gameCode: MessengerFixture.BubbleFighterCn2);
        var bob = await fixture.SignInAsync("bob");
        Assert.Equal((MessengerFixture.BubbleFighterCn2, MessengerFixture.BubbleFighterCn2), (ann.GameCode, ann.User.GameCode));

        Assert.Equal(MessengerResult.Ok, await fixture.Service.RequestNewFriendAsync(bob.Token, null, "ann", "hi", default));
        var request = (await fixture.DrainAsync(ann)).OfType<MessengerFriendRequestEvent>().Single();
        Assert.Equal(MessengerFixture.BubbleFighterCn2, request.Request.From.GameCode);
        Assert.Equal(MessengerResult.Ok, await fixture.Service.ConfirmNewFriendAsync(ann.Token, null, request.Request.Handle, MessengerFriendDecision.Accept, default));

        var (_, category) = await fixture.Service.AddCategoryAsync(ann.Token, null, "Team", default);
        Assert.Equal(MessengerFixture.BubbleFighterCn2, category!.GameCode);
        var annList = await fixture.Service.GetFriendListAsync(ann.Token, 0, default);
        Assert.Equal(MessengerFixture.BubbleFighterCn2, annList.Friends.Single().User.GameCode);
        Assert.Equal(MessengerFixture.BubbleFighterCn2, annList.Categories.Single().GameCode);
        var bobList = await fixture.Service.GetFriendListAsync(bob.Token, 0, default);
        Assert.Equal(MessengerFixture.BubbleFighterKr, bobList.Friends.Single().User.GameCode);

        // The store keeps the game's own code, so a second session of the same account on another build is unaffected.
        Assert.Equal(MessengerFixture.BubbleFighterKr, (await fixture.Store.FindUserAsync(ann.User.Id, default))!.GameCode);
        var annKr = await fixture.SignInAsync("ann");
        Assert.Equal(MessengerFixture.BubbleFighterKr, (await fixture.Service.GetFriendListAsync(annKr.Token, 0, default)).Categories.Single().GameCode);
        Assert.Equal(MessengerFixture.BubbleFighterCn2, (await fixture.Service.GetFriendListAsync(ann.Token, 0, default)).Categories.Single().GameCode);
    }

    [Fact]
    public async Task CatalogLookupsFollowTheSessionCode()
    {
        using var fixture = new MessengerFixture(o => o.Games =
        [
            .. o.Games,
            new() { Code = NexonGameCode.kartrider, FullName = "KartRider", ShortName = "Kart" },
        ]);
        var ann = await fixture.SignInAsync("ann", gameCode: (uint)NexonGameCode.bubblefighter_0102220F);
        var games = await fixture.Service.GetSupportGameListAsync(ann.Token, default);
        Assert.Equal([(uint)NexonGameCode.bubblefighter_0102220F, (uint)NexonGameCode.kartrider], games.Select(g => g.Code));
        Assert.Equal("Bubble Fighter", (await fixture.Service.FindGameAsync(ann.Token, MessengerFixture.BubbleFighterCn2, default))!.FullName);
        Assert.Equal((uint)NexonGameCode.kartrider, (await fixture.Service.FindGameAsync(ann.Token, (uint)NexonGameCode.kartrider, default))!.Code);
        Assert.Null(await fixture.Service.FindGameAsync(ann.Token, (uint)NexonGameCode.bigshot, default));
    }

    [Fact]
    public async Task ACodeOfAnotherGameIsRefused()
    {
        using var fixture = new MessengerFixture();
        Assert.Null(await fixture.Service.LoginAuthAsync(fixture.Ticket("ann"), (uint)NexonGameCode.kartrider, default));
        Assert.Null(await fixture.Service.LoginAuthAsync(fixture.Ticket("ann"), 0x0109220F, default));
    }

    [Fact]
    public void AliasesMustBeUniqueAcrossGames()
    {
        var options = new MessengerOptions
        {
            Games =
            [
                new() { Code = NexonGameCode.bubblefighter, Aliases = [NexonGameCode.bubblefighter_cn2], FullName = "a", ShortName = "a" },
                new() { Code = NexonGameCode.kartrider, Aliases = [NexonGameCode.bubblefighter_cn2], FullName = "b", ShortName = "b" },
            ],
        };
        Assert.Contains(options.Validate(), r => r.ErrorMessage!.Contains("unique", StringComparison.Ordinal));
    }
}
