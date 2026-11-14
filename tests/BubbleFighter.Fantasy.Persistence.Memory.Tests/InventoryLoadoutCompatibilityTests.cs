using BubbleFighter.Fantasy.Contracts.Inventory;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Memory.Tests;

/// <summary>Checks binary entry points and safe default dispatch for providers compiled before Article selection support.</summary>
public sealed class InventoryLoadoutCompatibilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 16, 0, 0, TimeSpan.Zero);
    private static readonly InventoryLoadoutScope Scope = new("compatibility", [202002, 301001, 301002]);
    private static readonly InventoryWearGroup[] Groups = [new(301001, 301002, 301001)];
    private static readonly InventorySlotRecord Equipment = new(42, InventoryBag.CharacterBag, 1, 202002, 1, 128)
        { Lifetime = InventoryLifetime.Permanent };
    private static readonly PetInitializationCommand Initialization = new(42, Scope, [301001, 301002],
        new(0, 0, Now.AddHours(1), null, null, null), Now);
    private static readonly Type[] LegacyParameters =
    [
        typeof(long), typeof(InventoryLoadoutScope), typeof(IReadOnlyList<InventoryWearGroup>), typeof(int?),
        typeof(bool), typeof(CancellationToken), typeof(PetInitializationCommand),
    ];

    /// <summary>The original CLR member remains resolvable, and only the extended ownership target is optional.</summary>
    [Fact]
    public void SnapshotOverloadsPreserveOriginalClrSignatureAndUnambiguousArguments()
    {
        var legacy = typeof(IInventoryLoadoutStore).GetMethod(nameof(IInventoryLoadoutStore.UpdateLoadoutSelectionSnapshotAsync), LegacyParameters);
        Assert.NotNull(legacy);
        Assert.Equal(typeof(ValueTask<PetInventorySnapshot>), legacy.ReturnType);
        Assert.True(legacy.GetParameters()[6].IsOptional);
        Assert.Null(legacy.GetParameters()[6].DefaultValue);
        var extended = typeof(IInventoryLoadoutStore).GetMethod(nameof(IInventoryLoadoutStore.UpdateLoadoutSelectionSnapshotAsync),
            [.. LegacyParameters, typeof(ArticleInitializationCommand), typeof(InventorySelectionTarget)]);
        Assert.NotNull(extended);
        Assert.False(extended.GetParameters()[6].IsOptional);
        Assert.False(extended.GetParameters()[7].IsOptional);
        Assert.True(extended.GetParameters()[8].IsOptional);
        Assert.Null(extended.GetParameters()[8].DefaultValue);
    }

    /// <summary>A legacy provider receives every original argument and its exact result when no new capability is requested.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExtendedDefaultForwardsCompatibleCallsToTheLegacyProvider(bool refused)
    {
        PetInventorySnapshot? expected = refused ? null : new([Equipment], [Equipment with { Bag = InventoryBag.EquippedBag }]);
        var legacy = new LegacySnapshotStore(expected);
        IInventoryLoadoutStore store = legacy;
        using var cancellation = new CancellationTokenSource();
        var result = await store.UpdateLoadoutSelectionSnapshotAsync(42, Scope, Groups, Equipment.ItemId, true,
            cancellation.Token, Initialization, articleInitialization: null);
        Assert.Same(expected, result);
        Assert.Equal(1, legacy.CallCount);
        Assert.Equal(new SelectionCall(42, Scope, Groups, Equipment.ItemId, true, cancellation.Token, Initialization), legacy.LastCall);
    }

    /// <summary>Legacy defaults reject Article preparation and exact ownership checks before a legacy provider can mutate state.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExtendedRequirementsFailBeforeLegacySelection(bool articles, bool ownership)
    {
        var legacy = new LegacySnapshotStore(new([Equipment], []));
        IInventoryLoadoutStore store = legacy;
        await Assert.ThrowsAsync<NotSupportedException>(() => store.UpdateLoadoutSelectionSnapshotAsync(42, Scope, Groups,
            Equipment.ItemId, true, default, Initialization,
            articles ? new(42, Scope, [Equipment.ItemId], Now) : null,
            ownership ? new(Equipment, Now) : null).AsTask());
        Assert.Equal(0, legacy.CallCount);
        Assert.Null(legacy.LastCall);
    }

    /// <summary>Stores without snapshot support still fail loudly instead of composing separate inventory operations.</summary>
    [Fact]
    public async Task UnsupportedStoreRetainsTheOriginalDefaultFailure()
    {
        IInventoryLoadoutStore store = new UnsupportedSnapshotStore();
        await Assert.ThrowsAsync<NotSupportedException>(() => store.UpdateLoadoutSelectionSnapshotAsync(42, Scope, Groups,
            Equipment.ItemId, true, default).AsTask());
        await Assert.ThrowsAsync<NotSupportedException>(() => store.UpdateLoadoutSelectionSnapshotAsync(42, Scope, Groups,
            Equipment.ItemId, true, default, Initialization).AsTask());
        await Assert.ThrowsAsync<NotSupportedException>(() => store.UpdateLoadoutSelectionSnapshotAsync(42, Scope, Groups,
            Equipment.ItemId, true, default, Initialization, articleInitialization: null).AsTask());
    }

    /// <summary>The original three-argument selection member binds directly and retains both ownership domains.</summary>
    [Fact]
    public void OriginalSelectionSignatureStillCapturesPetAndArticleOwnership()
    {
        var method = typeof(InventoryLoadouts).GetMethod(nameof(InventoryLoadouts.Selection),
            [typeof(InventoryLoadoutState), typeof(IReadOnlyList<InventorySlotRecord>), typeof(bool)]);
        Assert.NotNull(method);
        Assert.True(method.GetParameters()[2].IsOptional);
        Assert.False(Assert.IsType<bool>(method.GetParameters()[2].DefaultValue));
        var select = method.CreateDelegate<Func<InventoryLoadoutState, IReadOnlyList<InventorySlotRecord>, bool, InventoryLoadoutState>>();
        InventorySlotRecord[] rows =
        [
            Equipment with { ItemId = 301001, Flags = 129, PetIdentity = new(7, PetArticleKeys.First) },
            Equipment with { SlotKey = 2, Flags = 129, ArticleIdentity = new(8, PetArticleKeys.First + 1) },
        ];
        var result = select(new(null, 0, null), rows, true);
        Assert.Equal(new[] { 301001, 202002 }, result.WornItemIds);
        Assert.Equal(new[] { 7UL }, result.WornPetEntitlementIds);
        Assert.Equal(new[] { 8UL }, result.WornArticleEntitlementIds);
    }

    private sealed record SelectionCall(long UserNum, InventoryLoadoutScope Scope, IReadOnlyList<InventoryWearGroup> Groups,
        int? ItemId, bool Worn, CancellationToken Cancellation, PetInitializationCommand? Initialization);

    private sealed class LegacySnapshotStore(PetInventorySnapshot? result) : UnsupportedSnapshotStore, IInventoryLoadoutStore
    {
        public int CallCount { get; private set; }
        public SelectionCall? LastCall { get; private set; }

        public ValueTask<PetInventorySnapshot?> UpdateLoadoutSelectionSnapshotAsync(long userNum, InventoryLoadoutScope scope,
            IReadOnlyList<InventoryWearGroup> groups, int? itemId, bool worn, CancellationToken ct,
            PetInitializationCommand? initialization = null)
        {
            CallCount++;
            LastCall = new(userNum, scope, groups, itemId, worn, ct, initialization);
            return ValueTask.FromResult(result);
        }
    }

    private class UnsupportedSnapshotStore : IInventoryLoadoutStore
    {
        public ValueTask<IReadOnlyList<InventorySlotRecord>> ListLoadoutSlotsAsync(long userNum, InventoryLoadoutScope scope,
            InventoryBag bag, int maxCount, CancellationToken ct) => throw UnexpectedAccess();

        public ValueTask<InventoryLoadoutState> GetLoadoutAsync(long userNum, InventoryLoadoutScope scope, CancellationToken ct) =>
            throw UnexpectedAccess();

        public ValueTask<IReadOnlyList<InventorySlotRecord>?> UpdateLoadoutSelectionAsync(long userNum, InventoryLoadoutScope scope,
            IReadOnlyList<InventoryWearGroup> groups, int? itemId, bool worn, CancellationToken ct) => throw UnexpectedAccess();

        public ValueTask<InventoryLoadoutState> UpdateLoadoutPresetsAsync(long userNum, InventoryLoadoutScope scope,
            byte? activeSlot, WeaponPresetPage[]? pages, bool initializePages, CancellationToken ct) => throw UnexpectedAccess();

        public ValueTask<IReadOnlyList<InventorySlotRecord>> ListSlotsAsync(long userNum, InventoryBag bag, int maxCount,
            CancellationToken ct) => throw UnexpectedAccess();

        public ValueTask ReplaceSlotsAsync(long userNum, InventoryBag bag, IReadOnlyList<InventorySlotRecord> slots,
            CancellationToken ct) => throw UnexpectedAccess();

        public ValueTask<InventorySlotRecord?> SetSlotWornAsync(long userNum, int itemId, bool worn, CancellationToken ct) =>
            throw UnexpectedAccess();

        private static InvalidOperationException UnexpectedAccess() =>
            new("Snapshot defaults must not read or mutate separate inventory state.");
    }
}
