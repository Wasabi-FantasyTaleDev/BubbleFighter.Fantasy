using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Presents;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Contracts.Wallet;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Tests;

/// <summary>
/// What every gift store must do the same way: charge and store in one step, list per build and per side, deliver a gift
/// exactly once through the purchase that takes it, and refuse without changing anything.
/// </summary>
public abstract class PresentStoreContractTests : IDisposable
{
    private const string Build = "kr-812";

    /// <summary>The repositories of one fresh store.</summary>
    protected sealed record Stores(IPresentStore Presents, IPlayerRepository Players, IShopPurchaseStore Purchases,
        IInventoryRepository Inventory, IPlayerWalletRepository Wallets);

    /// <summary>Creates a fresh, empty store.</summary>
    protected abstract Stores Create();

    /// <inheritdoc />
    public virtual void Dispose() => GC.SuppressFinalize(this);

    private static async Task<PresentPlayer> NewPlayer(Stores stores, string name)
    {
        var record = await stores.Players.CreateAsync("account-" + name, name, default);
        return new PresentPlayer(record.UserNum, name);
    }

    private static PresentSendCommand Send(PresentPlayer from, PresentPlayer to, int price = 100, string money = "lucci",
        int lucci = 1000, long at = 1_000, int max = 100, string build = Build, string message = "hello") =>
        new(build, from, to, 1000123, 2000001, 207026, message, money, price, new PlayerWallet(from.UserNum, lucci, 50), at, max);

    private static ShopPurchaseCommand Take(PresentPlayer receiver, long presentId) =>
        new(new PlayerWallet(receiver.UserNum, 0, 0), 1000123, 0, "lucci", 0, 10_000, [new(207026, 1)]) { PresentId = presentId };

    [Fact]
    public async Task ASendChargesTheSenderStoresTheGiftAndListsItToBothPlayers()
    {
        var stores = Create();
        var (sender, receiver) = (await NewPlayer(stores, "sender"), await NewPlayer(stores, "receiver"));
        var result = await stores.Presents.SendAsync(Send(sender, receiver), default);

        Assert.Equal(PresentSendOutcome.Sent, result.Outcome);
        Assert.Equal(900, result.Wallet!.Lucci);
        Assert.Equal(50, result.Wallet.Cash);
        Assert.Equal(900, (await stores.Wallets.FindByUserNumAsync(sender.UserNum, default))!.Lucci);
        var gift = result.Present!;
        Assert.True(gift.Id > 0);
        Assert.Equal((Build, PresentState.Pending, 1000123), (gift.ProfileId, gift.State, gift.StockKey));
        Assert.Equal(("sender", "receiver", "hello"), (gift.SenderNickname, gift.ReceiverNickname, gift.Message));

        var received = await stores.Presents.ListReceivedAsync(Build, receiver.UserNum, 0, 4, default);
        Assert.Equal(1, received.Total);
        Assert.Equal(gift.Id, Assert.Single(received.Records).Id);
        Assert.Equal(1, (await stores.Presents.ListSentAsync(Build, sender.UserNum, 0, 4, default)).Total);
        Assert.Equal(0, (await stores.Presents.ListSentAsync(Build, receiver.UserNum, 0, 4, default)).Total);
        Assert.Equal(1, await stores.Presents.CountPendingAsync(Build, receiver.UserNum, default));
        Assert.Equal(0, await stores.Presents.CountPendingAsync(Build, sender.UserNum, default));
        // The charge is audited like a purchase.
        Assert.Equal(100, Assert.Single(await stores.Purchases.ListRecentAsync(sender.UserNum, 8, default)).Price);
    }

    [Fact]
    public async Task AStoredWalletIsChargedInsteadOfTheSeedAndACashPriceTakesCash()
    {
        var stores = Create();
        var (sender, receiver) = (await NewPlayer(stores, "sender"), await NewPlayer(stores, "receiver"));
        await stores.Wallets.UpsertAsync(new PlayerWallet(sender.UserNum, 300, 70), default);
        var lucci = await stores.Presents.SendAsync(Send(sender, receiver, price: 100, lucci: 9999), default);
        Assert.Equal(new PlayerWallet(sender.UserNum, 200, 70), lucci.Wallet);
        var cash = await stores.Presents.SendAsync(Send(sender, receiver, price: 20, money: "cash"), default);
        Assert.Equal(new PlayerWallet(sender.UserNum, 200, 50), cash.Wallet);
    }

    [Fact]
    public async Task EveryRefusalLeavesTheWalletTheAuditAndTheListsAsTheyWere()
    {
        var stores = Create();
        var (sender, receiver) = (await NewPlayer(stores, "sender"), await NewPlayer(stores, "receiver"));
        await stores.Presents.SendAsync(Send(sender, receiver, price: 1), default);
        var before = (await stores.Wallets.FindByUserNumAsync(sender.UserNum, default))!;

        Assert.Equal(PresentSendOutcome.InsufficientFunds,
            (await stores.Presents.SendAsync(Send(sender, receiver, price: 5000), default)).Outcome);
        Assert.Equal(PresentSendOutcome.ReceiverMissing,
            (await stores.Presents.SendAsync(Send(sender, new PresentPlayer(987_654, "ghost")), default)).Outcome);
        // One gift is stored for the receiver and sender already, so a cap of one is reached on both sides.
        Assert.Equal(PresentSendOutcome.ReceiverBoxFull, (await stores.Presents.SendAsync(Send(sender, receiver, max: 1), default)).Outcome);
        var third = await NewPlayer(stores, "third");
        Assert.Equal(PresentSendOutcome.SenderBoxFull, (await stores.Presents.SendAsync(Send(sender, third, max: 1), default)).Outcome);

        Assert.Equal(before, await stores.Wallets.FindByUserNumAsync(sender.UserNum, default));
        Assert.Single(await stores.Purchases.ListRecentAsync(sender.UserNum, 8, default));
        Assert.Equal(1, (await stores.Presents.ListSentAsync(Build, sender.UserNum, 0, 4, default)).Total);
        Assert.Equal(0, (await stores.Presents.ListReceivedAsync(Build, third.UserNum, 0, 4, default)).Total);
    }

    [Fact]
    public async Task ARepeatedOrConcurrentTakeGrantsTheItemExactlyOnce()
    {
        var stores = Create();
        var (sender, receiver) = (await NewPlayer(stores, "sender"), await NewPlayer(stores, "receiver"));
        var gift = (await stores.Presents.SendAsync(Send(sender, receiver), default)).Present!;

        var attempts = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
        {
            try { return await stores.Purchases.PurchaseAsync(Take(receiver, gift.Id), default); }
            catch (PresentUnavailableException) { return null; }
        })));

        Assert.Single(attempts, result => result is not null);
        var bag = await stores.Inventory.ListSlotsAsync(receiver.UserNum, InventoryBag.CharacterBag, 512, default);
        Assert.Equal(207026, Assert.Single(bag).ItemId);
        Assert.Single(await stores.Purchases.ListRecentAsync(receiver.UserNum, 8, default));
        Assert.Equal(PresentState.Accepted, (await stores.Presents.FindReceivedAsync(Build, receiver.UserNum, gift.Id, default))!.State);
        Assert.Equal(0, await stores.Presents.CountPendingAsync(Build, receiver.UserNum, default));

        // Asking again after the fact changes nothing either.
        await Assert.ThrowsAsync<PresentUnavailableException>(() => stores.Purchases.PurchaseAsync(Take(receiver, gift.Id), default).AsTask());
        Assert.Single(await stores.Inventory.ListSlotsAsync(receiver.UserNum, InventoryBag.CharacterBag, 512, default));
    }

    [Fact]
    public async Task AGiftNotPendingForThePlayerRefusesTheWholePurchaseWithoutAnyWrite()
    {
        var stores = Create();
        var (sender, receiver) = (await NewPlayer(stores, "sender"), await NewPlayer(stores, "receiver"));
        var thrown = (await stores.Presents.SendAsync(Send(sender, receiver), default)).Present!;
        var removed = (await stores.Presents.SendAsync(Send(sender, receiver), default)).Present!;
        var foreign = (await stores.Presents.SendAsync(Send(sender, receiver), default)).Present!;
        Assert.True(await stores.Presents.DiscardAsync(Build, receiver.UserNum, thrown.Id, default));
        Assert.True(await stores.Presents.DeleteReceivedAsync(Build, receiver.UserNum, removed.Id, default));

        foreach (var id in new[] { thrown.Id, removed.Id, 999_999 })
            await Assert.ThrowsAsync<PresentUnavailableException>(() => stores.Purchases.PurchaseAsync(Take(receiver, id), default).AsTask());
        // The sender is not the receiver of a gift it sent.
        await Assert.ThrowsAsync<PresentUnavailableException>(() => stores.Purchases.PurchaseAsync(Take(sender, foreign.Id), default).AsTask());

        Assert.Empty(await stores.Inventory.ListSlotsAsync(receiver.UserNum, InventoryBag.CharacterBag, 512, default));
        Assert.Empty(await stores.Inventory.ListSlotsAsync(sender.UserNum, InventoryBag.CharacterBag, 512, default));
        Assert.Empty(await stores.Purchases.ListRecentAsync(receiver.UserNum, 8, default));
        Assert.Equal(PresentState.Pending, (await stores.Presents.FindReceivedAsync(Build, receiver.UserNum, foreign.Id, default))!.State);
    }

    [Fact]
    public async Task ThrowingAGiftAwayHidesItOnceAndNeverDeliversIt()
    {
        var stores = Create();
        var (sender, receiver) = (await NewPlayer(stores, "sender"), await NewPlayer(stores, "receiver"));
        var gift = (await stores.Presents.SendAsync(Send(sender, receiver), default)).Present!;

        Assert.True(await stores.Presents.DiscardAsync(Build, receiver.UserNum, gift.Id, default));
        Assert.False(await stores.Presents.DiscardAsync(Build, receiver.UserNum, gift.Id, default));
        Assert.Null(await stores.Presents.FindReceivedAsync(Build, receiver.UserNum, gift.Id, default));
        Assert.Equal(0, await stores.Presents.CountPendingAsync(Build, receiver.UserNum, default));
        // The sender still sees it, and it is not delivered.
        var sent = await stores.Presents.ListSentAsync(Build, sender.UserNum, 0, 4, default);
        Assert.Equal(1, sent.Total);
        Assert.Equal(PresentState.Discarded, Assert.Single(sent.Records).State);
    }

    [Fact]
    public async Task EachSideRemovesItsOwnViewAndTheGiftIsGoneWhenBothHaveAndRemovalRepeatsFalse()
    {
        var stores = Create();
        var (sender, receiver) = (await NewPlayer(stores, "sender"), await NewPlayer(stores, "receiver"));
        var gift = (await stores.Presents.SendAsync(Send(sender, receiver), default)).Present!;
        // A stranger can remove nothing of theirs.
        var other = await NewPlayer(stores, "other");
        Assert.False(await stores.Presents.DeleteSentAsync(Build, other.UserNum, gift.Id, default));
        Assert.False(await stores.Presents.DeleteReceivedAsync(Build, sender.UserNum, gift.Id, default));

        Assert.True(await stores.Presents.DeleteSentAsync(Build, sender.UserNum, gift.Id, default));
        Assert.False(await stores.Presents.DeleteSentAsync(Build, sender.UserNum, gift.Id, default));
        Assert.Equal(0, (await stores.Presents.ListSentAsync(Build, sender.UserNum, 0, 4, default)).Total);
        // The receiver keeps its own view and can still take it.
        Assert.Equal(1, await stores.Presents.CountPendingAsync(Build, receiver.UserNum, default));
        Assert.NotNull(await stores.Purchases.PurchaseAsync(Take(receiver, gift.Id), default));
        Assert.True(await stores.Presents.DeleteReceivedAsync(Build, receiver.UserNum, gift.Id, default));
        Assert.False(await stores.Presents.DeleteReceivedAsync(Build, receiver.UserNum, gift.Id, default));
        Assert.Null(await stores.Presents.FindReceivedAsync(Build, receiver.UserNum, gift.Id, default));
        // Gone for both, the bag keeps what it was given.
        Assert.Single(await stores.Inventory.ListSlotsAsync(receiver.UserNum, InventoryBag.CharacterBag, 512, default));
    }

    [Fact]
    public async Task ListsPageNewestFirstAndAreKeptPerBuild()
    {
        var stores = Create();
        var (sender, receiver) = (await NewPlayer(stores, "sender"), await NewPlayer(stores, "receiver"));
        var ids = new List<long>();
        for (var i = 0; i < 6; i++)
            ids.Add((await stores.Presents.SendAsync(Send(sender, receiver, at: 1000 + i), default)).Present!.Id);
        var other = (await stores.Presents.SendAsync(Send(sender, receiver, build: "cn-324"), default)).Present!;

        var first = await stores.Presents.ListReceivedAsync(Build, receiver.UserNum, 0, 4, default);
        Assert.Equal(6, first.Total);
        Assert.Equal(Enumerable.Reverse(ids).Take(4), first.Records.Select(record => record.Id));
        var second = await stores.Presents.ListReceivedAsync(Build, receiver.UserNum, 4, 4, default);
        Assert.Equal(Enumerable.Reverse(ids).Skip(4), second.Records.Select(record => record.Id));
        Assert.Empty((await stores.Presents.ListReceivedAsync(Build, receiver.UserNum, 8, 4, default)).Records);

        // The other build's gift is invisible here and cannot be acted on from here.
        Assert.Equal(1, (await stores.Presents.ListReceivedAsync("cn-324", receiver.UserNum, 0, 4, default)).Total);
        Assert.Null(await stores.Presents.FindReceivedAsync(Build, receiver.UserNum, other.Id, default));
        Assert.False(await stores.Presents.DiscardAsync(Build, receiver.UserNum, other.Id, default));
        Assert.Equal(6, await stores.Presents.CountPendingAsync(Build, receiver.UserNum, default));
    }

    [Fact]
    public async Task APageOutsideTheBoundsIsRefusedBeforeAnyRead()
    {
        var stores = Create();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => stores.Presents.ListReceivedAsync(Build, 1, -1, 4, default).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => stores.Presents.ListSentAsync(Build, 1, 0, 0, default).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            stores.Presents.ListSentAsync(Build, 1, 0, PresentLimits.MaxPageCount + 1, default).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => stores.Presents.FindPlayerAsync("", default).AsTask());
    }

    [Fact]
    public async Task EveryOperationHonorsCancellationBeforeItReadsOrWrites()
    {
        var stores = Create();
        var (sender, receiver) = (await NewPlayer(stores, "sender"), await NewPlayer(stores, "receiver"));
        var gift = (await stores.Presents.SendAsync(Send(sender, receiver), default)).Present!;
        var reward = Reward(receiver.UserNum, "cancelled:reward");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var ct = cancelled.Token;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stores.Presents.FindPlayerAsync("sender", ct).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stores.Presents.SendAsync(Send(sender, receiver, price: 1), ct).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stores.Presents.GrantRewardAsync(reward, ct).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stores.Presents.ListReceivedAsync(Build, receiver.UserNum, 0, 4, ct).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stores.Presents.ListSentAsync(Build, sender.UserNum, 0, 4, ct).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stores.Presents.CountPendingAsync(Build, receiver.UserNum, ct).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stores.Presents.FindReceivedAsync(Build, receiver.UserNum, gift.Id, ct).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stores.Presents.DiscardAsync(Build, receiver.UserNum, gift.Id, ct).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stores.Presents.DeleteReceivedAsync(Build, receiver.UserNum, gift.Id, ct).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stores.Presents.DeleteSentAsync(Build, sender.UserNum, gift.Id, ct).AsTask());

        Assert.Equal(1, (await stores.Presents.ListReceivedAsync(Build, receiver.UserNum, 0, 4, default)).Total);
        Assert.Equal(1, (await stores.Presents.ListSentAsync(Build, sender.UserNum, 0, 4, default)).Total);
        Assert.Equal(PresentState.Pending,
            (await stores.Presents.FindReceivedAsync(Build, receiver.UserNum, gift.Id, default))!.State);
        Assert.Equal(PresentRewardOutcome.Granted, (await stores.Presents.GrantRewardAsync(reward, default)).Outcome);
    }

    [Fact]
    public async Task APlayerIsFoundByNicknameIgnoringCase()
    {
        var stores = Create();
        var player = await NewPlayer(stores, "MixedCase");
        Assert.Equal(player, await stores.Presents.FindPlayerAsync("mixedcase", default));
        Assert.Equal(player, await stores.Presents.FindPlayerAsync("MIXEDCASE", default));
        Assert.Null(await stores.Presents.FindPlayerAsync("nobody", default));
    }

    [Fact]
    public async Task AGiftThatIsNotWellFormedIsRefusedBeforeStorageIsTouched()
    {
        var stores = Create();
        var (sender, receiver) = (await NewPlayer(stores, "sender"), await NewPlayer(stores, "receiver"));
        Assert.Throws<ArgumentException>(() => Send(sender, sender).Validate());
        Assert.Throws<ArgumentException>(() => Send(sender, receiver, price: -1).Validate());
        Assert.Throws<ArgumentException>(() => Send(sender, receiver, money: "gold").Validate());
        Assert.Throws<ArgumentException>(() => Send(sender, receiver, message: new string('x', PresentLimits.MaxMessageChars + 1)).Validate());
        await Assert.ThrowsAsync<ArgumentException>(() => stores.Presents.SendAsync(Send(sender, receiver, max: 0), default).AsTask());
        Assert.Empty((await stores.Presents.ListSentAsync(Build, sender.UserNum, 0, 4, default)).Records);
    }

    private static PresentRewardCommand Reward(long receiver, string key = "level:5", params int[] stocks) =>
        new(Build, receiver, key, "System", stocks.Length == 0 ? [1000123] : stocks, "Level 5 reward", 5_000);

    [Fact]
    public async Task ARewardStoresItsGiftsFromTheSystemWithoutChargingAnyoneOrFillingASentList()
    {
        var stores = Create();
        var receiver = await NewPlayer(stores, "receiver");
        var result = await stores.Presents.GrantRewardAsync(Reward(receiver.UserNum, "level:5", 1000123, 1000124), default);

        Assert.Equal(PresentRewardOutcome.Granted, result.Outcome);
        Assert.Equal([1000123, 1000124], result.Presents.Select(gift => gift.StockKey));
        Assert.Equal(2, result.Presents.Select(gift => gift.Id).Distinct().Count());
        Assert.All(result.Presents, gift =>
        {
            Assert.Equal((PresentLimits.SystemSenderUserNum, "System", "receiver"), (gift.SenderUserNum, gift.SenderNickname, gift.ReceiverNickname));
            Assert.Equal((PresentState.Pending, "Level 5 reward", 5_000L), (gift.State, gift.Message, gift.SentAtUnixMilliseconds));
        });
        var received = await stores.Presents.ListReceivedAsync(Build, receiver.UserNum, 0, 4, default);
        Assert.Equal(2, received.Total);
        Assert.Equal(2, await stores.Presents.CountPendingAsync(Build, receiver.UserNum, default));
        Assert.Equal(0, (await stores.Presents.ListSentAsync(Build, PresentLimits.SystemSenderUserNum, 0, 4, default)).Total);
        Assert.Equal(0, (await stores.Presents.ListReceivedAsync("cn-717", receiver.UserNum, 0, 4, default)).Total);
        Assert.Empty(await stores.Purchases.ListRecentAsync(receiver.UserNum, 8, default));
    }

    [Fact]
    public async Task ARepeatedRewardKeyStoresNothingEvenAfterTheGiftsAreRemoved()
    {
        var stores = Create();
        var receiver = await NewPlayer(stores, "receiver");
        var first = await stores.Presents.GrantRewardAsync(Reward(receiver.UserNum), default);
        var again = await stores.Presents.GrantRewardAsync(Reward(receiver.UserNum), default);
        Assert.Equal((PresentRewardOutcome.Granted, PresentRewardOutcome.AlreadyClaimed), (first.Outcome, again.Outcome));
        Assert.Empty(again.Presents);
        Assert.Equal(1, (await stores.Presents.ListReceivedAsync(Build, receiver.UserNum, 0, 4, default)).Total);

        // The claim outlives the gift: removing it from the box does not make the reward come back.
        Assert.True(await stores.Presents.DeleteReceivedAsync(Build, receiver.UserNum, first.Presents[0].Id, default));
        Assert.Equal(PresentRewardOutcome.AlreadyClaimed, (await stores.Presents.GrantRewardAsync(Reward(receiver.UserNum), default)).Outcome);
        Assert.Equal(0, (await stores.Presents.ListReceivedAsync(Build, receiver.UserNum, 0, 4, default)).Total);
    }

    [Fact]
    public async Task ARewardKeyIsPerPlayerPerBuildAndPerKeyCaseSensitive()
    {
        var stores = Create();
        var (one, two) = (await NewPlayer(stores, "one"), await NewPlayer(stores, "two"));
        Assert.Equal(PresentRewardOutcome.Granted, (await stores.Presents.GrantRewardAsync(Reward(one.UserNum), default)).Outcome);
        Assert.Equal(PresentRewardOutcome.Granted, (await stores.Presents.GrantRewardAsync(Reward(two.UserNum), default)).Outcome);
        Assert.Equal(PresentRewardOutcome.Granted, (await stores.Presents.GrantRewardAsync(Reward(one.UserNum, "level:6"), default)).Outcome);
        Assert.Equal(PresentRewardOutcome.Granted, (await stores.Presents.GrantRewardAsync(Reward(one.UserNum, "LEVEL:5"), default)).Outcome);
        var otherBuild = Reward(one.UserNum) with { ProfileId = "cn-717" };
        Assert.Equal(PresentRewardOutcome.Granted, (await stores.Presents.GrantRewardAsync(otherBuild, default)).Outcome);
        Assert.Equal(3, (await stores.Presents.ListReceivedAsync(Build, one.UserNum, 0, 8, default)).Total);
    }

    [Fact]
    public async Task ConcurrentRewardsForOneKeyStoreExactlyOnce()
    {
        var stores = Create();
        var receiver = await NewPlayer(stores, "receiver");
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
            (await stores.Presents.GrantRewardAsync(Reward(receiver.UserNum, "level:5", 1000123, 1000124), default)).Outcome)));
        Assert.Equal(1, outcomes.Count(outcome => outcome == PresentRewardOutcome.Granted));
        Assert.Equal(15, outcomes.Count(outcome => outcome == PresentRewardOutcome.AlreadyClaimed));
        Assert.Equal(2, (await stores.Presents.ListReceivedAsync(Build, receiver.UserNum, 0, 4, default)).Total);
    }

    [Fact]
    public async Task ARewardForAMissingPlayerStoresNoClaimAndABadRewardIsRefusedBeforeStorage()
    {
        var stores = Create();
        Assert.Equal(PresentRewardOutcome.ReceiverMissing, (await stores.Presents.GrantRewardAsync(Reward(987_654), default)).Outcome);
        var receiver = await NewPlayer(stores, "receiver");
        Assert.Throws<ArgumentException>(() => Reward(0).Validate());
        Assert.Throws<ArgumentException>(() => (Reward(receiver.UserNum) with { StockKeys = [] }).Validate());
        Assert.Throws<ArgumentException>(() => (Reward(receiver.UserNum) with { StockKeys = [0] }).Validate());
        Assert.Throws<ArgumentException>(() => (Reward(receiver.UserNum) with { StockKeys = Enumerable.Repeat(1, PresentLimits.MaxRewardGifts + 1).ToArray() }).Validate());
        Assert.Throws<ArgumentException>(() => (Reward(receiver.UserNum) with { SenderNickname = "" }).Validate());
        Assert.Throws<ArgumentException>(() => (Reward(receiver.UserNum) with { ClaimKey = new string('k', PresentLimits.MaxClaimKeyChars + 1) }).Validate());
        await Assert.ThrowsAsync<ArgumentException>(() => stores.Presents.GrantRewardAsync(Reward(receiver.UserNum, " "), default).AsTask());
        Assert.Equal(0, (await stores.Presents.ListReceivedAsync(Build, receiver.UserNum, 0, 4, default)).Total);
        Assert.Equal(PresentRewardOutcome.Granted, (await stores.Presents.GrantRewardAsync(Reward(receiver.UserNum), default)).Outcome);
    }

    [Fact]
    public async Task ARewardGiftCanBeThrownAwayAndLeavesNoRowOnceTheReceiverRemovesIt()
    {
        var stores = Create();
        var receiver = await NewPlayer(stores, "receiver");
        var gift = (await stores.Presents.GrantRewardAsync(Reward(receiver.UserNum), default)).Presents.Single();
        Assert.NotNull(await stores.Presents.FindReceivedAsync(Build, receiver.UserNum, gift.Id, default));
        Assert.True(await stores.Presents.DiscardAsync(Build, receiver.UserNum, gift.Id, default));
        Assert.Equal(0, await stores.Presents.CountPendingAsync(Build, receiver.UserNum, default));
        Assert.Null(await stores.Presents.FindReceivedAsync(Build, receiver.UserNum, gift.Id, default));
        // The sender's view started hidden, so the discard already ended the row.
        Assert.False(await stores.Presents.DeleteReceivedAsync(Build, receiver.UserNum, gift.Id, default));
    }
}
