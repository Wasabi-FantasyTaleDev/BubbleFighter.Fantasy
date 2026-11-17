using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Contracts.Wallet;

namespace BubbleFighter.Fantasy.Contracts.CashInbox;

/// <summary>Lifecycle state of a stored cash inbox entry.</summary>
public enum CashInboxEntryState
{
    /// <summary>Waiting in the player's cash inbox to be picked up into the regular inventory.</summary>
    Active = 0,

    /// <summary>Picked up into the player's inventory in a committed transaction.</summary>
    PickedUp = 1,

    /// <summary>Expired past its deadline and excluded from active inbox listings and pickups.</summary>
    Expired = 2,
}

/// <summary>One persisted cash inbox slot row belonging to a player and client build.</summary>
/// <param name="CashSlotId">Monotonic store-allocated 32-bit slot key, never reused after pickup or expiry.</param>
/// <param name="ProfileId">Client build whose shop workbook <paramref name="StockKey"/> belongs to.</param>
/// <param name="UserNum">Owning player <c>players.user_num</c>.</param>
/// <param name="StockKey">Catalog <c>Stock</c> or <c>StockSet</c> key carried on the wire for dialog lookup and claim resolution.</param>
/// <param name="ItemKey">Client display key: individual <c>ItemId</c> for single-item purchases, or <c>StockSetId</c> for packages.</param>
/// <param name="Quantity">Charge/item count shown when duration days is zero.</param>
/// <param name="Days">Duration days shown on single timed/permanent items (32767 for permanent; 0 for packages or count-based items).</param>
/// <param name="Note">Optional short label carried in the slot's string field.</param>
/// <param name="CreatedAtUnixMilliseconds">UTC timestamp when the inbox slot was stored.</param>
/// <param name="ExpiresAtUnixMilliseconds">UTC expiry deadline, or zero when entries do not expire.</param>
/// <param name="State">Current lifecycle state of the entry.</param>
public sealed record CashInboxEntryRecord(
    long CashSlotId,
    string ProfileId,
    long UserNum,
    int StockKey,
    int ItemKey,
    int Quantity,
    int Days,
    string Note,
    long CreatedAtUnixMilliseconds,
    long ExpiresAtUnixMilliseconds,
    CashInboxEntryState State)
{
    /// <summary>Gets the secondary 32-bit wire slot identifier field (zero by default).</summary>
    public int SecondaryId { get; init; }

    /// <summary>Returns whether this entry is still active at <paramref name="nowUnixMilliseconds"/>.</summary>
    /// <param name="nowUnixMilliseconds">Current UTC time in milliseconds.</param>
    public bool IsActiveAt(long nowUnixMilliseconds) =>
        State == CashInboxEntryState.Active
        && (ExpiresAtUnixMilliseconds == 0 || ExpiresAtUnixMilliseconds > nowUnixMilliseconds);
}

/// <summary>Outcome of attempting to add a new item into a player's cash inbox.</summary>
public enum CashInboxGrantOutcome
{
    /// <summary>A new active entry was created with a newly allocated monotonic slot identifier.</summary>
    Granted = 0,

    /// <summary>The supplied idempotent claim key was already recorded earlier; no duplicate entry was added.</summary>
    AlreadyClaimed = 1,

    /// <summary>The player's active cash inbox entries already reached the configured ceiling.</summary>
    BoxFull = 2,

    /// <summary>The target player record does not exist.</summary>
    OwnerMissing = 3,
}

/// <summary>Validated request to enqueue one catalog item or bundle into a player's cash inbox.</summary>
/// <param name="ProfileId">Client build whose shop workbook <paramref name="StockKey"/> belongs to.</param>
/// <param name="UserNum">Receiving player <c>players.user_num</c>.</param>
/// <param name="StockKey">Positive catalog <c>Stock</c> or <c>StockSet</c> key.</param>
/// <param name="ItemKey">Positive client display key (<c>ItemId</c> or <c>StockSetId</c>).</param>
/// <param name="Quantity">Nonnegative wire count field.</param>
/// <param name="Days">Nonnegative wire duration field.</param>
/// <param name="Note">Optional note text shown alongside the slot.</param>
/// <param name="CreatedAtUnixMilliseconds">UTC timestamp when this entry is granted.</param>
/// <param name="ExpiresAtUnixMilliseconds">UTC expiry deadline, or zero when entries do not expire.</param>
/// <param name="MaxEntriesPerPlayer">Maximum concurrent active entries allowed per player.</param>
public sealed record CashInboxGrantCommand(
    string ProfileId,
    long UserNum,
    int StockKey,
    int ItemKey,
    int Quantity,
    int Days,
    string Note,
    long CreatedAtUnixMilliseconds,
    long ExpiresAtUnixMilliseconds,
    int MaxEntriesPerPlayer)
{
    /// <summary>Gets an optional per-player idempotency key; when set, duplicate claims return the original result once.</summary>
    public string? ClaimKey { get; init; }

    /// <summary>Gets the optional secondary 32-bit slot identifier stored on the entry.</summary>
    public int SecondaryId { get; init; }

    /// <summary>Gets whether to bypass the active-entry count cap for this grant.</summary>
    public bool IgnoreInboxLimit { get; init; }

    /// <summary>Validates bounds and identifiers before touching persistent storage.</summary>
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ProfileId);
        ArgumentNullException.ThrowIfNull(Note);
        if (ProfileId.Length > 32 || UserNum <= 0 || StockKey <= 0 || ItemKey <= 0
            || Quantity is < 0 or > ushort.MaxValue || Days is < 0 or > ushort.MaxValue
            || CreatedAtUnixMilliseconds < 0 || ExpiresAtUnixMilliseconds < 0
            || (ExpiresAtUnixMilliseconds > 0 && ExpiresAtUnixMilliseconds <= CreatedAtUnixMilliseconds)
            || MaxEntriesPerPlayer is < 1 or > CashInboxLimits.MaxEntriesPerPlayerCap
            || Note.Length > CashInboxLimits.MaxNoteChars)
            throw new ArgumentException("Cash inbox grant parameters are invalid or exceed configured bounds.");

        if (ClaimKey is not null && (string.IsNullOrWhiteSpace(ClaimKey) || ClaimKey.Length > CashInboxLimits.MaxClaimKeyChars))
            throw new ArgumentException("Cash inbox claim key must be non-empty and within length bounds.");
    }
}

/// <summary>Result of granting an item into the cash inbox.</summary>
/// <param name="Outcome">Whether the entry was created or why it was declined.</param>
/// <param name="Entry">The newly stored entry when <paramref name="Outcome"/> is <see cref="CashInboxGrantOutcome.Granted"/>, otherwise null.</param>
public sealed record CashInboxGrantResult(CashInboxGrantOutcome Outcome, CashInboxEntryRecord? Entry);

/// <summary>Storage-level outcome of picking up a cash inbox entry into the regular bag.</summary>
public enum CashInboxPickupOutcome
{
    /// <summary>The inbox entry was marked picked up and all grants were committed in the same transaction.</summary>
    PickedUp = 0,

    /// <summary>No active, unexpired entry exists for the requested slot identifier in this build.</summary>
    NotFound = 1,

    /// <summary>The requested slot identifier belongs to a different player account.</summary>
    OwnerMismatch = 2,

    /// <summary>One or more conditional grants conflict with the player's current inventory state or limits.</summary>
    AlreadyOwned = 3,

    /// <summary>The pickup could not be completed due to an unexpected state refusal.</summary>
    Failed = 4,
}

/// <summary>Validated request to atomically pick up a cash inbox entry into the player's regular bag.</summary>
/// <param name="ProfileId">Client build whose shop workbook was used to resolve <paramref name="Grants"/>.</param>
/// <param name="UserNum">Calling player attempting the pickup.</param>
/// <param name="CashSlotId">Requested wire slot key.</param>
/// <param name="SeedWallet">Starting wallet values if the player has no stored wallet yet.</param>
/// <param name="SlotKeyBase">Lower bound for allocating new inventory slot keys.</param>
/// <param name="Grants">Resolved regular-inventory grants matching the inbox entry's stock.</param>
/// <param name="PickedUpAtUnixMilliseconds">Current UTC timestamp for expiry and pickup recording.</param>
public sealed record CashInboxPickupCommand(
    string ProfileId,
    long UserNum,
    long CashSlotId,
    PlayerWallet SeedWallet,
    long SlotKeyBase,
    IReadOnlyList<ShopPurchaseGrant> Grants,
    long PickedUpAtUnixMilliseconds)
{
    /// <summary>Gets any bonus Lucci credited alongside the picked-up stock (nonnegative).</summary>
    public int LucciReward { get; init; }

    /// <summary>Validates pickup arguments and grant shapes before starting a transaction.</summary>
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ProfileId);
        ArgumentNullException.ThrowIfNull(SeedWallet);
        ArgumentNullException.ThrowIfNull(Grants);
        if (ProfileId.Length > 32 || UserNum <= 0 || SeedWallet.UserNum != UserNum
            || CashSlotId is <= 0 or > CashInboxLimits.MaxCashSlotId || SlotKeyBase <= 0
            || PickedUpAtUnixMilliseconds < 0 || SeedWallet.Lucci < 0 || SeedWallet.Cash < 0
            || LucciReward is < 0 or > 1_000_000
            || (Grants.Count == 0 && LucciReward == 0)
            || Grants.Count > InventoryLimits.MaxInventorySlotsPerBag
            || Grants.Any(g => g.ItemId <= 0))
            throw new ArgumentException("Cash inbox pickup parameters are invalid.");

        foreach (var grant in Grants)
        {
            if (!Enum.IsDefined(grant.Mode))
                throw new ArgumentException("The inventory grant mode is not supported.");
            if (grant.ExpectedSlot is { } expected && (grant.Mode != ShopGrantMode.MatchItem
                || expected.UserNum != UserNum || expected.Bag != InventoryBag.CharacterBag
                || expected.ItemId != grant.ItemId || expected.SlotKey <= 0))
                throw new ArgumentException("A conditional grant must expect a character-bag slot of the same player and item.");
            if (grant.Mode == ShopGrantMode.MatchItem && Grants.Count(g => g.ItemId == grant.ItemId) != 1)
                throw new ArgumentException("A conditional pickup may change each item only once.");
        }
    }

    /// <summary>Returns whether every conditional <see cref="ShopGrantMode.MatchItem"/> grant matches the locked character bag.</summary>
    /// <param name="slots">Current character bag rows read under the store's lock.</param>
    public bool MatchesInventorySnapshot(IReadOnlyList<InventorySlotRecord> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        foreach (var grant in Grants.Where(g => g.Mode == ShopGrantMode.MatchItem))
        {
            var matches = slots.Where(s => s.ItemId == grant.ItemId).Take(2).ToArray();
            if (grant.ExpectedSlot is null ? matches.Length != 0
                : matches.Length != 1 || matches[0] != grant.ExpectedSlot)
                return false;
        }
        return true;
    }

    /// <summary>Plans atomic character-bag additions and renewals under the store's player lock without mutating inputs.</summary>
    /// <param name="currentBag">Current ordered character-bag rows under the lock.</param>
    /// <param name="lastPurchaseSlotKey">Persisted slot-key watermark, including removed items.</param>
    public CashInboxBagGrantPlan PlanCharacterBagGrants(IReadOnlyList<InventorySlotRecord> currentBag, long lastPurchaseSlotKey)
    {
        ArgumentNullException.ThrowIfNull(currentBag);
        if (lastPurchaseSlotKey < 0)
            throw new ArgumentOutOfRangeException(nameof(lastPurchaseSlotKey));

        if (!MatchesInventorySnapshot(currentBag))
            return new(CashInboxPickupOutcome.AlreadyOwned, currentBag, [], [], [], lastPurchaseSlotKey);

        var appendCount = Grants.Count(g => g.ExpectedSlot is null);
        if (currentBag.Count + appendCount > InventoryLimits.MaxInventorySlotsPerBag)
            return new(CashInboxPickupOutcome.Failed, currentBag, [], [], [], lastPurchaseSlotKey);

        var nextBag = currentBag.ToList();
        var highest = Math.Max(lastPurchaseSlotKey, nextBag.Count == 0 ? 0 : nextBag.Max(s => s.SlotKey));
        var applied = new List<InventorySlotRecord>(Grants.Count);
        var added = new List<InventorySlotRecord>(appendCount);
        var updated = new List<InventorySlotRecord>();

        foreach (var grant in Grants)
        {
            if (grant.ExpectedSlot is { } expected)
            {
                var index = nextBag.FindIndex(s => s == expected);
                if (index < 0)
                    return new(CashInboxPickupOutcome.AlreadyOwned, currentBag, [], [], [], lastPurchaseSlotKey);

                var renewed = nextBag[index] with { Quantity = grant.Quantity, Lifetime = grant.Lifetime };
                nextBag[index] = renewed;
                applied.Add(renewed);
                updated.Add(renewed);
                continue;
            }

            highest = Math.Max(checked(highest + 1), SlotKeyBase);
            var created = new InventorySlotRecord(UserNum, InventoryBag.CharacterBag, highest, grant.ItemId, grant.Quantity, 0)
            {
                Lifetime = grant.Lifetime,
            };
            nextBag.Add(created);
            applied.Add(created);
            added.Add(created);
        }

        return new(CashInboxPickupOutcome.PickedUp, nextBag, applied, added, updated, highest);
    }

    /// <summary>Computes the updated wallet snapshot when applying <see cref="LucciReward"/>, or null if the balance would overflow.</summary>
    /// <param name="existing">Stored wallet before pickup, or null if the player has no wallet row yet.</param>
    public PlayerWallet? ResolveWalletAfter(PlayerWallet? existing)
    {
        var baseWallet = existing ?? SeedWallet;
        if (baseWallet.UserNum != UserNum || baseWallet.Lucci < 0 || baseWallet.Cash < 0)
            return null;

        var totalLucci = (long)baseWallet.Lucci + LucciReward;
        if (totalLucci > int.MaxValue)
            return null;

        return baseWallet with { Lucci = (int)totalLucci };
    }
}

/// <summary>Planned character-bag additions and renewals computed from a locked character bag snapshot.</summary>
/// <param name="Outcome">Whether grants can apply, or the refusal outcome if preconditions failed.</param>
/// <param name="BagAfter">Full resulting character bag preserving existing slot positions.</param>
/// <param name="AppliedSlots">Resulting slot records corresponding 1-to-1 with <see cref="CashInboxPickupCommand.Grants"/>.</param>
/// <param name="AddedSlots">Newly appended slots that require inserts and slot-key advancement.</param>
/// <param name="UpdatedSlots">Existing slots whose quantity or lifetime was renewed in place.</param>
/// <param name="HighestSlotKey">Updated slot-key watermark to persist atomically.</param>
public sealed record CashInboxBagGrantPlan(
    CashInboxPickupOutcome Outcome,
    IReadOnlyList<InventorySlotRecord> BagAfter,
    IReadOnlyList<InventorySlotRecord> AppliedSlots,
    IReadOnlyList<InventorySlotRecord> AddedSlots,
    IReadOnlyList<InventorySlotRecord> UpdatedSlots,
    long HighestSlotKey);

/// <summary>Outcome of an atomic cash inbox pickup transaction.</summary>
/// <param name="Outcome">How the transaction concluded.</param>
/// <param name="Entry">The updated entry on success, or the stored row when inspected.</param>
/// <param name="PurchaseResult">Committed wallet and bag grants when <paramref name="Outcome"/> is <see cref="CashInboxPickupOutcome.PickedUp"/>, otherwise null.</param>
public sealed record CashInboxPickupResult(
    CashInboxPickupOutcome Outcome,
    CashInboxEntryRecord? Entry,
    ShopPurchaseResult? PurchaseResult);
