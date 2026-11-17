using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.MysteryStore;
using Xunit;

namespace BubbleFighter.Fantasy.InventoryView.Tests;

/// <summary>Verifies atomic staging from frozen native stock terms and authoritative inventory ownership.</summary>
public sealed class MysteryStorePlannerTests
{
    private const int Coin = 100385;
    private const int OfferKey = 2000001;
    private const string Revision = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly InventoryLoadoutScope Scope = new("kr-1256", [Coin, .. Enumerable.Range(101, 20)]);
    private static readonly InventoryLoadoutState Loadout = new([], 0, null);

    [Fact]
    public void PurchaseStagesWholeSetAndCoinMirrorsWithoutMutatingInput()
    {
        var coin = Row(1, Coin, 10);
        var owned = Row(9, 101, 4) with { Flags = 128 };
        InventorySlotRecord[] rows = [coin, coin with { Bag = InventoryBag.EquippedBag },
            owned, owned with { Bag = InventoryBag.EquippedBag }];
        var selection = Selection();

        var plan = MysteryStorePlanner.Purchase(Purchase(), selection, rows, Loadout, 100, Now);

        Assert.Equal(MysteryStoreOutcome.Purchased, plan.Outcome);
        Assert.True(plan.RequiresCommit);
        Assert.Equal(1u, plan.Selection!.PurchasedMask);
        Assert.Equal(0u, selection.PurchasedMask);
        Assert.Equal(10, rows[0].Quantity);
        Assert.All(plan.Rows!.Where(row => row.ItemId == Coin), row => Assert.Equal(7, row.Quantity));
        var renewed = Assert.Single(plan.Rows!, row => row.Bag == InventoryBag.CharacterBag && row.ItemId == 101);
        Assert.Equal((9L, 6, (byte)128), (renewed.SlotKey, renewed.Quantity, renewed.Flags));
        Assert.Equal(renewed with { Bag = InventoryBag.EquippedBag },
            Assert.Single(plan.Rows!, row => row.Bag == InventoryBag.EquippedBag && row.ItemId == 101));
        var added = Assert.Single(plan.Rows!, row => row.ItemId == 102);
        Assert.Equal(101, added.SlotKey);
        Assert.Equal(1, added.Quantity);
        Assert.Equal(InventoryLifetime.Until(Now.AddDays(7)), added.Lifetime);
        Assert.Equal(new[] { 101, 102 }, plan.GrantItemIds);
        Assert.Equal(OfferKey, plan.ReceiptNativeKey);
    }

    [Fact]
    public void CurrentTokenReplayReturnsCurrentInventoryWithoutNewRowsOrGrants()
    {
        var first = MysteryStorePlanner.Purchase(Purchase(), Selection(), [Row(1, Coin, 10)], Loadout, 1, Now);
        var currentRows = first.Rows!.Select(row => row.ItemId == Coin ? row with { Quantity = 6 } : row).ToArray();
        var replay = MysteryStorePlanner.Purchase(Purchase(), first.Selection, currentRows, Loadout, first.LastSlotKey, Now);
        Assert.Equal(MysteryStoreOutcome.AlreadyPurchased, replay.Outcome);
        Assert.False(replay.RequiresCommit);
        Assert.Null(replay.Rows);
        Assert.Empty(replay.GrantItemIds);
        Assert.Equal(OfferKey, replay.ReceiptNativeKey);
        Assert.Equal(6, Assert.Single(replay.ReplayInventory!.CharacterBag, row => row.ItemId == Coin).Quantity);
        Assert.Equal(first.Rows!.Count, replay.ReplayInventory.CharacterBag.Count);
    }

    [Fact]
    public void ConflictingCoinMirrorCannotBeDebited()
    {
        var coin = Row(1, Coin, 10);
        InventorySlotRecord[] rows = [coin, coin with { Bag = InventoryBag.EquippedBag, Quantity = 9 }];
        Assert.Throws<InvalidDataException>(() => MysteryStorePlanner.Purchase(Purchase(), Selection(), rows, Loadout, 1, Now));
        Assert.Equal(10, rows[0].Quantity);
        Assert.Equal(9, rows[1].Quantity);
    }

    [Theory]
    [InlineData("token", MysteryStoreOutcome.StaleToken)]
    [InlineData("quote", MysteryStoreOutcome.QuoteMismatch)]
    [InlineData("expiry", MysteryStoreOutcome.Expired)]
    [InlineData("key", MysteryStoreOutcome.UnknownOffer)]
    [InlineData("revision", MysteryStoreOutcome.CatalogChanged)]
    public void StaleRequestsNeverProduceInventoryReplacements(string fault, MysteryStoreOutcome outcome)
    {
        var command = Purchase();
        if (fault == "token") command = command with { ExpectedToken = 9 };
        if (fault == "quote") command = command with { QuotedCoinPrice = 2 };
        if (fault == "key") command = command with { NativeKey = 2999999 };
        if (fault == "revision") command = command with { CatalogRevision = new string('f', 64) };
        var now = fault == "expiry" ? Now.AddSeconds(60) : Now;
        var rows = new[] { Row(1, Coin, 10) };
        var plan = MysteryStorePlanner.Purchase(command, Selection(), rows, Loadout, 1, now);
        Assert.Equal(outcome, plan.Outcome);
        Assert.False(plan.RequiresCommit);
        Assert.Null(plan.Rows);
        Assert.Equal(10, rows[0].Quantity);
    }

    [Fact]
    public void RefreshDebitsStoredQuoteAndResetsTheMaskWithAFreshOpaqueToken()
    {
        var current = Selection() with { PurchasedMask = 7 };
        var candidate = Board(current.Board.Offers.Reverse().ToArray());
        var command = new MysteryStoreRefreshCommand(new(7, Scope), 10, 2, candidate, 60);
        var plan = MysteryStorePlanner.Refresh(command, current, [Row(1, Coin, 2)], Loadout, 9, 11, Now);
        Assert.Equal(MysteryStoreOutcome.Refreshed, plan.Outcome);
        Assert.Empty(plan.Rows!);
        Assert.Equal(11u, plan.Selection!.Token);
        Assert.Equal(0u, plan.Selection.PurchasedMask);
        Assert.Equal(candidate.Offers.Select(offer => offer.NativeKey), plan.Selection.Board.Offers.Select(offer => offer.NativeKey));
        Assert.False(plan.AdmissionStillValid(Now.AddSeconds(60)));
        Assert.True(plan.AdmissionStillValid(Now.AddSeconds(59)));
        Assert.Throws<ArgumentException>(() =>
            MysteryStorePlanner.Refresh(command, current, [Row(1, Coin, 2)], Loadout, 9, 10, Now));
        Assert.Throws<ArgumentException>(() =>
            MysteryStorePlanner.Refresh(command, current, [Row(1, Coin, 2)], Loadout, 9, 9, Now));
    }

    [Fact]
    public void CoinRewardsCannotFundTheirOwnPurchase()
    {
        var offers = Board().Offers.ToArray();
        offers[0] = new(OfferKey, 3, 0, string.Empty, [new(Coin, InventoryGrantKind.Count, 5, 99, false)]);
        var current = Selection() with { Board = Board(offers) };
        var refused = MysteryStorePlanner.Purchase(Purchase(), current, [Row(1, Coin, 1)], Loadout, 1, Now);
        Assert.Equal(MysteryStoreOutcome.InsufficientCoins, refused.Outcome);
        Assert.Null(refused.Rows);
        var accepted = MysteryStorePlanner.Purchase(Purchase(), current, [Row(1, Coin, 3)], Loadout, 1, Now);
        Assert.Equal(5, Assert.Single(accepted.Rows!).Quantity);
    }

    [Fact]
    public void GrantLimitFailureDoesNotExposeTheStagedCoinDebit()
    {
        InventorySlotRecord[] rows = [Row(1, Coin, 10), Row(2, 101, 99)];
        var plan = MysteryStorePlanner.Purchase(Purchase(), Selection(), rows, Loadout, 2, Now);
        Assert.Equal(MysteryStoreOutcome.GrantRefused, plan.Outcome);
        Assert.False(plan.RequiresCommit);
        Assert.Null(plan.Rows);
        Assert.Equal(10, rows[0].Quantity);
        Assert.Equal(0u, plan.Selection!.PurchasedMask);
    }

    [Fact]
    public void FullBagRefusesAllRewardsAndPreservesCoinOwnership()
    {
        var extra = Enumerable.Range(1000, InventoryLimits.MaxInventorySlotsPerBag - 1).ToArray();
        var scope = new InventoryLoadoutScope("kr-1256", [.. Scope.SupportedItemIds, .. extra]);
        InventorySlotRecord[] rows = [Row(1, Coin, 10), .. extra.Select((item, index) => Row(index + 2, item, 1))];
        var command = Purchase() with { Inventory = new(7, scope) };
        var plan = MysteryStorePlanner.Purchase(command, Selection(), rows, Loadout, rows.Length, Now);
        Assert.Equal(MysteryStoreOutcome.GrantRefused, plan.Outcome);
        Assert.Null(plan.Rows);
        Assert.Equal(10, rows[0].Quantity);
    }

    [Fact]
    public void EnsureKeepsLiveOffersForTheSameRevisionAndReplacesMissingOrExpiredState()
    {
        var command = new MysteryStoreEnsureCommand(7, Scope, Board(), 60);
        var created = MysteryStorePlanner.Ensure(command, null, 10, Now);
        var unchanged = MysteryStorePlanner.Ensure(command, created.Selection, 11, Now.AddSeconds(1));
        Assert.Equal(MysteryStoreOutcome.Unchanged, unchanged.Outcome);
        Assert.Equal(10u, unchanged.Selection!.Token);
        var expired = MysteryStorePlanner.Ensure(command, created.Selection, 11, Now.AddSeconds(60));
        Assert.Equal(MysteryStoreOutcome.SelectionCreated, expired.Outcome);
        Assert.Equal(11u, expired.Selection!.Token);
        Assert.Null(expired.Rows);
    }

    [Fact]
    public void DeliberateRevisionChangeReplacesLiveOffersWithoutCoinDebitEvenWhenOldItemsWereRetired()
    {
        var scope = new InventoryLoadoutScope(Scope.Key, [Coin, .. Enumerable.Range(201, 20)]);
        var revision = new string('f', 64);
        var candidate = new MysteryStoreBoard(revision, Coin, 2, false,
            Enumerable.Range(0, 6).Select(index => new MysteryStoreOffer(OfferKey + index, 3, 0, string.Empty,
                [new(201 + index, InventoryGrantKind.Count, 1, 99, false)])).ToArray());
        var current = Selection() with { PurchasedMask = 1 };
        var rejectedPurchase = MysteryStorePlanner.Purchase(Purchase() with
            { Inventory = new(7, scope), CatalogRevision = revision }, current, [Row(1, Coin, 10)], Loadout, 1, Now);
        Assert.Equal(MysteryStoreOutcome.CatalogChanged, rejectedPurchase.Outcome);
        Assert.Null(rejectedPurchase.Rows);

        var replacement = MysteryStorePlanner.Ensure(new(7, scope, candidate, 60), current, 11, Now.AddSeconds(1));
        Assert.Equal(MysteryStoreOutcome.SelectionCreated, replacement.Outcome);
        Assert.Equal((11u, 0u, revision),
            (replacement.Selection!.Token, replacement.Selection.PurchasedMask, replacement.Selection.Board.CatalogRevision));
        Assert.Null(replacement.Rows);
    }

    [Fact]
    public void ZeroCostPurchaseNeedsNoCoinButStillValidatesInventoryOwnership()
    {
        var offers = Board().Offers.ToArray();
        offers[0] = offers[0] with { CoinPrice = 0 };
        var current = Selection() with { Board = Board(offers) };
        var command = Purchase() with { QuotedCoinPrice = 0 };
        var accepted = MysteryStorePlanner.Purchase(command, current, [], Loadout, 0, Now);
        Assert.Equal(MysteryStoreOutcome.Purchased, accepted.Outcome);
        Assert.DoesNotContain(accepted.Rows!, row => row.ItemId == Coin);
        Assert.Equal(2, accepted.Rows!.Count);
        Assert.Throws<InvalidDataException>(() =>
            MysteryStorePlanner.Purchase(command, current, [Row(1, 109, 1) with { UserNum = 8 }], Loadout, 1, Now));
        current = current with { Board = current.Board with { RefreshCoinCost = 0 } };
        var refresh = MysteryStorePlanner.Refresh(new(new(7, Scope), 10, 0, current.Board, 60),
            current, [], Loadout, 0, 11, Now);
        Assert.Equal(MysteryStoreOutcome.Refreshed, refresh.Outcome);
        Assert.Empty(refresh.Rows!);
    }

    [Fact]
    public void ReplanningAfterAnAllocatorWaitUsesFreshTimeWithoutDebitingTwice()
    {
        var rows = new[] { Row(1, Coin, 10) };
        var provisional = MysteryStorePlanner.Purchase(Purchase(), Selection(), rows, Loadout, 1, Now);
        var admittedAt = Now.AddSeconds(20);
        var final = MysteryStorePlanner.Purchase(Purchase(), Selection(), rows, Loadout, 1, admittedAt);
        Assert.Equal(7, Assert.Single(final.Rows!, row => row.ItemId == Coin).Quantity);
        Assert.Equal(InventoryLifetime.Until(admittedAt.AddDays(7)),
            Assert.Single(final.Rows!, row => row.ItemId == 102).Lifetime);
        Assert.NotEqual(Assert.Single(provisional.Rows!, row => row.ItemId == 102).Lifetime,
            Assert.Single(final.Rows!, row => row.ItemId == 102).Lifetime);
        var expired = MysteryStorePlanner.Purchase(Purchase(), Selection(), rows, Loadout, 1, Now.AddSeconds(60));
        Assert.Equal(MysteryStoreOutcome.Expired, expired.Outcome);
        Assert.Null(expired.Rows);
        Assert.Equal(10, rows[0].Quantity);
    }

    [Fact]
    public void FinalTokenCanPurchaseButCannotBeReplacedOrWrapped()
    {
        var current = Selection() with { Token = uint.MaxValue };
        var purchase = MysteryStorePlanner.Purchase(Purchase() with { ExpectedToken = uint.MaxValue }, current,
            [Row(1, Coin, 10)], Loadout, 1, Now);
        Assert.Equal(MysteryStoreOutcome.Purchased, purchase.Outcome);
        var refresh = MysteryStorePlanner.Refresh(new(new(7, Scope), uint.MaxValue, 2, Board(), 60),
            current, [Row(1, Coin, 10)], Loadout, 1, 0, Now);
        Assert.Equal(MysteryStoreOutcome.TokenExhausted, refresh.Outcome);
        Assert.Null(refresh.Rows);
        Assert.False(refresh.RequiresCommit);
        var rollover = MysteryStorePlanner.Ensure(new(7, Scope, Board(), 60), current, 0, Now.AddSeconds(60));
        Assert.Equal(MysteryStoreOutcome.TokenExhausted, rollover.Outcome);
        Assert.Equal(uint.MaxValue, rollover.Selection!.Token);
        Assert.False(rollover.RequiresCommit);
    }

    [Fact]
    public void CapturedTermsAndMetadataCatalogsDoNotObserveLaterSourceArrayChanges()
    {
        InventoryGrantTerm[] terms = [new(101, InventoryGrantKind.Count, 2, 99, false)];
        var offers = Board().Offers.ToArray();
        offers[0] = new(OfferKey, 3, 0, string.Empty, terms);
        var board = Board(offers);
        var articleIds = new[] { 101 };
        var petIds = new[] { 102 };
        var context = new InventoryMetadataPreparationCommand(7, Scope)
        {
            ArticleInitialization = new(7, Scope, articleIds, Now),
            PetInitialization = new(7, Scope, petIds, new(0, 0, Now, null, null, null), Now),
        };
        context.Validate();
        terms[0] = terms[0] with { Amount = 99 };
        offers[0] = offers[0] with { CoinPrice = 99 };
        articleIds[0] = 103;
        petIds[0] = 104;
        Assert.Equal((3, 2), ((int)board.Offers[0].CoinPrice, board.Offers[0].Grants[0].Amount));
        Assert.Equal(new[] { 101 }, context.ArticleInitialization.EligibleItemIds);
        Assert.Equal(new[] { 102 }, context.PetInitialization.PetItemIds);
    }

    private static InventorySlotRecord Row(long slot, int item, int count) =>
        new(7, InventoryBag.CharacterBag, slot, item, count, 0) { Lifetime = InventoryLifetime.Permanent };

    private static MysteryStorePurchaseCommand Purchase() => new(new(7, Scope), Revision, 10, OfferKey, 3);

    private static MysteryStoreSelection Selection() => new(7, Scope.Key, 10, Now.AddSeconds(60).ToUnixTimeMilliseconds(), Board(), 0);

    private static MysteryStoreBoard Board(IReadOnlyList<MysteryStoreOffer>? offers = null) => new(Revision, Coin, 2, false,
        offers ?? Enumerable.Range(0, 6).Select(index => new MysteryStoreOffer(OfferKey + index, 3, 0, string.Empty,
            index == 0 ? [new(101, InventoryGrantKind.Count, 2, 99, false), new(102, InventoryGrantKind.Days, 7, 30, false)]
                : [new(103 + index, InventoryGrantKind.Count, 1, 99, false)])).ToArray());
}
