using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Shop;

namespace BubbleFighter.Fantasy.Contracts.MysteryStore;

/// <summary>A provisional board and inventory replacement; a provider must commit all its effects together.</summary>
public sealed class MysteryStorePlan
{
    internal MysteryStorePlan(MysteryStoreOutcome outcome, MysteryStoreSelection? selection, DateTimeOffset plannedAt,
        IReadOnlyList<InventorySlotRecord>? rows = null, long lastSlotKey = 0, IReadOnlyList<int>? grantItemIds = null,
        int receiptNativeKey = 0, long? admissionExpiresAt = null,
        ShopPurchaseRefusal grantRefusal = ShopPurchaseRefusal.Unspecified, InventorySnapshot? replayInventory = null)
    {
        Outcome = outcome;
        Selection = selection;
        PlannedAt = plannedAt;
        Rows = rows is null ? null : Array.AsReadOnly(rows.ToArray());
        LastSlotKey = lastSlotKey;
        GrantItemIds = Array.AsReadOnly((grantItemIds ?? []).ToArray());
        ReceiptNativeKey = receiptNativeKey;
        AdmissionExpiresAtUnixMilliseconds = admissionExpiresAt;
        GrantRefusal = grantRefusal;
        ReplayInventory = replayInventory;
    }

    /// <summary>Gets the planned domain result, which is not a committed result yet.</summary>
    public MysteryStoreOutcome Outcome { get; }
    /// <summary>Gets the authoritative current or proposed replacement selection.</summary>
    public MysteryStoreSelection? Selection { get; }
    /// <summary>Gets whether a provider transaction must persist the proposed state.</summary>
    public bool RequiresCommit => Outcome is MysteryStoreOutcome.SelectionCreated or MysteryStoreOutcome.Refreshed or MysteryStoreOutcome.Purchased;
    /// <summary>Gets complete staged raw bags, or null when the plan does not mutate inventory.</summary>
    public IReadOnlyList<InventorySlotRecord>? Rows { get; }
    /// <summary>Gets the monotonic slot watermark to commit alongside staged rows.</summary>
    public long LastSlotKey { get; }
    /// <summary>Gets distinct rewarded item identities to select after metadata preparation.</summary>
    public IReadOnlyList<int> GrantItemIds { get; }
    /// <summary>Gets the exact native Stock/StockSet receipt key, never an equipment item id.</summary>
    public int ReceiptNativeKey { get; }
    /// <summary>Gets the old selection deadline that must still admit this purchase or paid refresh after allocator waits.</summary>
    public long? AdmissionExpiresAtUnixMilliseconds { get; }
    /// <summary>Gets the provider-supplied instant at which this immutable plan was formed.</summary>
    public DateTimeOffset PlannedAt { get; }
    /// <summary>Gets the shared inventory planner's refusal classification when grant planning failed.</summary>
    public ShopPurchaseRefusal GrantRefusal { get; }
    /// <summary>Gets the mandatory current locked snapshot for a purchase replay; it contains no newly minted grants.</summary>
    public InventorySnapshot? ReplayInventory { get; }

    /// <summary>Rechecks expiry after an external allocator wait; no provider may commit an expired paid operation.</summary>
    /// <param name="now">Fresh provider time immediately before persistence.</param>
    /// <returns>Whether the original board still admits the planned mutation.</returns>
    public bool AdmissionStillValid(DateTimeOffset now) => AdmissionExpiresAtUnixMilliseconds is not { } expiry
        || Math.Max(now.ToUnixTimeMilliseconds(), PlannedAt.ToUnixTimeMilliseconds()) < expiry;
}

/// <summary>Stages mystery-store effects from locked provider state without writes, clock reads or identity allocation.</summary>
public static class MysteryStorePlanner
{
    /// <summary>Stages a free board for missing/expired state or a deliberate server catalog revision change.</summary>
    /// <param name="command">Trusted owner and complete candidate from one effective revision.</param>
    /// <param name="current">Current selection read under the provider's account lock.</param>
    /// <param name="replacementToken">Next durable monotonic token, unused when no replacement is admitted.</param>
    /// <param name="now">Fresh provider time after acquiring the account lock.</param>
    /// <returns>An immutable provisional result; no input or durable state is changed.</returns>
    public static MysteryStorePlan Ensure(MysteryStoreEnsureCommand command, MysteryStoreSelection? current,
        uint replacementToken, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(command.UserNum);
        ArgumentNullException.ThrowIfNull(command.Scope);
        ArgumentNullException.ThrowIfNull(command.Candidate);
        command.Candidate.Validate(command.UserNum, command.Scope);
        ArgumentOutOfRangeException.ThrowIfZero(command.DurationSeconds);
        current?.Validate(command.UserNum, command.Scope);
        if (current is not null && now.ToUnixTimeMilliseconds() < current.ExpiresAtUnixMilliseconds
            && current.Board.CatalogRevision == command.Candidate.CatalogRevision)
        {
            current.Board.Validate(command.UserNum, command.Scope);
            return new(MysteryStoreOutcome.Unchanged, current, now);
        }
        if (current?.Token == uint.MaxValue)
            return new(MysteryStoreOutcome.TokenExhausted, current, now);
        return new(MysteryStoreOutcome.SelectionCreated,
            Replace(command.UserNum, command.Scope, current, command.Candidate, command.DurationSeconds, replacementToken, now), now);
    }

    /// <summary>Stages the stored refresh cost and a complete next board in one indivisible mutation.</summary>
    /// <param name="command">Trusted context and echoed token/quote.</param>
    /// <param name="current">Current locked selection.</param>
    /// <param name="rows">Complete original raw inventory under the account lock.</param>
    /// <param name="loadout">Current build selection, retained independently of raw worn flags.</param>
    /// <param name="lastSlotKey">Durable allocator watermark, including removed ownership.</param>
    /// <param name="replacementToken">Next durable monotonic token, unused on refusal.</param>
    /// <param name="now">Fresh provider time after account and allocator waits.</param>
    /// <returns>A staged atomic debit/replacement or a refusal without changed rows.</returns>
    public static MysteryStorePlan Refresh(MysteryStoreRefreshCommand command, MysteryStoreSelection? current,
        IReadOnlyList<InventorySlotRecord> rows, InventoryLoadoutState loadout, long lastSlotKey,
        uint replacementToken, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Inventory);
        ArgumentNullException.ThrowIfNull(command.Candidate);
        command.Inventory.Validate();
        command.Candidate.Validate(command.Inventory.UserNum, command.Inventory.Scope);
        ArgumentOutOfRangeException.ThrowIfZero(command.DurationSeconds);
        if (Check(command.Inventory, current, command.ExpectedToken, command.Candidate.CatalogRevision, now) is { } refusal)
            return new(refusal, current, now);
        if (command.QuotedCoinCost != current!.Board.RefreshCoinCost)
            return new(MysteryStoreOutcome.QuoteMismatch, current, now);
        if (current.Token == uint.MaxValue)
            return new(MysteryStoreOutcome.TokenExhausted, current, now);
        var staged = Debit(command.Inventory.UserNum, command.Inventory.Scope, rows, loadout,
            current.Board.CoinItemId, current.Board.RefreshCoinCost, now);
        if (staged is null) return new(MysteryStoreOutcome.InsufficientCoins, current, now);
        return new(MysteryStoreOutcome.Refreshed,
            Replace(command.Inventory.UserNum, command.Inventory.Scope, current, command.Candidate,
                command.DurationSeconds, replacementToken, now), now,
            staged, Highest(rows, lastSlotKey), admissionExpiresAt: current.ExpiresAtUnixMilliseconds);
    }

    /// <summary>Stages stored-price coin consumption, every stock member and one purchased bit from the same locked snapshot.</summary>
    /// <param name="command">Trusted context and echoed offer request.</param>
    /// <param name="current">Current locked selection.</param>
    /// <param name="rows">Complete original raw inventory; never pass rows from a prior provisional plan.</param>
    /// <param name="loadout">Current build selection used for grant ownership checks.</param>
    /// <param name="lastSlotKey">Durable historical slot watermark.</param>
    /// <param name="now">Fresh provider time after account and allocator waits.</param>
    /// <returns>All staged grants and the bought bit, a current-inventory replay, or a refusal.</returns>
    public static MysteryStorePlan Purchase(MysteryStorePurchaseCommand command, MysteryStoreSelection? current,
        IReadOnlyList<InventorySlotRecord> rows, InventoryLoadoutState loadout, long lastSlotKey, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Inventory);
        command.Inventory.Validate();
        if (Check(command.Inventory, current, command.ExpectedToken, command.CatalogRevision, now) is { } refusal)
            return new(refusal, current, now);
        var index = -1;
        for (var candidate = 0; candidate < current!.Board.Offers.Count; candidate++)
            if (current.Board.Offers[candidate].NativeKey == command.NativeKey) { index = candidate; break; }
        if (index < 0) return new(MysteryStoreOutcome.UnknownOffer, current, now);
        var offer = current.Board.Offers[index];
        if (command.QuotedCoinPrice != offer.CoinPrice)
            return new(MysteryStoreOutcome.QuoteMismatch, current, now);
        var bit = 1u << index;
        if ((current.PurchasedMask & bit) != 0)
        {
            InventoryArticles.ValidateRows(command.Inventory.UserNum, rows);
            _ = loadout.Copy();
            return new(MysteryStoreOutcome.AlreadyPurchased, current, now, receiptNativeKey: offer.NativeKey,
                replayInventory: InventorySnapshot.Project(rows, loadout, command.Inventory.Scope));
        }

        var user = command.Inventory.UserNum;
        var scope = command.Inventory.Scope;
        var staged = Debit(user, scope, rows, loadout, current.Board.CoinItemId, offer.CoinPrice, now);
        if (staged is null) return new(MysteryStoreOutcome.InsufficientCoins, current, now);
        var projected = InventorySnapshot.Project(staged, loadout, scope);
        IReadOnlyList<ShopPurchaseGrant> grants;
        try { grants = InventoryGrantPlanner.Plan(user, offer.Grants, projected.CharacterBag, now); }
        catch (InventoryGrantRejectedException error)
        {
            return new(MysteryStoreOutcome.GrantRefused, current, now, grantRefusal: error.Refusal);
        }

        var highest = Highest(rows, lastSlotKey);
        foreach (var grant in grants)
        {
            var owned = staged.Where(row => row.Bag == InventoryBag.CharacterBag && row.ItemId == grant.ItemId).ToArray();
            if (owned.Length > 1 || (grant.ExpectedSlot is null) != (owned.Length == 0))
                throw new InvalidDataException("Grant ownership differs from the locked projected inventory.");
            if (owned.Length == 0)
            {
                if (staged.Any(row => row.ItemId == grant.ItemId))
                    throw new InvalidDataException("A reward has an equipped mirror without its ownership row.");
                staged.Add(new(user, InventoryBag.CharacterBag, checked(++highest), grant.ItemId, grant.Quantity, 0)
                    { Lifetime = grant.Lifetime });
            }
            else
            {
                InventoryArticles.RequireMirrors(owned[0], staged.Where(row => row.Bag == InventoryBag.EquippedBag
                    && row.ItemId == grant.ItemId).ToArray());
                for (var row = 0; row < staged.Count; row++)
                    if (staged[row].ItemId == grant.ItemId)
                        staged[row] = staged[row] with { Quantity = grant.Quantity, Lifetime = grant.Lifetime };
            }
        }
        if (staged.GroupBy(row => row.Bag).Any(bag => bag.Count() > InventoryLimits.MaxInventorySlotsPerBag))
            return new(MysteryStoreOutcome.GrantRefused, current, now);
        InventoryArticles.ValidateRows(user, staged);
        return new(MysteryStoreOutcome.Purchased, current with { PurchasedMask = current.PurchasedMask | bit }, now,
            staged, highest, grants.Select(grant => grant.ItemId).ToArray(), offer.NativeKey,
            current.ExpiresAtUnixMilliseconds);
    }

    private static MysteryStoreOutcome? Check(InventoryMetadataPreparationCommand inventory, MysteryStoreSelection? current,
        uint token, string revision, DateTimeOffset now)
    {
        if (current is null) return MysteryStoreOutcome.MissingSelection;
        current.Validate(inventory.UserNum, inventory.Scope);
        if (current.Token != token) return MysteryStoreOutcome.StaleToken;
        if (now.ToUnixTimeMilliseconds() >= current.ExpiresAtUnixMilliseconds) return MysteryStoreOutcome.Expired;
        if (current.Board.CatalogRevision != revision) return MysteryStoreOutcome.CatalogChanged;
        current.Board.Validate(inventory.UserNum, inventory.Scope);
        return null;
    }

    private static MysteryStoreSelection Replace(long user, InventoryLoadoutScope scope, MysteryStoreSelection? current,
        MysteryStoreBoard candidate, ushort durationSeconds, uint token, DateTimeOffset now)
    {
        if (token == 0 || current is not null && token <= current.Token)
            throw new ArgumentException("A replacement requires a greater, nonzero provider-allocated token without wrapping.");
        return new(user, scope.Key, token, now.AddSeconds(durationSeconds).ToUnixTimeMilliseconds(), candidate, 0);
    }

    private static List<InventorySlotRecord>? Debit(long user, InventoryLoadoutScope scope,
        IReadOnlyList<InventorySlotRecord> rows, InventoryLoadoutState loadout, int coinItemId, int cost, DateTimeOffset now)
    {
        InventoryArticles.ValidateRows(user, rows);
        _ = loadout.Copy();
        var staged = rows.ToList();
        if (cost == 0) return staged;
        var snapshot = InventorySnapshot.Project(rows, loadout, scope);
        var matches = snapshot.CharacterBag.Where(row => row.ItemId == coinItemId).ToArray();
        if (matches.Length > 1) throw new InvalidDataException("The coin item has ambiguous duplicate ownership.");
        if (matches.Length == 0) return null;
        var coin = matches[0];
        if (coin.SlotKey <= 0 || coin.Quantity < cost || coin.Lifetime.Kind != InventoryLifetimeKind.Permanent
            || InventoryExpiry.IsExpired(coin, now) || InventoryArticleIdentityResolver.Of(coin) is not null
            || coin.Pet is not null || coin.Enhancements is not null || coin.Modifications is not null || coin.Favorites is not null
            || (coin.Flags & InventorySlotRecord.WornFlagBit) != 0) return null;
        // Validate raw mirrors, including rows that a build loadout projection may rewrite.
        var rawCoin = rows.Single(row => row.Bag == InventoryBag.CharacterBag && row.ItemId == coinItemId);
        InventoryArticles.RequireMirrors(rawCoin, rows.Where(row => row.Bag == InventoryBag.EquippedBag && row.ItemId == coinItemId).ToArray());
        var remaining = coin.Quantity - cost;
        if (remaining == 0) staged.RemoveAll(row => row.ItemId == coinItemId);
        else
            for (var index = 0; index < staged.Count; index++)
                if (staged[index].ItemId == coinItemId) staged[index] = staged[index] with { Quantity = remaining };
        return staged;
    }

    private static long Highest(IReadOnlyList<InventorySlotRecord> rows, long watermark)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(watermark);
        return Math.Max(watermark, rows.Select(row => row.SlotKey).DefaultIfEmpty().Max());
    }
}
