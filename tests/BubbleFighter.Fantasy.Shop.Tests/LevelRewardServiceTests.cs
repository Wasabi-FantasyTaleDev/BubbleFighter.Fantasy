using BubbleFighter.Fantasy.Contracts.Presents;
using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Wallet;
using BubbleFighter.Fantasy.Shop.Data;
using BubbleFighter.Fantasy.Shop.Presents;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Shop.Tests;

// What the level gifts do on top of the gift box: every level a rise crosses is granted once, however often the same
// rise is announced, and a receiver who is logged in on the build is told with the ordinary new-gift notice.
public sealed partial class PresentServiceTests
{
    private const int SecondKey = 3000002;

    private static LevelRewardService Rewards(Fixture f, params (int Level, int[] Keys)[] table)
    {
        f.Options.LevelRewards = [.. table.Select(entry => new ShopLevelReward { Level = entry.Level, StockKeys = entry.Keys })];
        f.Options.LevelRewardSender = "Staff";
        f.Options.LevelRewardMessage = "Level {level}!";
        return new LevelRewardService(f.Options, f.Presents, f.Service, f.Progress, TimeProvider.System, NullLogger.Instance);
    }

    private static PlayerLevelRaised Raised(Fixture f, int from, int to, string protocol = Build + "/game") =>
        new(protocol, f.ReceiverPlayer.UserNum, from, to);

    private static async Task<PresentRecord[]> BoxOf(Fixture f) =>
        [.. (await f.Presents.ListReceivedAsync(Build, f.ReceiverPlayer.UserNum, 0, 16, default)).Records.Reverse()];

    [Fact]
    public async Task ARiseOfOneLevelGrantsThatLevelsGiftsFromTheConfiguredSenderAndNotifiesTheLiveReceiver()
    {
        var f = await new Fixture().StartAsync();
        var rewards = Rewards(f, (2, [GiftKey]), (3, [GiftKey]));

        await rewards.OnLevelRaisedAsync(Raised(f, 1, 2), default);

        var gift = Assert.Single(await BoxOf(f));
        Assert.Equal((GiftKey, "Staff", "Level 2!", PresentState.Pending), (gift.StockKey, gift.SenderNickname, gift.Message, gift.State));
        Assert.Equal(PresentLimits.SystemSenderUserNum, gift.SenderUserNum);
        Assert.Equal(new PresentNoticeSent(true, 1), Assert.Single(f.Notices.Items));
        Assert.Same(f.ReceiverSession, Assert.Single(f.Notices.Sessions));
        // Nothing is charged to anybody and nothing is in a bag until the receiver takes the gift.
        Assert.Empty(await f.BagOf(f.ReceiverPlayer));
        Assert.Null(await f.Wallets.FindByUserNumAsync(f.SenderPlayer.UserNum, default));
    }

    [Fact]
    public async Task ARiseOverSeveralLevelsGrantsEachConfiguredLevelOnceWithOneNotice()
    {
        var f = await new Fixture().StartAsync();
        var rewards = Rewards(f, (2, [GiftKey]), (3, [GiftKey, SecondKey]), (4, [GiftKey]), (9, [GiftKey]));

        await rewards.OnLevelRaisedAsync(Raised(f, 1, 4), default);

        var box = await BoxOf(f);
        Assert.Equal(["Level 2!", "Level 3!", "Level 3!", "Level 4!"], box.Select(gift => gift.Message));
        Assert.Equal([GiftKey, GiftKey, SecondKey, GiftKey], box.Select(gift => gift.StockKey));
        Assert.Equal(new PresentNoticeSent(true, 4), Assert.Single(f.Notices.Items));
    }

    [Fact]
    public async Task ALevelWithNoRewardAndARiseThatNeedsNoneGrantNothingAndSendNoNotice()
    {
        var f = await new Fixture().StartAsync();
        var rewards = Rewards(f, (5, [GiftKey]));

        await rewards.OnLevelRaisedAsync(Raised(f, 1, 4), default);
        // The event of another build is not this row's business.
        await rewards.OnLevelRaisedAsync(Raised(f, 1, 5, "cn-717/game"), default);
        // An old level not below the new one crosses nothing.
        await rewards.OnLevelRaisedAsync(Raised(f, 5, 5), default);
        await Rewards(f).OnLevelRaisedAsync(Raised(f, 1, 9), default);

        Assert.Empty(await BoxOf(f));
        Assert.Empty(f.Notices.Items);
    }

    [Fact]
    public async Task ARepeatedOrOverlappingRiseGrantsEachLevelOnlyOnce()
    {
        var f = await new Fixture().StartAsync();
        var rewards = Rewards(f, (2, [GiftKey]), (3, [GiftKey]));

        await rewards.OnLevelRaisedAsync(Raised(f, 1, 3), default);
        await rewards.OnLevelRaisedAsync(Raised(f, 1, 3), default);
        // A second recording that starts from a stale old level only adds what was not granted yet.
        await rewards.OnLevelRaisedAsync(Raised(f, 2, 3), default);

        Assert.Equal(2, (await BoxOf(f)).Length);
        Assert.Single(f.Notices.Items);
    }

    [Fact]
    public async Task ConcurrentRisesGrantEachLevelOnce()
    {
        var f = await new Fixture().StartAsync();
        // Logged out, so the notice recorder (a plain list) is not written from several threads.
        f.Sessions.Remove(f.ReceiverSession);
        var rewards = Rewards(f, (2, [GiftKey]), (3, [GiftKey, SecondKey]), (4, [GiftKey]));

        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
            rewards.OnLevelRaisedAsync(Raised(f, 1, 4), default).AsTask())));

        Assert.Equal(4, (await BoxOf(f)).Length);
        Assert.Empty(f.Notices.Items);
    }

    [Fact]
    public async Task AGrantedGiftSurvivesTheReceiverRemovingItAndTheLevelIsNotGrantedAgain()
    {
        var f = await new Fixture().StartAsync();
        var rewards = Rewards(f, (2, [GiftKey]));
        await rewards.OnLevelRaisedAsync(Raised(f, 1, 2), default);
        var gift = Assert.Single(await BoxOf(f));

        await f.Service.ActionAsync(f.ReceiverSession, new PresentActionRequested(PresentAction.DeleteReceived, gift.Id), default);
        await rewards.OnLevelRaisedAsync(Raised(f, 1, 2), default);

        Assert.Empty(await BoxOf(f));
    }

    [Fact]
    public async Task AReceiverTakesALevelGiftThroughTheOrdinaryAcceptPathForNoCharge()
    {
        var f = await new Fixture().StartAsync();
        await Rewards(f, (2, [GiftKey])).OnLevelRaisedAsync(Raised(f, 1, 2), default);
        var gift = Assert.Single(await BoxOf(f));

        await f.Service.ActionAsync(f.ReceiverSession, new PresentActionRequested(PresentAction.Receive, gift.Id), default);

        Assert.True(Assert.Single(f.Actions.Items).Succeeded);
        Assert.Single(await f.BagOf(f.ReceiverPlayer));
        Assert.Equal(PresentState.Accepted,
            (await f.Presents.FindReceivedAsync(Build, f.ReceiverPlayer.UserNum, gift.Id, default))!.State);
    }

    [Fact]
    public async Task AMissingPlayerIsLoggedAndGrantsNothing()
    {
        var f = await new Fixture().StartAsync();
        var rewards = Rewards(f, (2, [GiftKey]));

        await rewards.OnLevelRaisedAsync(new PlayerLevelRaised(Build + "/game", 987_654, 1, 2), default);

        Assert.Empty(await BoxOf(f));
        Assert.Empty(f.Notices.Items);
    }

    [Fact]
    public void TheClaimKeyIsTheLevelAndFitsTheStoresKeyWidth()
    {
        Assert.Equal("level:12", LevelRewardService.ClaimKey(12));
        Assert.True(LevelRewardService.ClaimKey(ShopOptions.MaxRewardLevel).Length <= PresentLimits.MaxClaimKeyChars);
    }

    private static LoginMenuInstalled RewardLogin(Fixture f, string protocol = Build + "/game") => new(protocol,
        f.ReceiverSession, new LoginSucceeded((int)f.ReceiverPlayer.UserNum, "account", "receiver", 1,
            DateTime.UnixEpoch, 300001, [300001], new PlayerWallet(f.ReceiverPlayer.UserNum, 0, 0), [], []));

    [Fact]
    public async Task LoginRecoversSavedLevelsWhoseEventNeverRanAndDoesNotDuplicateGrantedLevels()
    {
        var f = await new Fixture().StartAsync();
        await f.Wallets.UpsertAsync(new(f.ReceiverPlayer.UserNum, 0, 0), default);
        await f.Progress.SettleRoundAsync(new("m-restart", f.ReceiverPlayer.UserNum, 0, _ => new(f.ReceiverPlayer.UserNum, 4, 100)), default);
        // The process stopped after committing experience, before publishing the level event.
        var restarted = Rewards(f, (2, [GiftKey]), (3, [GiftKey]), (4, [GiftKey]), (5, [GiftKey]));
        await restarted.LoginAsync(RewardLogin(f, "cn-717/game"), default);
        Assert.Empty(await BoxOf(f));
        await restarted.LoginAsync(RewardLogin(f), default);
        await f.Service.LoginAsync(RewardLogin(f), default);
        Assert.Equal(3, (await BoxOf(f)).Length);
        Assert.Equal(new PresentNoticeSent(true, 3), Assert.Single(f.Notices.Items));
        await Rewards(f, (2, [GiftKey]), (3, [GiftKey]), (4, [GiftKey])).LoginAsync(RewardLogin(f), default);
        Assert.Equal(3, (await BoxOf(f)).Length);
    }

    [Fact]
    public async Task LaterRiseRecoversEarlierOutstandingRewardsAndDoesNotLoopOverUnconfiguredLevels()
    {
        var f = await new Fixture().StartAsync();
        await Rewards(f, (2, [GiftKey])).OnLevelRaisedAsync(Raised(f, 1, 2), default);
        await Rewards(f, (2, [GiftKey]), (3, [GiftKey]), (4, [GiftKey]))
            .OnLevelRaisedAsync(Raised(f, 3, int.MaxValue), default);
        Assert.Equal(3, (await BoxOf(f)).Length);
    }

    [Fact]
    public async Task SystemRewardsCanContainOffSaleStockAndLucciOnlySetsAndAreTakenOnce()
    {
        var f = await new Fixture().StartAsync();
        const int offSale = 7000001, cashSet = 7000002;
        f.RewardGifts[offSale] = new ShopGift(offSale, 0, 0, [Card.Stocks[0] with { StockId = offSale, IsOnSale = false }]);
        f.RewardGifts[cashSet] = new ShopGift(cashSet, cashSet, 5000, []);
        await Rewards(f, (2, [offSale, cashSet])).OnLevelRaisedAsync(Raised(f, 1, 2), default);
        var gifts = await BoxOf(f);
        foreach (var gift in gifts)
        {
            await f.Service.ActionAsync(f.ReceiverSession, new(PresentAction.Receive, gift.Id), default);
            await f.Service.ActionAsync(f.ReceiverSession, new(PresentAction.Receive, gift.Id), default);
        }
        Assert.Single(await f.BagOf(f.ReceiverPlayer));
        Assert.Equal(f.Options.StartingLucci + 5000, (await f.Wallets.FindByUserNumAsync(f.ReceiverPlayer.UserNum, default))!.Lucci);
        Assert.Equal([true, false, true, false], f.Actions.Items.Select(reply => reply.Succeeded));
    }
}
