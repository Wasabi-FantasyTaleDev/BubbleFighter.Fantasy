using BubbleFighter.Fantasy.Contracts.Fighters;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Wallet;
using BubbleFighter.Fantasy.Persistence.Memory;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Memory.Tests;

/// <summary>
/// Store behaviour of the in-memory repositories over one shared table: reads of
/// unknown players are empty, replaces store the whole list in order, bags are
/// isolated per player and per bag, the money / inventory pair the shop's
/// purchase flow uses round-trips through the same table, the worn-flag flip
/// targets only the character-bag rows of one item id, and the active
/// weapon-preset slot defaults to 0, round-trips per player and honours the
/// slot bound.
/// </summary>
public sealed class MemoryRepositoryRoundTripTests
{
    [Fact]
    public async Task UnknownPlayerReadsAreEmpty()
    {
        var table = new MemoryPlayerTable(16);
        var inventory = new MemoryInventoryRepository(table);
        var money = new MemoryPlayerWalletRepository(table);
        var fighters = new MemoryFighterRepository(table);

        Assert.Null(await money.FindByUserNumAsync(7, CancellationToken.None));
        Assert.Empty(await inventory.ListSlotsAsync(7, InventoryBag.CharacterBag, 1, CancellationToken.None));
        Assert.Empty(await fighters.ListByUserNumAsync(7, CancellationToken.None));
    }

    [Fact]
    public async Task MoneyUpsertRoundTripsAndReplaces()
    {
        var table = new MemoryPlayerTable(16);
        var money = new MemoryPlayerWalletRepository(table);

        var seeded = new PlayerWallet(1, 100000, 0);
        await money.UpsertAsync(seeded, CancellationToken.None);
        var read = await money.FindByUserNumAsync(1, CancellationToken.None);
        Assert.Equal(seeded, read);

        var charged = seeded with { Lucci = 90000 };
        await money.UpsertAsync(charged, CancellationToken.None);
        Assert.Equal(charged, await money.FindByUserNumAsync(1, CancellationToken.None));
    }

    [Fact]
    public async Task ReplaceStoresWholeBagInOrderAndReplacesIt()
    {
        var table = new MemoryPlayerTable(16);
        var inventory = new MemoryInventoryRepository(table);

        var first = new List<InventorySlotRecord>
        {
            new(1, InventoryBag.CharacterBag, 11, 300001, 99999, 0),
            new(1, InventoryBag.CharacterBag, 12, 300002, 99999, 0),
        };
        await inventory.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, first, CancellationToken.None);
        Assert.Equal(first, await inventory.ListSlotsAsync(1, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, CancellationToken.None));

        var second = new List<InventorySlotRecord>
        {
            new(1, InventoryBag.CharacterBag, 11, 300001, 99999, 0),
            new(1, InventoryBag.CharacterBag, 12, 300002, 99999, 0),
            new(1, InventoryBag.CharacterBag, 13, 444444, 99999, 0),
        };
        await inventory.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, second, CancellationToken.None);
        Assert.Equal(second, await inventory.ListSlotsAsync(1, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, CancellationToken.None));
    }

    [Fact]
    public async Task BagsAreIsolatedPerPlayerAndPerBag()
    {
        var table = new MemoryPlayerTable(16);
        var inventory = new MemoryInventoryRepository(table);

        var bag = new List<InventorySlotRecord> { new(1, InventoryBag.CharacterBag, 11, 300001, 99999, 0) };
        await inventory.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, bag, CancellationToken.None);

        Assert.Empty(await inventory.ListSlotsAsync(2, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, CancellationToken.None));
        Assert.Empty(await inventory.ListSlotsAsync(1, InventoryBag.EquippedBag, InventoryLimits.MaxInventorySlotsPerBag, CancellationToken.None));
        Assert.Equal(bag, await inventory.ListSlotsAsync(1, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, CancellationToken.None));
    }


    [Fact]
    public async Task FightersUpsertInSlotOrderAndReplace()
    {
        var table = new MemoryPlayerTable(16);
        var fighters = new MemoryFighterRepository(table);

        await fighters.UpsertAsync(new FighterRecord(1, 1, 0, 1, 0, ""), CancellationToken.None);
        await fighters.UpsertAsync(new FighterRecord(1, 0, 7, 10, 300007, "Dao"), CancellationToken.None);

        var stored = await fighters.ListByUserNumAsync(1, CancellationToken.None);
        Assert.Equal(2, stored.Count);
        Assert.Equal(0, stored[0].Slot);
        Assert.Equal(1, stored[1].Slot);

        var replaced = new FighterRecord(1, 0, 9, 11, 300009, "Soe");
        await fighters.UpsertAsync(replaced, CancellationToken.None);
        stored = await fighters.ListByUserNumAsync(1, CancellationToken.None);
        Assert.Equal(2, stored.Count);
        Assert.Equal(replaced, stored[0]);
    }


    [Fact]
    public async Task PurchaseFlowChargesWalletAndGrantsSlotThroughSharedTable()
    {
        var table = new MemoryPlayerTable(16);
        var money = new MemoryPlayerWalletRepository(table);
        var inventory = new MemoryInventoryRepository(table);

        var wallet = new PlayerWallet(1, 100000, 0);
        await money.UpsertAsync(wallet, CancellationToken.None);

        // A seeded starter row, as the lobby's first login stores one.
        var starter = new InventorySlotRecord(1, InventoryBag.CharacterBag, 11, 300001, 99999, 0);
        await inventory.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, [starter], CancellationToken.None);
        var bag = await inventory.ListSlotsAsync(1, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, CancellationToken.None);
        Assert.Single(bag);

        var stored = await money.FindByUserNumAsync(1, CancellationToken.None);
        Assert.NotNull(stored);
        var charged = stored! with { Lucci = stored.Lucci - 2500 };
        await money.UpsertAsync(charged, CancellationToken.None);

        var granted = new InventorySlotRecord(1, InventoryBag.CharacterBag, 0x40F86B0000000000, 444444, 99999, 0);
        await inventory.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, [.. bag, granted], CancellationToken.None);

        var after = await inventory.ListSlotsAsync(1, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, CancellationToken.None);
        Assert.Equal(2, after.Count);
        Assert.Equal(444444, after[^1].ItemId);
        var walletAfter = await money.FindByUserNumAsync(1, CancellationToken.None);
        Assert.Equal(97500, walletAfter!.Lucci);
    }

    [Fact]
    public async Task SetSlotWornFlipsOnlyTheTargetItemAndPreservesOtherBits()
    {
        var table = new MemoryPlayerTable(16);
        var inventory = new MemoryInventoryRepository(table);

        // Two rows share the item id (a repeated purchase) and a third row is a
        // different item; one of the pair already carries a second, unproven flag
        // bit that must survive the flip.
        var first = new InventorySlotRecord(1, InventoryBag.CharacterBag, 11, 200001, 99999, 0);
        var second = new InventorySlotRecord(1, InventoryBag.CharacterBag, 12, 200001, 99999, 0x02);
        var other = new InventorySlotRecord(1, InventoryBag.CharacterBag, 13, 201001, 99999, 0);
        var equippedBagRow = new InventorySlotRecord(1, InventoryBag.EquippedBag, 14, 200001, 99999, 0);
        await inventory.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, [first, second, other], CancellationToken.None);
        await inventory.ReplaceSlotsAsync(1, InventoryBag.EquippedBag, [equippedBagRow], CancellationToken.None);

        // Wear: every character-bag row with the id flips and the highest-ordinal
        // match comes back for the re-push.
        var worn = await inventory.SetSlotWornAsync(1, 200001, worn: true, CancellationToken.None);
        Assert.NotNull(worn);
        Assert.Equal(12, worn!.SlotKey);
        Assert.Equal(0x02 | InventorySlotRecord.WornFlagBit, worn.Flags);

        var stored = await inventory.ListSlotsAsync(1, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, CancellationToken.None);
        Assert.Equal(InventorySlotRecord.WornFlagBit, stored[0].Flags);
        Assert.Equal(0x02 | InventorySlotRecord.WornFlagBit, stored[1].Flags);
        Assert.Equal(0, stored[2].Flags);

        // Unequip clears the bit while keeping the other bit set.
        var cleared = await inventory.SetSlotWornAsync(1, 200001, worn: false, CancellationToken.None);
        Assert.Equal(0x02, cleared!.Flags);
        stored = await inventory.ListSlotsAsync(1, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, CancellationToken.None);
        Assert.Equal(0, stored[0].Flags);
        Assert.Equal(0x02, stored[1].Flags);

        // The equipped-bag store is a different bag and is never touched.
        var equipped = await inventory.ListSlotsAsync(1, InventoryBag.EquippedBag, InventoryLimits.MaxInventorySlotsPerBag, CancellationToken.None);
        Assert.Equal(0, equipped[0].Flags);
    }

    [Fact]
    public async Task SetSlotWornOfUnknownItemOrPlayerReturnsNullAndMutatesNothing()
    {
        var table = new MemoryPlayerTable(16);
        var inventory = new MemoryInventoryRepository(table);

        await inventory.ReplaceSlotsAsync(
            1, InventoryBag.CharacterBag,
            [new InventorySlotRecord(1, InventoryBag.CharacterBag, 11, 200001, 99999, 0)],
            CancellationToken.None);

        Assert.Null(await inventory.SetSlotWornAsync(1, 999999, worn: true, CancellationToken.None));
        Assert.Null(await inventory.SetSlotWornAsync(2, 200001, worn: true, CancellationToken.None));
        Assert.Null(await inventory.SetSlotWornAsync(7, 200001, worn: false, CancellationToken.None));

        var stored = await inventory.ListSlotsAsync(1, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, CancellationToken.None);
        var slot = Assert.Single(stored);
        Assert.Equal(0, slot.Flags);

        // Guards fail loud before any state is touched.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            inventory.SetSlotWornAsync(1, 0, worn: true, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            inventory.SetSlotWornAsync(0, 200001, worn: true, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            inventory.SetSlotWornAsync(1, -5, worn: true, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task ActiveWeaponPresetSlotDefaultsReadsZeroAndRoundTripsPerPlayer()
    {
        var table = new MemoryPlayerTable(16);
        var state = new MemoryPlayerInventoryState(table);

        // A player with nothing stored reads as slot 0, the slot a
        // login starts on, and the read does not track the player.
        Assert.Equal(0, await state.GetActiveWeaponPresetSlotAsync(7, CancellationToken.None));

        // A granted slot is stored and read back; a later grant replaces it.
        await state.SetActiveWeaponPresetSlotAsync(1, 3, CancellationToken.None);
        Assert.Equal(3, await state.GetActiveWeaponPresetSlotAsync(1, CancellationToken.None));
        await state.SetActiveWeaponPresetSlotAsync(1, 0, CancellationToken.None);
        Assert.Equal(0, await state.GetActiveWeaponPresetSlotAsync(1, CancellationToken.None));

        // Slots are isolated per player: granting for player 2 leaves player 1
        // on the slot it held.
        await state.SetActiveWeaponPresetSlotAsync(1, 2, CancellationToken.None);
        await state.SetActiveWeaponPresetSlotAsync(2, 4, CancellationToken.None);
        Assert.Equal(2, await state.GetActiveWeaponPresetSlotAsync(1, CancellationToken.None));
        Assert.Equal(4, await state.GetActiveWeaponPresetSlotAsync(2, CancellationToken.None));

        // Guards fail loud before any state is touched.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            state.SetActiveWeaponPresetSlotAsync(0, 1, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            state.SetActiveWeaponPresetSlotAsync(-1, 1, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            state.SetActiveWeaponPresetSlotAsync(1, InventoryLimits.MaxWeaponPresetSlot + 1, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            state.GetActiveWeaponPresetSlotAsync(0, CancellationToken.None).AsTask());
        Assert.Equal(2, await state.GetActiveWeaponPresetSlotAsync(1, CancellationToken.None));
    }

    [Fact]
    public async Task ActiveWeaponPresetSlotReadTracksNoPlayerButSetHonoursTheCap()
    {
        var table = new MemoryPlayerTable(1);
        var state = new MemoryPlayerInventoryState(table);

        // The default read of an unknown player does not grow the table, so
        // the single tracked slot stays free for the write that follows.
        Assert.Equal(0, await state.GetActiveWeaponPresetSlotAsync(2, CancellationToken.None));
        await state.SetActiveWeaponPresetSlotAsync(1, 1, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            state.SetActiveWeaponPresetSlotAsync(2, 1, CancellationToken.None).AsTask());
        Assert.Contains("max_players cap (1)", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, await state.GetActiveWeaponPresetSlotAsync(1, CancellationToken.None));
    }

    [Fact]
    public async Task CapRefusesNewPlayerButServesTrackedOnes()
    {
        var table = new MemoryPlayerTable(2);
        var money = new MemoryPlayerWalletRepository(table);

        await money.UpsertAsync(new PlayerWallet(1, 1, 0), CancellationToken.None);
        await money.UpsertAsync(new PlayerWallet(2, 2, 0), CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            money.UpsertAsync(new PlayerWallet(3, 3, 0), CancellationToken.None).AsTask());
        Assert.Contains("max_players cap (2)", ex.Message, StringComparison.Ordinal);

        // Tracked players keep working, and reads of untracked players stay null.
        Assert.Equal(1, (await money.FindByUserNumAsync(1, CancellationToken.None))!.Lucci);
        Assert.Equal(2, (await money.FindByUserNumAsync(2, CancellationToken.None))!.Lucci);
        Assert.Null(await money.FindByUserNumAsync(3, CancellationToken.None));
    }
}
