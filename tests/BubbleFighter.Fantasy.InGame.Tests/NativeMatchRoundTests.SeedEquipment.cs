using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Sessions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class NativeMatchRoundTests
{
    /// <summary>The shared flow freezes both loadouts and temporary specs before any validation callback can mutate the originals.</summary>
    [Fact]
    public async Task SeedEquipmentIsAnOwnedSnapshotOfTheAcceptedRoom()
    {
        var items = new[] { 300001, 203103, 201001 };
        var selections = new[] { 3103, 1001, 7001, 5001, 0 };
        var validator = new SeedValidator { Inspect = _ => { items[1] = 202001; selections[0] = 2001; } };
        await using var rig = new Rig("cn-324", 18, seedInputs: validator);
        await rig.StartAsync(101);
        rig.Room.Seats[0].EquippedItemIds = items;
        rig.Room.Seats[0].InventorySelection = new RoomInventorySelection(1, selections);
        rig.Room.Seats[1].EquippedItemIds = new[] { 300001, 202001, 201001 };
        await ReadySeedRelaysAsync(rig);
        await rig.Round.HandleMatchConnectionReportedAsync(rig.Guest, new(true), default);
        Assert.Equal(2, validator.Requests.Count);
        Assert.Equal(2, rig.Seeds.Sent.Count);
        var request = rig.Seeds.Sent[0].Message;
        Assert.Equal(101, request.Equipment!.ModeId);
        Assert.Contains(203103, request.Equipment.WornItemIds);
        Assert.DoesNotContain(202001, request.Equipment.WornItemIds);
        Assert.Equal(3103, request.Equipment.PrimarySpecification);
        Assert.Equal(1001, request.Equipment.SidearmSpecification);
        Assert.Same(validator.Requests[0], request);
        Assert.Equal(rig.Room.StartGeneration, request.Generation);
    }

    /// <summary>An invalid second seat leaves the countdown and every seed sender untouched, without marking a countdown complete.</summary>
    [Fact]
    public async Task EverySeedInputIsValidatedBeforeAnyCountdownOrSeedSend()
    {
        var validator = new SeedValidator { RejectSecond = true };
        await using var rig = new Rig("cn-324", 18, seedInputs: validator);
        await rig.StartAsync();
        await ReadySeedRelaysAsync(rig);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rig.Round.HandleMatchConnectionReportedAsync(rig.Guest, new(true), default).AsTask());
        Assert.Equal(2, validator.Requests.Count);
        Assert.Empty(rig.Countdowns.Sent);
        Assert.Empty(rig.Seeds.Sent);
        Assert.NotEqual(rig.Room.StartGeneration, rig.Room.CountdownGeneration);
        validator.RejectSecond = false;
        await rig.Round.HandleMatchConnectionReportedAsync(rig.Guest, new(true), default);
        Assert.Equal(2, rig.Countdowns.Sent.Count);
        Assert.Equal(2, rig.Seeds.Sent.Count);
    }

    /// <summary>Empty temporary selectors accepted by the shared room boundary are normalized before seed validation.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task NativeEmptySelectorsPreserveTheAcceptedWornSnapshot(int sentinel)
    {
        var validator = new SeedValidator();
        await using var rig = new Rig("cn-324", 18, seedInputs: validator);
        await rig.StartAsync();
        rig.Room.Seats[0].EquippedItemIds = new[] { 300001, 203103, 201001 };
        rig.Room.Seats[0].InventorySelection = new(1, new[] { sentinel, sentinel, 7001, 5001, 0 });
        await ReadySeedRelaysAsync(rig);
        await rig.Round.HandleMatchConnectionReportedAsync(rig.Guest, new(true), default);
        var equipment = validator.Requests[0].Equipment!;
        Assert.Null(equipment.PrimarySpecification);
        Assert.Null(equipment.SidearmSpecification);
        Assert.Contains(203103, equipment.WornItemIds);
        Assert.Same(equipment, rig.Seeds.Sent[0].Message.Equipment);
        Assert.Equal(2, rig.Countdowns.Sent.Count);
    }

    /// <summary>Every seat's request carries the same roster: each occupant's actor with that occupant's own worn set.</summary>
    [Fact]
    public async Task EverySeedRequestCarriesTheWholeRoster()
    {
        var validator = new SeedValidator();
        await using var rig = new Rig("cn-324", 18, seedInputs: validator);
        await rig.StartAsync();
        rig.Room.Seats[0].EquippedItemIds = new[] { 300001, 203103 };
        rig.Room.Seats[1].EquippedItemIds = new[] { 300002, 202001 };
        await ReadySeedRelaysAsync(rig);
        await rig.Round.HandleMatchConnectionReportedAsync(rig.Guest, new(true), default);
        Assert.Equal(2, rig.Seeds.Sent.Count);
        var first = rig.Seeds.Sent[0].Message;
        var second = rig.Seeds.Sent[1].Message;
        Assert.Equal(first.Roster, second.Roster);
        var occupants = first.Roster!.Occupants;
        Assert.Equal([first.EntityId, second.EntityId], occupants.Select(occupant => occupant.EntityId));
        Assert.Same(first.Equipment, occupants[0].Equipment);
        Assert.Same(second.Equipment, occupants[1].Equipment);
        Assert.Contains(202001, occupants[1].Equipment!.WornItemIds);
    }

    private static async Task ReadySeedRelaysAsync(Rig rig)
    {
        await rig.LoadAsync(rig.Host);
        await rig.LoadAsync(rig.Guest);
        var hostRelay = new Session(101, "cn-324");
        var guestRelay = new Session(102, "cn-324");
        rig.Registry.MarkInitialized(hostRelay, await rig.Registry.BindCapabilityAsync(hostRelay, new(rig.Offers.Sent[0].Message.Ticket), default));
        rig.Registry.MarkInitialized(guestRelay, await rig.Registry.BindCapabilityAsync(guestRelay, new(rig.Offers.Sent[1].Message.Ticket), default));
        await rig.Round.HandleMatchConnectionReportedAsync(rig.Host, new(true), default);
    }

    /// <summary>Weapon routes coexist with the seed policy; only actual pickups in fixed-loadout modes are refused without mutation.</summary>
    [Theory]
    [InlineData(16, true, false)]
    [InlineData(101, true, false)]
    [InlineData(3, true, true)]
    [InlineData(16, false, true)]
    [InlineData(101, false, true)]
    public async Task WeaponPickupOnlyRefusesTheActiveFixedLoadoutMode(int mode, bool fixedEquipment, bool picked)
    {
        var validator = new SeedValidator { FixedEquipment = fixedEquipment };
        await using var rig = new Rig("cn-324", 18, seedInputs: validator, weaponObjects: true);
        await rig.StartAsync(mode);
        lock (rig.Room.Gate) rig.Room.MarkPlaying();
        await rig.Round.HandleWeaponDropAsync(rig.Host, new(1, 2, 3, 0, 0, 0, 2001, 0, 0, true), default);
        var created = rig.WeaponDrops.Sent[0].Message;
        var seat = rig.Room.Seats[0];
        var request = new RoundWeaponPickupRequested(created.ObjectId, RoomRegistry.SceneEntity(seat.Slot), seat.Slot);
        await rig.Round.HandleWeaponPickupAsync(rig.Host, request, default);
        await rig.Round.HandleWeaponPickupAsync(rig.Host, request, default);
        Assert.Equal(picked ? 2 : 0, rig.WeaponPickups.Sent.Count);
        Assert.Equal(!picked, rig.Room.Weapons!.Objects.Contains(created.ObjectId));
        Assert.Equal(!picked, rig.Room.Weapons.FixedSeedRefusalLogged);
        Assert.Empty(rig.Seeds.Sent);
    }

    /// <summary>A relay lost during input validation cannot receive stale seeds or open a countdown.</summary>
    [Fact]
    public async Task TransportReadinessIsRecheckedAfterSeedValidation()
    {
        Rig? active = null;
        var validator = new SeedValidator { Inspect = _ => active!.Room.Seats[0].RelayConfirmed = false };
        await using var rig = new Rig("cn-324", 18, seedInputs: validator);
        active = rig;
        await rig.StartAsync();
        await ReadySeedRelaysAsync(rig);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rig.Round.HandleMatchConnectionReportedAsync(rig.Guest, new(true), default).AsTask());
        Assert.Empty(rig.Countdowns.Sent);
        Assert.Empty(rig.Seeds.Sent);
        Assert.NotEqual(rig.Room.StartGeneration, rig.Room.CountdownGeneration);
    }

    private sealed class SeedValidator : IMatchSeedInputValidator
    {
        /// <summary>Gets the relay protocol exercised by this shared-flow fixture.</summary>
        public string Protocol => "cn-324/relay";
        internal bool FixedEquipment { get; init; } = true;
        /// <summary>Models the optional policy's mode-scoped live weapon restriction.</summary>
        public bool RequiresFixedEquipment(int modeId) => FixedEquipment && modeId is 16 or 101;
        internal List<MatchSeedTablesRequested> Requests { get; } = [];
        internal bool RejectSecond { get; set; }
        internal Action<MatchSeedTablesRequested>? Inspect { get; init; }

        /// <summary>Records validation without publishing or drawing any seed data.</summary>
        public void Validate(MatchSeedTablesRequested request)
        {
            Requests.Add(request);
            if (RejectSecond && Requests.Count == 2) throw new InvalidOperationException("Second seat has invalid equipment.");
            Inspect?.Invoke(request);
        }
    }
}
