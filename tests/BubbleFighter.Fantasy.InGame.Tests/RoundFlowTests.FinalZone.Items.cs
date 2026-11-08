using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Sessions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    /// <summary>Competing claims yield one sender-inclusive award, and foreign or replaced sessions cannot transfer ownership.</summary>
    [Fact]
    public async Task FinalZoneItemClaimsAreSerializedAndExactOwnerOnly()
    {
        var rig = new Rig();
        rig.Round.FinalZoneEliminated = new Sink<RoundFinalZonePlayerEliminated>();
        var picked = new Sink<RoundFinalZoneItemPicked>();
        rig.Round.FinalZoneItemPicked = picked;
        var spawned = new Sink<RoundFinalZoneItemsSpawned>();
        rig.Round.FinalZoneItemsSpawned = spawned;
        try
        {
            var room = await StartFinalZoneRoundAsync(rig);
            var hostActor = RoomRegistry.SceneEntity(room.FindByGame(rig.Host.Id)!.Slot);
            var guestActor = RoomRegistry.SceneEntity(room.FindByGame(rig.Guest.Id)!.Slot);
            var item = FinalZoneFixtureItem(65536, 12);
            room.FinalZone!.Items = FinalZoneFixtureLedger(hostActor, guestActor);
            room.FinalZone.Items.Initialize([item]);
            await rig.Round.HandleFinalZoneItemPickupAsync(rig.Guest, new(item.ObjectId, hostActor), default);
            await rig.Round.HandleFinalZoneItemPickupAsync(CaptainPlayer(rig.Host.Id, "replacement"), new(item.ObjectId, hostActor), default);
            Assert.Empty(picked.To(rig.Host));
            await Task.WhenAll(
                rig.Round.HandleFinalZoneItemPickupAsync(rig.Host, new(item.ObjectId, hostActor), default).AsTask(),
                rig.Round.HandleFinalZoneItemPickupAsync(rig.Guest, new(item.ObjectId, guestActor), default).AsTask());
            var award = Assert.Single(picked.To(rig.Host));
            Assert.Equal(award, Assert.Single(picked.To(rig.Guest)));
            var winner = award.Actor == hostActor ? rig.Host : rig.Guest;
            await rig.Round.HandleFinalZoneItemsDroppedAsync(winner, new([item with { Quantity = 13 }]), default);
            Assert.Empty(spawned.To(rig.Host));
            await rig.Round.HandleFinalZoneItemsDroppedAsync(winner, new([item with { Quantity = 5 }]), default);
            var returned = Assert.Single(Assert.Single(spawned.To(rig.Host)).Items);
            Assert.Equal(65537, returned.ObjectId);
            Assert.Equal(7, Assert.Single(room.FinalZone.Items.Held(award.Actor)).Quantity);
            room.RoundCancelled = true;
            await rig.Round.HandleFinalZoneItemPickupAsync(winner, new(returned.ObjectId, award.Actor), default);
            Assert.Single(picked.To(rig.Host));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>The eliminated owner alone can publish a conserved backpack, and its recipient is identified by user number.</summary>
    [Fact]
    public async Task FinalZoneBackpackRequiresEliminationAndAwardsOneCurrentUser()
    {
        var rig = new Rig();
        rig.Round.FinalZoneEliminated = new Sink<RoundFinalZonePlayerEliminated>();
        rig.Round.FinalZoneItemPicked = new Sink<RoundFinalZoneItemPicked>();
        var backpacks = new Sink<RoundFinalZoneBackpackSpawned>();
        var picked = new Sink<RoundFinalZoneBackpackPicked>();
        rig.Round.FinalZoneBackpackSpawned = backpacks;
        rig.Round.FinalZoneBackpackPicked = picked;
        try
        {
            var third = CaptainPlayer(33, "third");
            var room = await StartFinalZoneRoundAsync(rig, third);
            var owner = room.FindByGame(rig.Guest.Id)!;
            var host = room.FindByGame(rig.Host.Id)!;
            var ownerActor = RoomRegistry.SceneEntity(owner.Slot);
            var hostActor = RoomRegistry.SceneEntity(host.Slot);
            room.FinalZone!.Items = FinalZoneFixtureLedger(ownerActor, hostActor);
            room.FinalZone.Items.Initialize([FinalZoneFixtureItem(65536, 12)]);
            await rig.Round.HandleFinalZoneItemPickupAsync(rig.Guest, new(65536, ownerActor), default);
            var drop = new RoundFinalZoneBackpackDropped(ownerActor, 1, 2, 3, 0, 0, 0, "",
                [new(9001, -1, 9, new([], []))]);
            await rig.Round.HandleFinalZoneBackpackDroppedAsync(rig.Guest, drop, default);
            Assert.Empty(backpacks.To(rig.Host));
            await rig.Round.HandleLifeReportedAsync(rig.Guest, new(RoundLifeEvent.Died, ownerActor), default);
            await rig.Round.HandleFinalZoneBackpackDroppedAsync(rig.Host, drop, default);
            Assert.Empty(backpacks.To(rig.Host));
            await rig.Round.HandleFinalZoneBackpackDroppedAsync(rig.Guest, drop, default);
            await rig.Round.HandleFinalZoneBackpackDroppedAsync(rig.Guest, drop, default);
            var backpack = Assert.Single(backpacks.To(rig.Host));
            await rig.Round.HandleFinalZoneBackpackPickupAsync(rig.Guest, new(backpack.ObjectId, 9001), default);
            Assert.Empty(picked.To(rig.Host));
            await rig.Round.HandleFinalZoneBackpackPickupAsync(rig.Host, new(backpack.ObjectId, 9001), default);
            await rig.Round.HandleFinalZoneBackpackPickupAsync(rig.Host, new(backpack.ObjectId, 9001), default);
            Assert.Equal(new(host.UserNum, backpack.ObjectId, 9001), Assert.Single(picked.To(rig.Host)));
            Assert.Equal(9, Assert.Single(room.FinalZone.Items.Held(hostActor)).Quantity);
            Assert.Empty(room.FinalZone.Items.Held(ownerActor));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    private static FinalZoneItemLedger FinalZoneFixtureLedger(params int[] actors) => new(
        new Dictionary<int, FinalZoneItemSpec> { [9001] = new("Fixture", 9001, "GunAmmo", "Fixture", -1, true, true, true) },
        actors, 65536, 32, 999);

    private static FinalZoneMapItem FinalZoneFixtureItem(int id, int quantity) =>
        new(id, 9001, quantity, 1, 2, 3, 0, 0, 0, false, new([], []));
}
