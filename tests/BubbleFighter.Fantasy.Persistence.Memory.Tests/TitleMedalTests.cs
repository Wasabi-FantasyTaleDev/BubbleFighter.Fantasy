using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Titles;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Memory.Tests;

/// <summary>Checks atomic native stock ownership, catalogue medal costs and durable event semantics in memory.</summary>
public sealed class TitleMedalTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly InventoryLoadoutScope Scope = new("kr-1256", [400001, 300100, 301001]);

    /// <summary>A client-supplied missing medal target returns an empty map without allocating an account or scope.</summary>
    [Fact]
    public async Task ReadingUnknownPlayerDoesNotCreateState()
    {
        var table = new MemoryPlayerTable(1);
        var store = new MemoryInventoryRepository(table);
        Assert.Empty(await store.ReadAsync(1, Scope.Key, default));
        Assert.Null(table.Find(1));
        await Seed(store);
        Assert.Empty(await store.ReadAsync(1, "kr-812", default));
        Assert.Single(table.Find(1)!.TitleMedals);
    }

    /// <summary>Concurrent duplicate title requests spend every recipe cost once and initialize late metadata in both bags.</summary>
    [Fact]
    public async Task ConcurrentClaimsCommitOneCompleteInventoryAndAllMedalDebits()
    {
        var table = new MemoryPlayerTable(1);
        var store = new MemoryInventoryRepository(table);
        await Seed(store);
        var command = Claim() with
        {
            ArticleInitialization = new(1, Scope, [300100, 400001], Now),
            PetInitialization = new(1, Scope, [301001], new(0, 0, Now, null, null, null), Now),
        };
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () => await store.TryClaimAsync(command, default))));
        var result = Assert.Single(results, value => value is not null)!;
        Assert.Equal(new[] { new TitleMedalCount(30007, 10), new TitleMedalCount(30008, 5) }, result.Medals);
        Assert.Equal(400001, result.GrantedItem.ItemId);
        Assert.Equal(InventoryLifetime.Permanent, result.GrantedItem.Lifetime);
        Assert.Equal(3, result.GrantedItem.SlotKey);
        foreach (var bag in new[] { result.Inventory.CharacterBag, result.Inventory.EquippedBag })
        {
            Assert.NotNull(bag.Single(row => row.ItemId == 300100).ArticleIdentity);
            Assert.NotNull(bag.Single(row => row.ItemId == 301001).PetIdentity);
            Assert.NotNull(bag.Single(row => row.ItemId == 301001).Pet!.Find(Scope.Key));
        }
        Assert.Equal(3UL, table.LastPetEntitlementId);
        Assert.NotNull(result.GrantedItem.ArticleIdentity);
        Assert.Empty(table.Find(1)!.Purchases);
        Assert.Equal(3, table.Find(1)!.LastPurchaseSlotKey);
        Assert.Null(await store.TryClaimAsync(command with { ExpectedTitle = result.GrantedItem }, default));
        Assert.Equal(result.Medals, await store.ReadAsync(1, Scope.Key, default));
    }

    /// <summary>A stale ownership plan or one missing medal changes neither other balances nor metadata allocation.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefusedClaimsLeaveAllBalancesAndAllocatorsUntouched(bool stale)
    {
        var table = new MemoryPlayerTable(1);
        var store = new MemoryInventoryRepository(table);
        await Seed(store);
        var before = await store.ListSlotsAsync(1, InventoryBag.CharacterBag, 512, default);
        var command = stale ? Claim() with { ExpectedTitle = new(1, InventoryBag.CharacterBag, 999, 400001, 1, 0) }
            : new TitleClaimCommand(1, Scope, 400001, 1200001, [new(30007, 20), new(30008, 99)],
                new(400001, InventoryGrantKind.Permanent, 1, 1, false), null, Now);
        command = command with { ArticleInitialization = new(1, Scope, [300100], Now) };
        Assert.Null(await store.TryClaimAsync(command, default));
        Assert.Equal(before, await store.ListSlotsAsync(1, InventoryBag.CharacterBag, 512, default));
        Assert.Equal(new[] { new TitleMedalCount(30007, 30), new TitleMedalCount(30008, 25) }, await store.ReadAsync(1, Scope.Key, default));
        Assert.Equal(0UL, table.LastPetEntitlementId);
        Assert.Equal(0, table.Find(1)!.LastPurchaseSlotKey);
    }

    /// <summary>Credit events deduplicate after spending, distinguish catalogue scopes and reject conflicting payloads.</summary>
    [Fact]
    public async Task GrantReceiptsSurviveSpendingAndRejectPayloadOrProducerConflicts()
    {
        var store = new MemoryInventoryRepository(new(1));
        await Seed(store);
        Assert.NotNull(await store.TryClaimAsync(Claim(), default));
        var replay = await store.GrantAsync(Grant(), default);
        Assert.False(replay.Applied);
        Assert.Equal(10, replay.Medals.Single(row => row.MedalId == 30007).Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.GrantAsync(Grant() with { Source = "other" }, default).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.GrantAsync(new(1, Scope.Key, "test", "initial", [new(30007, 31)]), default).AsTask());
        Assert.True((await store.GrantAsync(Grant() with { CatalogScope = "kr-812" }, default)).Applied);
        Assert.Equal(30, (await store.ReadAsync(1, "kr-812", default)).Single(row => row.MedalId == 30007).Count);
    }

    /// <summary>The scope budget rejects new state without evicting old deduplication evidence.</summary>
    [Fact]
    public async Task CapacityAndOverflowRejectWholeCredits()
    {
        var store = new MemoryInventoryRepository(new(1), titleMedalLimits: new() { MaxScopes = 1 });
        await Seed(store);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.GrantAsync(Grant() with { CatalogScope = "kr-812" }, default).AsTask());
        Assert.False((await store.GrantAsync(Grant(), default)).Applied);
        var unbounded = new MemoryInventoryRepository(new(1));
        await Seed(unbounded);
        await Assert.ThrowsAsync<OverflowException>(() => unbounded.GrantAsync(new(1, Scope.Key, "test", "overflow", [new(30007, int.MaxValue)]), default).AsTask());
        Assert.Equal(30, (await unbounded.ReadAsync(1, Scope.Key, default))[0].Count);
    }

    /// <summary>
    /// Receipts are keyed rows, not a capped list in the balance: grants past the former 4096-receipt budget keep applying
    /// and every earlier event still deduplicates.
    /// </summary>
    [Fact]
    public async Task GrantsBeyondTheFormerReceiptBudgetKeepApplyingAndDeduplicating()
    {
        var store = new MemoryInventoryRepository(new(1));
        await Seed(store);
        for (var index = 0; index < 4200; index++)
            Assert.True((await store.GrantAsync(new(1, Scope.Key, "test", "event-" + index, [new(30007, 1)]), default)).Applied);
        Assert.False((await store.GrantAsync(new(1, Scope.Key, "test", "event-0", [new(30007, 1)]), default)).Applied);
        Assert.False((await store.GrantAsync(Grant(), default)).Applied);
        Assert.Equal(30 + 4200, (await store.ReadAsync(1, Scope.Key, default)).Single(row => row.MedalId == 30007).Count);
    }

    /// <summary>A timed title renews only after expiry and preserves its ordinary identity and unknown flag bits.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimedClaimsRespectActiveOwnershipAndNativeGrantTerms(bool expired)
    {
        var store = new MemoryInventoryRepository(new(1));
        await Seed(store);
        var title = new InventorySlotRecord(1, InventoryBag.CharacterBag, 20, 400001, 1, 0x40)
            { Lifetime = InventoryLifetime.Until(Now.AddDays(expired ? -1 : 1)) };
        await store.AppendMissingItemsAsync(1, InventoryBag.CharacterBag, [title], default);
        await store.AppendMissingItemsAsync(1, InventoryBag.EquippedBag, [title with { Bag = InventoryBag.EquippedBag }], default);
        var result = await store.TryClaimAsync(Claim() with
        {
            ExpectedTitle = title,
            GrantTerm = new(400001, InventoryGrantKind.Days, 7, 30, false),
        }, default);
        if (!expired) Assert.Null(result);
        else
        {
            Assert.NotNull(result);
            foreach (var bag in new[] { result.Inventory.CharacterBag, result.Inventory.EquippedBag })
            {
                var renewed = bag.Single(row => row.ItemId == 400001);
                Assert.Equal(20, renewed.SlotKey);
                Assert.Equal(0x40, renewed.Flags);
                Assert.Equal(InventoryLifetime.Until(Now.AddDays(7)), renewed.Lifetime);
            }
        }
    }

    /// <summary>Both ordinary and pet equipment selection initialize late Articles within their successful transaction.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExistingSelectionPathsStageArticleMetadataAtomically(bool petSelection, bool refuse)
    {
        var table = new MemoryPlayerTable(1);
        var store = new MemoryInventoryRepository(table);
        await Seed(store);
        var pets = new PetInitializationCommand(1, Scope, [301001], new(0, 0, Now, null, null, null), Now);
        var initial = await store.InitializePetsAsync(pets, default);
        var articles = new ArticleInitializationCommand(1, Scope, [300100], Now);
        var target = new PetEquipmentTarget(301001, initial.CharacterBag.Single(row => row.ItemId == 301001).PetIdentity!);
        if (refuse) target = target with { Identity = new(999, 100000999) };
        var result = petSelection
            ? await store.SelectPetAsync(new(1, Scope, [], target, true, Now) { Initialization = pets, ArticleInitialization = articles }, default)
            : await store.UpdateLoadoutSelectionSnapshotAsync(1, Scope, [], refuse ? 400001 : 300100, true, default, pets, articles);
        Assert.Equal(refuse, result is null);
        Assert.Equal(refuse ? 1UL : 2UL, table.LastPetEntitlementId);
        var saved = await store.ReadEnhancementInventoryAsync(1, Scope, default);
        Assert.Equal(refuse, saved.CharacterBag.Single(row => row.ItemId == 300100).ArticleIdentity is null);
    }

    /// <summary>Acquisition bonuses commit with the title and remain consumed after the title item is removed.</summary>
    [Fact]
    public async Task BonusReceiptSurvivesTitleRemovalAndPreventsRepeatedRewards()
    {
        var table = new MemoryPlayerTable(1);
        var store = new MemoryInventoryRepository(table);
        await Seed(store);
        var scope = new InventoryLoadoutScope(Scope.Key, [400001, 300100, 301001, 100500]);
        var command = Claim() with
        {
            Scope = scope,
            BonusGrants = [new(100500, InventoryGrantKind.Count, 2, 100, false)],
            BonusMedals = [new(30009, 3)],
        };
        var first = await store.TryClaimAsync(command, default);
        Assert.NotNull(first);
        Assert.Equal(2, first.Inventory.CharacterBag.Single(row => row.ItemId == 100500).Quantity);
        Assert.Equal(3, first.Medals.Single(row => row.MedalId == 30009).Count);
        Assert.Single(table.Find(1)!.TitleAcquisitions);
        await store.ReplaceSlotsAsync(1, InventoryBag.CharacterBag,
            first.Inventory.CharacterBag.Where(row => row.ItemId != 400001).ToArray(), default);
        await store.ReplaceSlotsAsync(1, InventoryBag.EquippedBag,
            first.Inventory.EquippedBag.Where(row => row.ItemId != 400001).ToArray(), default);
        await store.GrantAsync(new(1, Scope.Key, "tests", "top-up", [new(30007, 20), new(30008, 20)]), default);
        var reacquired = await store.TryClaimAsync(command, default);
        Assert.NotNull(reacquired);
        Assert.Equal(2, reacquired.Inventory.CharacterBag.Single(row => row.ItemId == 100500).Quantity);
        Assert.Equal(3, reacquired.Medals.Single(row => row.MedalId == 30009).Count);
        Assert.Single(table.Find(1)!.TitleAcquisitions);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.TryClaimAsync(command with
        {
            BonusGrants = [new(100500, InventoryGrantKind.Count, 5, 100, false)],
        }, default).AsTask());
    }

    /// <summary>A refused bonus grant cannot consume title costs, create a title or retain an acquisition receipt.</summary>
    [Fact]
    public async Task BonusFailureRollsBackTitleMedalsAndReceipt()
    {
        var table = new MemoryPlayerTable(1);
        var store = new MemoryInventoryRepository(table);
        await Seed(store);
        var before = await store.ListSlotsAsync(1, InventoryBag.CharacterBag, 512, default);
        var command = Claim() with { BonusGrants = [new(300100, InventoryGrantKind.Count, 1, 1, false)] };
        Assert.Null(await store.TryClaimAsync(command, default));
        Assert.Equal(before, await store.ListSlotsAsync(1, InventoryBag.CharacterBag, 512, default));
        Assert.Equal(30, (await store.ReadAsync(1, Scope.Key, default)).Single(row => row.MedalId == 30007).Count);
        Assert.Empty(table.Find(1)!.TitleAcquisitions);
    }

    /// <summary>An automatic grant debits no medal, keeps the ledger, creates one permanent row and refuses a replay.</summary>
    [Fact]
    public async Task AutomaticGrantDebitsNothingAndIsIdempotent()
    {
        var table = new MemoryPlayerTable(1);
        var store = new MemoryInventoryRepository(table);
        await Seed(store);
        var medals = await store.ReadAsync(1, Scope.Key, default);
        var command = new TitleClaimCommand(1, Scope, 400001, 1200001, [], new(400001, InventoryGrantKind.Permanent, 1, 1, false), null, Now)
            { IsAutomaticGrant = true };
        var result = Assert.IsType<TitleClaimResult>(await store.TryClaimAsync(command, default));
        Assert.Equal(medals, result.Medals);
        Assert.Equal(medals, await store.ReadAsync(1, Scope.Key, default));
        Assert.Equal(InventoryLifetime.Permanent, result.GrantedItem.Lifetime);
        Assert.Single(result.Inventory.CharacterBag, row => row.ItemId == 400001);
        Assert.Null(await store.TryClaimAsync(command, default));
        Assert.Null(await store.TryClaimAsync(command with { ExpectedTitle = result.GrantedItem }, default));
        Assert.Throws<ArgumentException>(() => (command with { BonusMedals = [new(30007, 1)] }).Validate());
        Assert.Throws<ArgumentException>(() => new TitleClaimCommand(1, Scope, 400001, 1200001, [new(30007, 1)],
            new(400001, InventoryGrantKind.Permanent, 1, 1, false), null, Now) { IsAutomaticGrant = true }.Validate());
        Assert.Throws<ArgumentException>(() => new TitleClaimCommand(1, Scope, 400001, 1200001, [],
            new(400001, InventoryGrantKind.Permanent, 1, 1, false), null, Now).Validate());
    }
    private static TitleClaimCommand Claim() => new(1, Scope, 400001, 1200001,
        [new(30007, 20), new(30008, 20)], new(400001, InventoryGrantKind.Permanent, 1, 1, false), null, Now);

    private static TitleMedalGrantCommand Grant() => new(1, Scope.Key, "test", "initial", [new(30007, 30), new(30008, 25)]);

    private static async Task Seed(MemoryInventoryRepository store)
    {
        InventorySlotRecord[] rows = [
            new(1, InventoryBag.CharacterBag, 1, 300100, 1, 0) { Lifetime = InventoryLifetime.Permanent },
            new(1, InventoryBag.CharacterBag, 2, 301001, 1, 0) { Lifetime = InventoryLifetime.Permanent },
        ];
        await store.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, rows, default);
        await store.ReplaceSlotsAsync(1, InventoryBag.EquippedBag, rows.Select(row => row with { Bag = InventoryBag.EquippedBag }).ToArray(), default);
        await store.GrantAsync(Grant(), default);
    }
}
