using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Sessions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Checks conservation across competing pickups, live returns and one-time terminal backpacks.</summary>
public sealed class FinalZoneItemLedgerTests
{
    private const int FirstObject = 65536;
    private static readonly FinalZoneItemSpec Ammo = new("FixtureAmmo", 9001, "GunAmmo", "catFixture", -1, true, true, true);

    /// <summary>Two owned rods are debited in order, linked once, and cannot be replayed or claimed as loose items afterward.</summary>
    [Fact]
    public void TrapEndpointsConserveInventoryAndValidateTheirOwnedPair()
    {
        var rod = new FinalZoneItemSpec("FixtureRod", 560, "Etc", "catBoobyTrap", 5921, false, false, false);
        var ledger = new FinalZoneItemLedger(new Dictionary<int, FinalZoneItemSpec> { [560] = rod }, [1, 2], FirstObject, 8, 999);
        ledger.Initialize([], [new(560, 5921, 3, new([], []))]);
        var first = new RoundFinalZoneTrapRequested(1, 560, 1, 1, 2, 3);
        Assert.Null(ledger.TryPlaceTrap(first with { Stage = 3 }));
        Assert.Null(ledger.TryPlaceTrap(first with { Actor = 3 }));
        Assert.Null(ledger.TryPlaceTrap(first with { ItemSpecId = 561 }));
        Assert.Equal(FirstObject, ledger.TryPlaceTrap(first)!.ObjectId);
        Assert.Null(ledger.TryPlaceTrap(first));
        Assert.Throws<InvalidDataException>(() => ledger.TryPlaceTrap(first with { Stage = 3, X = float.NaN }));
        Assert.Equal(2, Assert.Single(ledger.Held(1)).Quantity);
        Assert.Equal(FirstObject + 1, ledger.TryPlaceTrap(first with { Stage = 3 })!.ObjectId);
        var link = new RoundFinalZoneTrapLinked(1, FirstObject, FirstObject + 1, 5921, []);
        Assert.False(ledger.TryLinkTrap(link with { Actor = 2 }));
        Assert.False(ledger.TryLinkTrap(link with { WeaponId = 5922 }));
        Assert.False(ledger.TryLinkTrap(link with { FirstObjectId = FirstObject + 1, SecondObjectId = FirstObject }));
        Assert.True(ledger.TryLinkTrap(link));
        Assert.False(ledger.TryLinkTrap(link));
        Assert.False(ledger.TryPickUp(2, FirstObject));
        Assert.NotNull(ledger.TryPlaceTrap(first));
        Assert.Empty(ledger.Held(1));
        Assert.Null(ledger.TryPlaceTrap(first with { Stage = 3 }));
        Assert.Equal(3, ledger.IssuedObjects);
    }

    /// <summary>Recovering a loose first rod restores its quantity and allows the owner's next first endpoint.</summary>
    [Fact]
    public void LooseTrapRecoveryCancelsPendingPlacement()
    {
        var rod = new FinalZoneItemSpec("FixtureRod", 560, "Etc", "catBoobyTrap", 5921, false, false, false);
        var ledger = new FinalZoneItemLedger(new Dictionary<int, FinalZoneItemSpec> { [560] = rod }, [1], FirstObject, 2, 999);
        ledger.Initialize([], [new(560, 5921, 1, new([], []))]);
        var first = new RoundFinalZoneTrapRequested(1, 560, 1, 1, 2, 3);
        var spawned = ledger.TryPlaceTrap(first)!;
        Assert.True(ledger.TryPickUp(1, spawned.ObjectId));
        Assert.Equal(1, Assert.Single(ledger.Held(1)).Quantity);
        Assert.NotNull(ledger.TryPlaceTrap(first));
        Assert.Null(ledger.TryPlaceTrap(first with { Stage = 3 }));
        Assert.Equal(2, ledger.IssuedObjects);
    }

    /// <summary>An issued drone yields one box whose repeated specifications are removed one entry at a time.</summary>
    [Fact]
    public void DroneBoxUsesOneSharedBudgetAndExclusiveEntryTransfers()
    {
        var ledger = Create();
        ledger.Initialize([]);
        var drone = Assert.IsType<int>(ledger.TryCreateDrone());
        Assert.Equal(FirstObject, drone);
        Assert.Null(ledger.TryDropDrone(drone + 1, [Entry(5)]));
        Assert.Throws<InvalidDataException>(() => ledger.TryDropDrone(drone, [Entry(0)]));
        Assert.Equal(1, ledger.DroneCount);
        var box = Assert.IsType<int>(ledger.TryDropDrone(drone, [Entry(5), Entry(7)]));
        Assert.Equal(FirstObject + 1, box);
        Assert.Equal(0, ledger.DroneCount);
        Assert.Null(ledger.TryDropDrone(drone, [Entry(5)]));
        Assert.True(ledger.TryPickUpBackpack(1, box, Ammo.Id));
        Assert.Equal(5, Assert.Single(ledger.Held(1)).Quantity);
        Assert.True(ledger.TryPickUpBackpack(2, box, Ammo.Id));
        Assert.Equal(7, Assert.Single(ledger.Held(2)).Quantity);
        Assert.False(ledger.TryPickUpBackpack(1, box, Ammo.Id));
        Assert.Equal(2, ledger.IssuedObjects);
    }

    /// <summary>Native opening items are credited once to every participant before pickup and survive terminal validation.</summary>
    [Fact]
    public void NativeOpeningItemsAreConservedWithoutAllocatingWorldHandles()
    {
        var ledger = Create();
        Assert.Throws<InvalidDataException>(() => ledger.Initialize([], [Entry(1), Entry(1)]));
        Assert.Empty(ledger.Held(1));
        Assert.Empty(ledger.Held(2));
        ledger.Initialize([], [Entry(1)]);
        Assert.Equal(0, ledger.IssuedObjects);
        Assert.Equal(1, Assert.Single(ledger.Held(1)).Quantity);
        Assert.Equal(1, Assert.Single(ledger.Held(2)).Quantity);
        Assert.Throws<InvalidOperationException>(() => ledger.Initialize([], [Entry(1)]));
        Assert.Null(ledger.TryDropBackpack(Backpack(1, Entry(2))));
        Assert.NotNull(ledger.TryDropBackpack(Backpack(1, Entry(1))));
        Assert.Empty(ledger.Held(1));
        Assert.Equal(1, Assert.Single(ledger.Held(2)).Quantity);
    }

    /// <summary>Only one participant can receive a world item and a partial return cannot mint ammunition.</summary>
    [Fact]
    public void PickupAndPartialDropConserveGrantedAmount()
    {
        var ledger = Create();
        ledger.Initialize([Item(FirstObject, 12)]);
        Assert.False(ledger.TryPickUp(3, FirstObject));
        Assert.True(ledger.TryPickUp(1, FirstObject));
        Assert.False(ledger.TryPickUp(2, FirstObject));
        Assert.Null(ledger.TryDrop(1, [Item(0, 13)]));
        Assert.Equal(12, Assert.Single(ledger.Held(1)).Quantity);
        var drop = Assert.Single(ledger.TryDrop(1, [Item(0, 5)])!);
        Assert.Equal(FirstObject + 1, drop.ObjectId);
        Assert.Equal(7, Assert.Single(ledger.Held(1)).Quantity);
        Assert.True(ledger.TryPickUp(2, drop.ObjectId));
        Assert.Equal(5, Assert.Single(ledger.Held(2)).Quantity);
        Assert.False(ledger.TryPickUp(1, drop.ObjectId));
        Assert.Equal(0, ledger.GroundCount);
        Assert.Equal(2, ledger.IssuedObjects);
    }

    /// <summary>A terminal report may reduce consumed quantities, but cannot inflate, duplicate or replay its contents.</summary>
    [Fact]
    public void DeathBackpackAcceptsOnlyGrantedRemainderOnce()
    {
        var ledger = Create();
        ledger.Initialize([Item(FirstObject, 12)]);
        Assert.True(ledger.TryPickUp(1, FirstObject));
        Assert.Null(ledger.TryDropBackpack(Backpack(1, Entry(13))));
        Assert.Null(ledger.TryDropBackpack(Backpack(1, Entry(5), Entry(5))));
        Assert.Null(ledger.TryDropBackpack(Backpack(2, Entry(5))));
        var spawned = Assert.IsType<RoundFinalZoneBackpackSpawned>(ledger.TryDropBackpack(Backpack(1, Entry(7))));
        Assert.Equal(FirstObject + 1, spawned.ObjectId);
        Assert.Empty(ledger.Held(1));
        Assert.Null(ledger.TryDropBackpack(Backpack(1, Entry(7))));
        Assert.False(ledger.TryPickUpBackpack(1, spawned.ObjectId, Ammo.Id));
        Assert.True(ledger.TryPickUpBackpack(2, spawned.ObjectId, Ammo.Id));
        Assert.False(ledger.TryPickUpBackpack(2, spawned.ObjectId, Ammo.Id));
        Assert.Equal(7, Assert.Single(ledger.Held(2)).Quantity);
        Assert.Equal(0, ledger.BackpackCount);
    }

    /// <summary>Budget exhaustion, malformed coordinates and altered reinforcement never partially debit an inventory.</summary>
    [Fact]
    public void InvalidAndOverBudgetReturnsLeaveOwnershipIntact()
    {
        var ledger = Create(maximumObjects: 1);
        ledger.Initialize([Item(FirstObject, 12)]);
        Assert.True(ledger.TryPickUp(1, FirstObject));
        Assert.Null(ledger.TryDrop(1, [Item(0, 5)]));
        Assert.Null(ledger.TryDropBackpack(Backpack(1, Entry(5))));
        Assert.Equal(12, Assert.Single(ledger.Held(1)).Quantity);
        ledger = Create();
        ledger.Initialize([Item(FirstObject, 12)]);
        Assert.True(ledger.TryPickUp(1, FirstObject));
        Assert.Throws<InvalidDataException>(() => ledger.TryDrop(1, [Item(0, 5) with { X = float.NaN }]));
        Assert.Null(ledger.TryDrop(1, [Item(0, 5) with { Reinforcement = new([1], []) }]));
        Assert.Throws<InvalidDataException>(() => ledger.TryDropBackpack(Backpack(1, Entry(5) with { WeaponId = 123 })));
        Assert.Equal(12, Assert.Single(ledger.Held(1)).Quantity);
        Assert.Equal(1, ledger.IssuedObjects);
    }

    /// <summary>Caller-owned lists cannot change a published object's identity, and even an empty initialization happens once.</summary>
    [Fact]
    public void ManifestIsAtomicAndRetainsImmutableReinforcement()
    {
        var ledger = Create();
        Assert.Throws<InvalidDataException>(() => ledger.Initialize([Item(FirstObject, 1), Item(FirstObject + 2, 1)]));
        Assert.Equal(0, ledger.GroundCount);
        var mutable = new[] { 7 };
        ledger.Initialize([Item(FirstObject, 1) with { Reinforcement = new(mutable, []) }]);
        mutable[0] = 8;
        Assert.True(ledger.TryPickUp(1, FirstObject));
        Assert.Equal(7, Assert.Single(Assert.Single(ledger.Held(1)).Reinforcement.Unknown4));
        Assert.Throws<InvalidOperationException>(() => ledger.Initialize([]));
        ledger = Create();
        ledger.Initialize([]);
        Assert.Throws<InvalidOperationException>(() => ledger.Initialize([]));
    }

    private static FinalZoneItemLedger Create(int maximumObjects = 8) => new(
        new Dictionary<int, FinalZoneItemSpec> { [Ammo.Id] = Ammo }, [1, 2], FirstObject, maximumObjects, 999);
    private static FinalZoneMapItem Item(int id, int amount) => new(id, Ammo.Id, amount, 1, 2, 3, 0, 0, 0, true, new([], []));
    private static FinalZoneBackpackEntry Entry(int amount) => new(Ammo.Id, Ammo.WeaponId, amount, new([], []));
    private static RoundFinalZoneBackpackDropped Backpack(int actor, params FinalZoneBackpackEntry[] items) =>
        new(actor, 1, 2, 3, 0, 0, 0, "fixture", items);
}
