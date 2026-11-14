using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Pets;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Lobby.Tests;

public sealed partial class FighterInfoTests
{
    /// <summary>A selected native pet remains unavailable until the inspected build owns a complete saved appearance.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task InspectionWaitsForOwnedPetMetadataWithoutProjectingOrRepairing(bool hasIdentity, bool hasBuildProgression)
    {
        using var online = new PlayerSessionScope();
        var asker = new FeatureSession(1);
        var target = new FeatureSession(2);
        online.Attach(asker, new(10, "asker", "Asker", 5));
        online.Attach(target, new(20, "target", "Target", 9));
        var scope = new InventoryLoadoutScope("cn-717", [300007, 301001]);
        var identity = new PetEntitlementIdentity(5, PetArticleKeys.First + 5);
        var progress = new PetProgression(1, 37, DateTimeOffset.MaxValue, null, null, null);
        var otherBuild = PetProgressions.Empty.With("kr-1256", progress with { Experience = 99 });
        InventorySlotRecord[] rows = [Row(20, 300007), Row(20, 301001) with
        {
            PetIdentity = hasIdentity ? identity : null,
            Pet = hasBuildProgression ? otherBuild.With(scope.Key, progress) : otherBuild,
        }];
        var loadouts = new InspectionLoadouts(scope, rows);
        var pets = new InspectionPets(scope.Key);
        var inventory = new Inventory { Read = _ => throw new InvalidOperationException("Unscoped inspection read.") };
        var events = new EventBus(NullLogger.Instance);
        var projections = 0;
        using var projection = events.Intercept<InventoryPresentationRequested, InventoryItemPresentation>((request, _, _) =>
        {
            projections++;
            if (request.Slot.ItemId != 301001) return ValueTask.FromResult(InventoryItemPresentation.Empty);
            Assert.Equal(identity, request.Slot.PetIdentity);
            Assert.Equal(progress, request.Slot.Pet!.Find(scope.Key));
            return ValueTask.FromResult(new InventoryItemPresentation([new(301001, 7, [1, 37, 0, 0], 0, "")], null));
        });
        var replies = new Replies();
        await FighterInfoHandler.HandleAsync(online, inventory, replies, asker, new(20), default,
            events: events, loadouts: loadouts, scope: scope, petEntitlements: pets);
        var unavailable = Assert.Single(replies.Sent).Reply;
        Assert.Null(unavailable.Fighter);
        Assert.Null(unavailable.Appearance);
        Assert.Equal(0, projections);
        Assert.Equal(1, loadouts.Reads);

        rows[1] = rows[1] with { PetIdentity = identity, Pet = otherBuild.With(scope.Key, progress) };
        replies.Sent.Clear();
        await FighterInfoHandler.HandleAsync(online, inventory, replies, asker, new(20), default,
            events: events, loadouts: loadouts, scope: scope, petEntitlements: pets);
        var available = Assert.Single(replies.Sent).Reply;
        Assert.NotNull(available.Fighter);
        Assert.Equal(new[] { 300007, 301001 }, available.Appearance!.EquippedItemIds);
        Assert.Equal(new uint[] { 1, 37, 0, 0 }, Assert.Single(available.Appearance.ItemAttributes).Values);
        Assert.Equal(2, projections);
        Assert.Equal(2, loadouts.Reads);
        Assert.Equal(99, rows[1].Pet!.Find("kr-1256")!.Experience);

        projection.Dispose();
        using var failure = events.Intercept<InventoryPresentationRequested, InventoryItemPresentation>((_, _, _) =>
            throw new InvalidDataException("Unrelated projection failure."));
        await Assert.ThrowsAsync<InvalidDataException>(() => FighterInfoHandler.HandleAsync(online, inventory,
            replies, asker, new(20), default, events: events, loadouts: loadouts, scope: scope,
            petEntitlements: pets).AsTask());
        Assert.Single(replies.Sent);
    }

    /// <summary>Inspection uses the supplied canonical read-only projection once, then derives modifiers without repairs or later reads.</summary>
    [Fact]
    public async Task InspectionKeepsScopedRowsAndModifiersFromOneRead()
    {
        using var online = new PlayerSessionScope();
        var asker = new FeatureSession(1);
        var target = new FeatureSession(2);
        online.Attach(asker, new(10, "asker", "Asker", 5));
        online.Attach(target, new(20, "target", "Target", 9));
        var scope = new InventoryLoadoutScope("kr-812", [300007, 301001]);
        InventorySlotRecord[] rows = [Row(20, 300007), Row(20, 301001), Row(20, 300029)];
        var loadouts = new InspectionLoadouts(scope, rows);
        var events = new EventBus(NullLogger.Instance);
        uint[] words = [1, 37, 0, 0];
        using var projection = events.Intercept<InventoryPresentationRequested, InventoryItemPresentation>((request, _, _) =>
        {
            Assert.Equal("test/game", request.Protocol);
            Assert.NotEqual(300029, request.Slot.ItemId);
            if (request.Slot.ItemId != 301001) return ValueTask.FromResult(InventoryItemPresentation.Empty);
            rows[1] = rows[1] with { ItemId = 200001 };
            return ValueTask.FromResult(new InventoryItemPresentation([new RoomItemAttribute(301001, 7, words, 0, "")], null));
        });
        var replies = new Replies();
        await FighterInfoHandler.HandleAsync(online, new Inventory { Read = _ => throw new InvalidOperationException("Unscoped read.") },
            replies, asker, new(20), default, events: events, loadouts: loadouts, scope: scope);
        words[1] = 999;
        var appearance = Assert.Single(replies.Sent).Reply.Appearance!;
        Assert.Equal(1, loadouts.Reads);
        Assert.Equal(new[] { 300007, 301001 }, appearance.EquippedItemIds);
        Assert.Equal(new uint[] { 1, 37, 0, 0 }, Assert.Single(appearance.ItemAttributes).Values);
        Assert.DoesNotContain(300029, appearance.EquippedItemIds);
    }

    private sealed class InspectionLoadouts(InventoryLoadoutScope expected, InventorySlotRecord[] rows) : IInventoryLoadoutStore
    {
        internal int Reads { get; private set; }
        /// <summary>Records a canonical character-bag inspection and returns only rows supported by the expected build scope.</summary>
        public ValueTask<IReadOnlyList<InventorySlotRecord>> ListLoadoutSlotsAsync(long userNum, InventoryLoadoutScope scope,
            InventoryBag bag, int maxCount, CancellationToken ct)
        {
            Assert.Same(expected, scope);
            Assert.Equal(20, userNum);
            Assert.Equal(InventoryBag.CharacterBag, bag);
            Reads++;
            return ValueTask.FromResult<IReadOnlyList<InventorySlotRecord>>(rows.Where(row => scope.Supports(row.ItemId)).ToArray());
        }
        /// <summary>Rejects unscoped inventory reads during fighter inspection.</summary>
        public ValueTask<IReadOnlyList<InventorySlotRecord>> ListSlotsAsync(long userNum, InventoryBag bag, int maxCount, CancellationToken ct) =>
            throw new InvalidOperationException("Inspection must read the canonical loadout projection.");
        /// <summary>Rejects loadout materialization during fighter inspection.</summary>
        public ValueTask<InventoryLoadoutState> GetLoadoutAsync(long userNum, InventoryLoadoutScope scope, CancellationToken ct) =>
            throw new InvalidOperationException("Inspection must not materialize a loadout.");
        /// <summary>Rejects equipment normalization or selection changes during fighter inspection.</summary>
        public ValueTask<IReadOnlyList<InventorySlotRecord>?> UpdateLoadoutSelectionAsync(long userNum, InventoryLoadoutScope scope,
            IReadOnlyList<InventoryWearGroup> groups, int? itemId, bool worn, CancellationToken ct) => throw new InvalidOperationException("Inspection must not repair selection.");
        /// <summary>Rejects preset initialization or repair during fighter inspection.</summary>
        public ValueTask<InventoryLoadoutState> UpdateLoadoutPresetsAsync(long userNum, InventoryLoadoutScope scope,
            byte? activeSlot, WeaponPresetPage[]? pages, bool initializePages, CancellationToken ct) => throw new InvalidOperationException("Inspection must not repair presets.");
        /// <summary>Rejects inventory ownership replacement during fighter inspection.</summary>
        public ValueTask ReplaceSlotsAsync(long userNum, InventoryBag bag, IReadOnlyList<InventorySlotRecord> slots, CancellationToken ct) =>
            throw new InvalidOperationException("Inspection must not replace ownership.");
        /// <summary>Rejects worn-flag changes during fighter inspection.</summary>
        public ValueTask<InventorySlotRecord?> SetSlotWornAsync(long userNum, int itemId, bool worn, CancellationToken ct) =>
            throw new InvalidOperationException("Inspection must not change selection.");
    }

    private sealed class InspectionPets(string build) : IPetEntitlements
    {
        /// <summary>Gets the build whose pet classification is exposed to the inspection handler.</summary>
        public string BuildKey => build;
        /// <summary>Identifies the fixture pet among the inspected player's owned items.</summary>
        public bool IsPetItem(int itemId) => itemId == 301001;
        /// <summary>Rejects pet initialization commands because inspection must leave metadata unchanged.</summary>
        public PetInitializationCommand CreateInitialization(long userNum, DateTimeOffset now) =>
            throw new InvalidOperationException("Inspection must not initialize pet metadata.");
        /// <summary>Rejects pet preparation that would initialize or reread inventory during inspection.</summary>
        public ValueTask<PetInventorySnapshot> PrepareAsync(long userNum, CancellationToken ct) =>
            throw new InvalidOperationException("Inspection must not prepare or reread inventory.");
        /// <summary>Rejects Article resolution from a second inventory snapshot during inspection.</summary>
        public ValueTask<PetEquipmentTarget?> ResolveAsync(long userNum, ulong articleKey, CancellationToken ct) =>
            throw new InvalidOperationException("Inspection must not resolve another snapshot.");
        /// <summary>Rejects pet equipment changes during inspection.</summary>
        public ValueTask<PetInventorySnapshot?> SelectAsync(long userNum, PetEquipmentTarget target,
            IReadOnlyList<InventoryWearGroup> groups, bool worn, CancellationToken ct) =>
            throw new InvalidOperationException("Inspection must not change pet selection.");
        /// <summary>Rejects ordinary equipment changes during inspection.</summary>
        public ValueTask<PetInventorySnapshot?> SelectItemAsync(long userNum, int itemId,
            IReadOnlyList<InventoryWearGroup> groups, bool worn, CancellationToken ct) =>
            throw new InvalidOperationException("Inspection must not change item selection.");
    }
}
