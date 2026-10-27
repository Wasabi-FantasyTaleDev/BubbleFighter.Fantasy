using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Wallet;

namespace BubbleFighter.Fantasy.Lobby.Data;

/// <summary>
/// One player's resolved login inventory snapshot, ready for the S2C 802 /
/// 803 builders. S2C 803 containers 2 and 3 and S2C 810 are sent empty.
/// </summary>
/// <param name="CharacterBag">S2C 802 rows (bag 0).</param>
/// <param name="EquippedBag">S2C 803 container-1 rows (bag 1).</param>
public sealed record InventorySnapshot(
    IReadOnlyList<InventorySlotRecord> CharacterBag,
    IReadOnlyList<InventorySlotRecord> EquippedBag);

/// <summary>
/// Loads one player's inventory from the store for the login burst. A player with no stored rows at
/// all is seeded with the configured starter bag, and a player whose stored rows predate the current starter
/// set is topped up additively: every starter key the stored bag does not carry is appended, and nothing the
/// player already owns is removed or rewritten. Seeding only fills the two bags; container 2, container 3 and
/// the cash inventory stay empty (<c>sub_933190</c> treats container 3 as a removal list, so it must never
/// carry ids on a login push). </summary> <remarks> The additive top-up exists because the shop UI is built
/// from the bag: a player whose rows were stored before the starter set grew would otherwise keep a short bag
/// forever and render an empty shop. The top-up is idempotent, so a player who already owns every starter key
/// triggers no write, and a purchased item is never duplicated or reordered. </remarks>
public sealed class LoginInventoryResolver(IInventoryRepository inventory, bool seedStarterInventory,
    IEquipmentSelection? selection = null, StarterKit? starter = null, TimeProvider? clock = null)
{
    private readonly StarterKit _starter = starter ?? StarterKit.Default;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    // Recording creation intentionally starts a fresh bag, including the chosen character's worn flag.
    internal async ValueTask ResetStarterAsync(long userNum, StarterKit kit, CancellationToken ct)
    {
        await inventory.ReplaceSlotsAsync(userNum, InventoryBag.CharacterBag,
            kit.Slots(userNum, InventoryBag.CharacterBag, _clock.GetUtcNow()), ct).ConfigureAwait(false);
        await inventory.ReplaceSlotsAsync(userNum, InventoryBag.EquippedBag,
            kit.Slots(userNum, InventoryBag.EquippedBag, _clock.GetUtcNow()), ct).ConfigureAwait(false);
    }

    /// <summary>Resolves the snapshot for one player, seeding on first login.</summary>
    /// <param name="userNum">Login <c>players.user_num</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    public async ValueTask<InventorySnapshot> ResolveAsync(long userNum, CancellationToken ct)
    {
        var snapshot = await ResolveStoredAsync(userNum, ct).ConfigureAwait(false);
        if (selection is null) return snapshot;
        var canonical = await selection.ReadAsync(userNum, ct).ConfigureAwait(false);
        // S2C 803 updates the same client map as 802; never replay a stale independent worn snapshot.
        return snapshot with { CharacterBag = canonical, EquippedBag = canonical };
    }

    // Login removes expired rows before filling missing starter items, and normalizes only after both bags exist.
    internal async ValueTask PrepareAsync(long userNum, CancellationToken ct) =>
        _ = await ResolveStoredAsync(userNum, ct).ConfigureAwait(false);

    private async ValueTask<InventorySnapshot> ResolveStoredAsync(long userNum, CancellationToken ct)
    {
        var characterBag = await inventory.ListSlotsAsync(
            userNum, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, ct).ConfigureAwait(false);
        var equippedBag = await inventory.ListSlotsAsync(
            userNum, InventoryBag.EquippedBag, InventoryLimits.MaxInventorySlotsPerBag, ct).ConfigureAwait(false);

        var empty = characterBag.Count == 0 && equippedBag.Count == 0;
        if (!seedStarterInventory)
            return new InventorySnapshot(characterBag, equippedBag);

        // Seed on first login, then top up on every later one. A player created
        // before the starter bag grew keeps the smaller stored bag forever under
        // an empty-only check, which is why a shop built from the bag stayed
        // blank for an existing character. The top-up is additive: any starter
        // item the player already owns is left exactly as stored, and nothing is
        // ever removed, so a player who has since bought or sold keeps their own
        // bag. Only a bag that is missing a starter id is rewritten.
        if (empty)
        {
            var seededBag = _starter.Slots(userNum, InventoryBag.CharacterBag, _clock.GetUtcNow());
            var seededEquipped = _starter.Slots(userNum, InventoryBag.EquippedBag, _clock.GetUtcNow());
            await inventory.ReplaceSlotsAsync(userNum, InventoryBag.CharacterBag, seededBag, ct).ConfigureAwait(false);
            await inventory.ReplaceSlotsAsync(userNum, InventoryBag.EquippedBag, seededEquipped, ct).ConfigureAwait(false);
            return new InventorySnapshot(seededBag, seededEquipped);
        }

        var toppedBag = TopUpStarter(characterBag, userNum, InventoryBag.CharacterBag);
        var toppedEquipped = TopUpStarter(equippedBag, userNum, InventoryBag.EquippedBag);
        if (toppedBag is null && toppedEquipped is null)
            return new InventorySnapshot(characterBag, equippedBag);

        // Append only the missing starter rows in one store operation: a whole-bag replace built
        // from the read above would erase anything bought between that read and this write.
        if (toppedBag is not null)
            characterBag = await inventory.AppendMissingItemsAsync(
                userNum, InventoryBag.CharacterBag, toppedBag.Skip(characterBag.Count).ToList(), ct).ConfigureAwait(false);
        if (toppedEquipped is not null)
            equippedBag = await inventory.AppendMissingItemsAsync(
                userNum, InventoryBag.EquippedBag, toppedEquipped.Skip(equippedBag.Count).ToList(), ct).ConfigureAwait(false);
        return new InventorySnapshot(characterBag, equippedBag);
    }

    /// <summary>
    /// Returns the stored bag with any missing starter item appended, or
    /// <see langword="null"/> when the bag already carries every starter item.
    /// Existing rows are never modified or dropped, so the result is the stored
    /// order followed by the additions in starter order.
    /// </summary>
    /// <param name="stored">The bag as stored.</param>
    /// <param name="userNum">Login <c>players.user_num</c>.</param>
    /// <param name="bag">Which bag the rows belong to.</param>
    /// <returns>The topped-up bag, or <see langword="null"/> when nothing is missing.</returns>
    private List<InventorySlotRecord>? TopUpStarter(
        IReadOnlyList<InventorySlotRecord> stored, long userNum, InventoryBag bag)
    {
        var owned = new HashSet<int>();
        foreach (var row in stored)
            owned.Add(row.ItemId);

        var missing = _starter.Slots(userNum, bag, _clock.GetUtcNow())
            .Where(row => !owned.Contains(row.ItemId))
            .Select(row => row with { Flags = 0 })
            .ToList();
        if (missing.Count == 0)
            return null;

        var topped = new List<InventorySlotRecord>(stored.Count + missing.Count);
        topped.AddRange(stored);
        topped.AddRange(missing);
        if (topped.Count > InventoryLimits.MaxInventorySlotsPerBag)
        {
            throw new InvalidOperationException(
                $"Starter top-up for user {userNum} bag {bag} would need {topped.Count} slots, above the "
                + $"{InventoryLimits.MaxInventorySlotsPerBag}-slot cap ({stored.Count} stored + {missing.Count} missing); "
                + "the bag is full, so the starter set cannot be granted.");
        }

        return topped;
    }
}

/// <summary>
/// Loads one player's money snapshot for S2C 106, seeding on first login. The
/// seed is the deployment's starting balances (both zero by default, which
/// keeps the login burst all-zero). Currency
/// meanings are unverified, so the field order follows the S2C 106 wire order:
/// field 5 (lucci wallet, client <c>dword_18E56E8+0x4C</c>) and field 6 (cash
/// wallet, <c>+0x50</c>).
/// </summary>
public sealed class LoginMoneyResolver(IPlayerWalletRepository money, int startingLucci, int startingCash)
{
    /// <summary>Resolves the snapshot for one player, seeding on first login.</summary>
    /// <param name="userNum">Login <c>players.user_num</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    public async ValueTask<PlayerWallet> ResolveAsync(long userNum, CancellationToken ct)
    {
        var stored = await money.FindByUserNumAsync(userNum, ct).ConfigureAwait(false);
        if (stored is not null)
            return stored;
        var seeded = new PlayerWallet(userNum, startingLucci, startingCash);
        await money.UpsertAsync(seeded, ct).ConfigureAwait(false);
        return seeded;
    }
}
