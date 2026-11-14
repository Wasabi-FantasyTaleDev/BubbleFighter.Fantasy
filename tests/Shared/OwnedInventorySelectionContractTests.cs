using BubbleFighter.Fantasy.Contracts.Inventory;
using Xunit;

namespace BubbleFighter.Fantasy.Tests.Shared;

public abstract class OwnedInventorySelectionContractTests
{
    protected abstract IInventoryLoadoutStore Store { get; }
    protected abstract long UserNum { get; }
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 16, 0, 0, TimeSpan.Zero);
    private static readonly InventoryLoadoutScope Scope = new("owned-selection", [120001, 120002, 202002]);
    private static readonly InventoryWearGroup[] Groups = [new(120001, 120002, 120001)];

    /// <summary>The provider's original CLR entry point still commits and publishes both build-scoped inventory bags.</summary>
    [Fact]
    public async Task OriginalSnapshotSignatureBindsToTheAtomicProvider()
    {
        var method = Store.GetType().GetMethod(nameof(IInventoryLoadoutStore.UpdateLoadoutSelectionSnapshotAsync),
            [typeof(long), typeof(InventoryLoadoutScope), typeof(IReadOnlyList<InventoryWearGroup>), typeof(int?),
                typeof(bool), typeof(CancellationToken), typeof(PetInitializationCommand)]);
        Assert.NotNull(method);
        Assert.True(method.GetParameters()[6].IsOptional);
        Assert.Null(method.GetParameters()[6].DefaultValue);
        var select = method.CreateDelegate<Func<long, InventoryLoadoutScope, IReadOnlyList<InventoryWearGroup>, int?,
            bool, CancellationToken, PetInitializationCommand?, ValueTask<PetInventorySnapshot?>>>(Store);
        var target = await SeedAsync();
        var committed = await select(UserNum, Scope, Groups, target.ItemId, true, default, null);
        Assert.NotNull(committed);
        Assert.All(committed.CharacterBag.Concat(committed.EquippedBag).Where(row => row.ItemId == target.ItemId),
            row => Assert.Equal(129, row.Flags));
        Assert.All(committed.CharacterBag.Concat(committed.EquippedBag).Where(row => row.ItemId == 120001),
            row => Assert.Equal(128, row.Flags));
        Assert.Equal(committed.CharacterBag.ToArray(),
            (await Store.ListLoadoutSlotsAsync(UserNum, Scope, InventoryBag.CharacterBag, 100, default)).ToArray());
        Assert.Equal(committed.EquippedBag.ToArray(),
            (await Store.ListLoadoutSlotsAsync(UserNum, Scope, InventoryBag.EquippedBag, 100, default)).ToArray());
    }

    [Fact]
    public async Task ExactSelectionPublishesBothBagsAndKeepsOtherBuildsIndependent()
    {
        var target = await SeedAsync();
        var otherScope = new InventoryLoadoutScope("other-selection", Scope.SupportedItemIds);
        await Store.UpdateLoadoutSelectionAsync(UserNum, otherScope, Groups, 120001, true, default);
        var committed = await SelectAsync(target);
        Assert.NotNull(committed);
        Assert.All(committed.CharacterBag.Concat(committed.EquippedBag).Where(row => row.ItemId == 120002),
            row => Assert.Equal(129, row.Flags));
        Assert.All(committed.CharacterBag.Concat(committed.EquippedBag).Where(row => row.ItemId == 120001),
            row => Assert.Equal(128, row.Flags));
        Assert.Contains(120001, (await Store.GetLoadoutAsync(UserNum, otherScope, default)).WornItemIds!);
        var removed = await Store.UpdateLoadoutSelectionSnapshotAsync(UserNum, Scope, Groups, 120002, false, default,
            initialization: null, articleInitialization: null, target: new(committed.CharacterBag.Single(row => row.ItemId == 120002), Now));
        Assert.NotNull(removed);
        Assert.All(removed.CharacterBag.Concat(removed.EquippedBag), row => Assert.Equal(128, row.Flags));
    }

    [Theory]
    [InlineData("slot")]
    [InlineData("quantity")]
    [InlineData("lifetime")]
    [InlineData("removed")]
    public async Task OwnershipChangedAfterReadCannotAuthorizeAReplacement(string change)
    {
        var target = await SeedAsync();
        foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
        {
            var rows = await Store.ListSlotsAsync(UserNum, bag, 100, default);
            await Store.ReplaceSlotsAsync(UserNum, bag, rows.Where(row => change != "removed" || row.ItemId != target.ItemId)
                .Select(row => row.ItemId != target.ItemId ? row : change switch
                {
                    "slot" => row with { SlotKey = row.SlotKey + 100 },
                    "quantity" => row with { Quantity = row.Quantity + 1 },
                    "lifetime" => row with { Lifetime = InventoryLifetime.Until(Now.AddDays(1)) },
                    _ => row,
                }).ToArray(), default);
        }
        Assert.Null(await SelectAsync(target));
        var after = await Store.ListLoadoutSlotsAsync(UserNum, Scope, InventoryBag.CharacterBag, 100, default);
        Assert.Contains(after, row => row.ItemId == 120001 && (row.Flags & 1) != 0);
        Assert.DoesNotContain(after, row => row.ItemId == 120002 && (row.Flags & 1) != 0);
    }

    [Fact]
    public async Task ExpiredOwnershipCannotBeSelectedEvenWhenItsSnapshotStillMatches()
    {
        var target = await SeedAsync(InventoryLifetime.Until(Now));
        Assert.Null(await SelectAsync(target));
        Assert.Contains(120001, (await Store.GetLoadoutAsync(UserNum, Scope, default)).WornItemIds!);
    }

    [Fact]
    public async Task FirstMetadataInitializationDoesNotInvalidateTheSameTransactionsOwnershipCheck()
    {
        await SeedAsync();
        var target = (await Store.ListLoadoutSlotsAsync(UserNum, Scope, InventoryBag.CharacterBag, 100, default))
            .Single(row => row.ItemId == 202002);
        Assert.Null(target.ArticleIdentity);
        var committed = await Store.UpdateLoadoutSelectionSnapshotAsync(UserNum, Scope, Groups, target.ItemId, true, default,
            initialization: null, articleInitialization: new(UserNum, Scope, [target.ItemId], Now), target: new(target, Now));
        Assert.NotNull(committed);
        var owned = committed.CharacterBag.Single(row => row.ItemId == target.ItemId);
        Assert.NotNull(owned.ArticleIdentity);
        Assert.Equal(129, owned.Flags);
        Assert.Equal(owned.ArticleIdentity, committed.EquippedBag.Single(row => row.ItemId == target.ItemId).ArticleIdentity);
    }

    [Fact]
    public async Task OwnershipPreconditionCannotBeRetargetedToAnotherItem()
    {
        var target = await SeedAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => Store.UpdateLoadoutSelectionSnapshotAsync(UserNum, Scope, Groups,
            120001, true, default, initialization: null, articleInitialization: null, target: new(target, Now)).AsTask());
        Assert.Contains(120001, (await Store.GetLoadoutAsync(UserNum, Scope, default)).WornItemIds!);
    }

    private ValueTask<PetInventorySnapshot?> SelectAsync(InventorySlotRecord target) =>
        Store.UpdateLoadoutSelectionSnapshotAsync(UserNum, Scope, Groups, target.ItemId, true, default,
            initialization: null, articleInitialization: null, target: new(target, Now));

    private async Task<InventorySlotRecord> SeedAsync(InventoryLifetime? lifetime = null)
    {
        var rows = new[]
        {
            new InventorySlotRecord(UserNum, InventoryBag.CharacterBag, 1, 120001, 1, 129) { Lifetime = InventoryLifetime.Permanent },
            new InventorySlotRecord(UserNum, InventoryBag.CharacterBag, 2, 120002, 1, 128) { Lifetime = lifetime ?? InventoryLifetime.Permanent },
            new InventorySlotRecord(UserNum, InventoryBag.CharacterBag, 3, 202002, 1, 128) { Lifetime = InventoryLifetime.Permanent },
        };
        await Store.ReplaceSlotsAsync(UserNum, InventoryBag.CharacterBag, rows, default);
        await Store.ReplaceSlotsAsync(UserNum, InventoryBag.EquippedBag, rows.Select(row => row with { Bag = InventoryBag.EquippedBag }).ToArray(), default);
        await Store.UpdateLoadoutSelectionAsync(UserNum, Scope, Groups, null, false, default);
        return (await Store.ListLoadoutSlotsAsync(UserNum, Scope, InventoryBag.CharacterBag, 100, default)).Single(row => row.ItemId == 120002);
    }
}
