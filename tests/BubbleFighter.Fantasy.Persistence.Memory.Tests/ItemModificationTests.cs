using System.Text.Json;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Wallet;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Memory.Tests;

/// <summary>Checks exact modification ownership, coupled Lucci debits and immutable durable state.</summary>
public sealed class ItemModificationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly InventoryLoadoutScope Scope = new("kr-1256", [300100, 300101, 190200, 190201, 301001]);

    /// <summary>Concurrent copies charge once, preserve enchantment, and retain an empty revision after deletion and replenishment.</summary>
    [Fact]
    public async Task DuplicateApplyAndDeleteCommitWalletAndMaterialsExactlyOnce()
    {
        var (store, wallets, _, snapshot) = await Seed();
        snapshot = Assert.IsType<InventorySnapshot>(await store.TryEnhanceAsync(new([Target(snapshot)], Scope, [],
            ItemEnhancementKind.Weapon, true, Now), default));
        var command = Apply(snapshot);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () => await store.TryModifyAsync(command, default))));
        var applied = Assert.Single(results, result => result is not null)!;
        Assert.Equal(new PlayerWallet(1, 900, 50), applied.Wallet);
        Assert.Equal(applied.Wallet, await wallets.FindByUserNumAsync(1, default));
        foreach (var bag in new[] { applied.Inventory.CharacterBag, applied.Inventory.EquippedBag })
        {
            var target = bag.Single(row => row.ItemId == 300100);
            Assert.Equal(new ItemEnhancementState(ItemEnhancementKind.Weapon, 1), target.Enhancements!.Find(Scope.Key));
            Assert.Equal(new ItemModificationState(1, [new(101, 160, -40, 0, 0)]), target.Modifications!.Find(Scope.Key));
            Assert.Equal(4, bag.Single(row => row.ItemId == 190200).Quantity);
            Assert.DoesNotContain(bag, row => row.ItemId == 190201);
        }
        var deletion = new ItemModificationCommand(Target(applied.Inventory), Scope, [], null, 0, 25, Now);
        results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () => await store.TryModifyAsync(deletion, default))));
        var deleted = Assert.Single(results, result => result is not null)!;
        Assert.Equal(new PlayerWallet(1, 875, 50), deleted.Wallet);
        Assert.Equal(new ItemModificationState(2, []), Target(deleted.Inventory).Modifications!.Find(Scope.Key));

        foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
        {
            var rows = (await store.ListSlotsAsync(1, bag, 100, default)).Select(row =>
                row.ItemId == 190200 ? row with { Quantity = 6 } : row).ToList();
            rows.Add(snapshot.CharacterBag.Single(row => row.ItemId == 190201) with { Bag = bag });
            await store.ReplaceSlotsAsync(1, bag, rows, default);
        }
        Assert.Null(await store.TryModifyAsync(command, default));
        Assert.Null(await store.TryModifyAsync(deletion, default));
        Assert.Equal(deleted.Wallet, await wallets.FindByUserNumAsync(1, default));
        var refreshed = await store.ReadEnhancementInventoryAsync(1, Scope, default);
        var repeated = Assert.IsType<ItemModificationResult>(await store.TryModifyAsync(Apply(refreshed), default));
        Assert.Equal(new ItemModificationState(3, [new(101, 160, -40, 0, 0)]), Target(repeated.Inventory).Modifications!.Find(Scope.Key));
    }

    /// <summary>Rejected ownership, material, currency and group plans save neither a partial debit nor modification state.</summary>
    [Theory]
    [InlineData("wallet")]
    [InlineData("material")]
    [InlineData("expired")]
    [InlineData("worn")]
    [InlineData("stale")]
    [InlineData("missing_group")]
    public async Task RefusedPlanChangesNeitherInventoryNorWallet(string reason)
    {
        var (store, wallets, _, snapshot) = await Seed(reason == "expired");
        var command = Apply(snapshot);
        if (reason == "wallet") await wallets.UpsertAsync(new(1, 99, 50), default);
        if (reason == "material") await store.ConsumeItemAsync(1, 190201, default);
        if (reason == "worn") Assert.NotNull(await store.SelectArticleAsync(new(1, Scope, Target(snapshot), [], true, Now), default));
        if (reason == "stale") command = command with { ExpectedTarget = command.ExpectedTarget with { SlotKey = 99 } };
        if (reason == "missing_group") command = new(Target(snapshot), Scope, [], null, 0, 25, Now);
        var before = await store.ReadEnhancementInventoryAsync(1, Scope, default);
        var wallet = await wallets.FindByUserNumAsync(1, default);
        Assert.Null(await store.TryModifyAsync(command, default));
        var after = await store.ReadEnhancementInventoryAsync(1, Scope, default);
        Assert.Equal(before.CharacterBag, after.CharacterBag);
        Assert.Equal(before.EquippedBag, after.EquippedBag);
        Assert.Equal(wallet, await wallets.FindByUserNumAsync(1, default));
    }

    /// <summary>Late identity preparation rolls back its shared allocator and wallet when exact target ownership is stale.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateMetadataCommitsOnlyWithItsModification(bool refuse)
    {
        var (store, wallets, table, snapshot) = await Seed();
        InventorySlotRecord[] late = [
            new(1, InventoryBag.CharacterBag, 9, 300101, 1, 0) { Lifetime = InventoryLifetime.Permanent },
            new(1, InventoryBag.CharacterBag, 10, 301001, 1, 0) { Lifetime = InventoryLifetime.Permanent },
        ];
        await store.AppendMissingItemsAsync(1, InventoryBag.CharacterBag, late, default);
        await store.AppendMissingItemsAsync(1, InventoryBag.EquippedBag, late.Select(row => row with { Bag = InventoryBag.EquippedBag }).ToArray(), default);
        var command = Apply(snapshot) with
        {
            ArticleInitialization = new(1, Scope, [300100, 300101], Now),
            PetInitialization = new(1, Scope, [301001], new(0, 0, Now, null, null, null), Now),
        };
        if (refuse) command = command with { ExpectedTarget = command.ExpectedTarget with { SlotKey = 99 } };
        var result = await store.TryModifyAsync(command, default);
        var saved = await store.ReadEnhancementInventoryAsync(1, Scope, default);
        Assert.Equal(refuse ? 1UL : 3UL, table.LastPetEntitlementId);
        Assert.Equal(refuse ? 1000 : 900, (await wallets.FindByUserNumAsync(1, default))!.Lucci);
        if (refuse)
        {
            Assert.Null(result);
            Assert.Null(saved.CharacterBag.Single(row => row.ItemId == 300101).ArticleIdentity);
            Assert.Null(saved.CharacterBag.Single(row => row.ItemId == 301001).PetIdentity);
        }
        else
        {
            Assert.NotNull(result);
            Assert.NotNull(saved.CharacterBag.Single(row => row.ItemId == 300101).ArticleIdentity);
            Assert.NotNull(saved.CharacterBag.Single(row => row.ItemId == 301001).PetIdentity);
            Assert.Equal(result.Inventory.CharacterBag, saved.CharacterBag);
        }
    }

    /// <summary>Ordinary inventory replacement cannot forge or erase server-managed modification metadata.</summary>
    [Fact]
    public async Task ExternalWritesCannotForgeOrEraseModifications()
    {
        var (store, _, _, snapshot) = await Seed();
        var forged = snapshot.CharacterBag.Select(row => row.ItemId == 300100 ? row with
        { Modifications = ItemModifications.Empty.With(Scope.Key, new(1, [new(101, 160, -40, 0, 0)])) } : row).ToArray();
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, forged, default).AsTask());
        var applied = Assert.IsType<ItemModificationResult>(await store.TryModifyAsync(Apply(snapshot), default));
        var erased = applied.Inventory.CharacterBag.Select(row => row with { Modifications = null }).ToArray();
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, erased, default).AsTask());
        Assert.Equal(applied.Inventory.CharacterBag, (await store.ReadEnhancementInventoryAsync(1, Scope, default)).CharacterBag);
    }

    /// <summary>Cancellation and invalid group identities cannot charge Lucci or initialize an installed state.</summary>
    [Fact]
    public async Task InvalidOrCancelledCommandsCannotCommit()
    {
        var (store, wallets, _, snapshot) = await Seed();
        var command = Apply(snapshot);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.TryModifyAsync(command with { AddedAttribute = new(101, 160, -40, 3, 0) }, default).AsTask());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.TryModifyAsync(command, cancellation.Token).AsTask());
        Assert.Equal(snapshot.CharacterBag, (await store.ReadEnhancementInventoryAsync(1, Scope, default)).CharacterBag);
        Assert.Equal(new PlayerWallet(1, 1000, 50), await wallets.FindByUserNumAsync(1, default));
    }

    /// <summary>Snapshots own their inputs, preserve signed values and compare structurally after JSON persistence.</summary>
    [Fact]
    public void ModificationStateIsBoundedImmutableAndStructurallyComparable()
    {
        ItemModificationAttribute[] input = [new(107, int.MaxValue, int.MinValue, 2, 2), new(101, 160, -40, 0, 0)];
        var state = new ItemModificationState(7, input);
        var metadata = ItemModifications.Empty.With(Scope.Key, state).With("cn-774", new(1, []));
        input[0] = new(106, 1, -1, 1, 1);
        Assert.Equal([0, 2], state.Attributes.Select(attribute => attribute.Group));
        Assert.Equal(int.MinValue, state.Attributes[1].Value1);
        var restored = JsonSerializer.Deserialize<ItemModifications>(JsonSerializer.Serialize(metadata));
        Assert.Equal(metadata, restored);
        Assert.Equal(metadata.GetHashCode(), restored!.GetHashCode());
        Assert.Throws<InvalidDataException>(() => new ItemModificationState(0, []));
        Assert.Throws<InvalidDataException>(() => new ItemModificationState(1, [input[0], input[0]]));
        Assert.Throws<InvalidDataException>(() => metadata.Validate(1));
        var full = ItemModifications.Empty;
        for (var index = 0; index < 256; index++) full = full.With($"scope-{index}", new(1, []));
        Assert.Throws<InvalidDataException>(() => full.With("overflow", new(1, [])));
    }

    /// <summary>Unknown, repeated, omitted and malformed serialized fields fail before they can become inventory state.</summary>
    [Theory]
    [InlineData("{\"kr-1256\":{\"Revision\":1}}")]
    [InlineData("{\"kr-1256\":{\"Revision\":\"1\",\"Attributes\":[]}}")]
    [InlineData("{\"kr-1256\":{\"Revision\":1,\"Revision\":2,\"Attributes\":[]}}")]
    [InlineData("{\"kr-1256\":{\"Revision\":1,\"Attributes\":[],\"Unexpected\":1}}")]
    [InlineData("{\"kr-1256\":{\"Revision\":1,\"Attributes\":[{\"Id\":101,\"Value0\":1,\"Value1\":-1,\"Group\":0}]}}")]
    [InlineData("{\"kr-1256\":{\"Revision\":1,\"Attributes\":[]},\"kr-1256\":{\"Revision\":2,\"Attributes\":[]}}")]
    public void MalformedJsonIsRejected(string json) => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ItemModifications>(json));

    private static InventorySlotRecord Target(InventorySnapshot snapshot) => snapshot.CharacterBag.Single(row => row.ItemId == 300100);
    private static ItemModificationCommand Apply(InventorySnapshot snapshot) => new(Target(snapshot), Scope,
        [new(snapshot.CharacterBag.Single(row => row.ItemId == 190200), 2), new(snapshot.CharacterBag.Single(row => row.ItemId == 190201), 1)],
        new(101, 160, -40, 0, 0), null, 100, Now);

    private static async Task<(MemoryInventoryRepository Store, MemoryPlayerWalletRepository Wallets, MemoryPlayerTable Table, InventorySnapshot Snapshot)> Seed(bool expired = false)
    {
        var table = new MemoryPlayerTable(1);
        var store = new MemoryInventoryRepository(table);
        var wallets = new MemoryPlayerWalletRepository(table);
        await wallets.GetOrCreateAsync(new(1, 1000, 50), default);
        InventorySlotRecord[] rows = [
            new(1, InventoryBag.CharacterBag, 1, 300100, 1, 0x40) { Lifetime = InventoryLifetime.Permanent },
            new(1, InventoryBag.CharacterBag, 2, 190200, 6, 0),
            new(1, InventoryBag.CharacterBag, 3, 190201, 1, 0)
            { Lifetime = expired ? InventoryLifetime.Until(Now.AddSeconds(-1)) : InventoryLifetime.Permanent },
        ];
        await store.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, rows, default);
        await store.ReplaceSlotsAsync(1, InventoryBag.EquippedBag, rows.Select(row => row with { Bag = InventoryBag.EquippedBag }).ToArray(), default);
        return (store, wallets, table, await store.InitializeArticlesAsync(new(1, Scope, [300100], Now), default));
    }
}
