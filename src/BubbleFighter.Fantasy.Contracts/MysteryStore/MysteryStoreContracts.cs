using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Shop;

namespace BubbleFighter.Fantasy.Contracts.MysteryStore;

/// <summary>One native Stock or StockSet offer with frozen inventory grant terms and an explicit coin quote.</summary>
/// <param name="NativeKey">Positive native stock identity used by the request and receipt.</param>
/// <param name="CoinPrice">Exact configured inventory-coin quote, bounded to the native signed display range.</param>
/// <param name="Unknown4">Opaque native offer word retained without assigning a meaning.</param>
/// <param name="UnknownName">Opaque native offer text retained in its advertised order.</param>
/// <param name="Grants">All resolved stock members, copied before the offer is published.</param>
public sealed record MysteryStoreOffer(int NativeKey, ushort CoinPrice, uint Unknown4, string UnknownName,
    IReadOnlyList<InventoryGrantTerm> Grants)
{
    /// <summary>Gets copied native member terms; matching item terms are combined by the shared inventory planner.</summary>
    public IReadOnlyList<InventoryGrantTerm> Grants { get; } = CopyGrants(Grants);

    private static IReadOnlyList<InventoryGrantTerm> CopyGrants(IReadOnlyList<InventoryGrantTerm> grants)
    {
        ArgumentNullException.ThrowIfNull(grants);
        if (grants.Count is < 1 or > 10 || grants.Any(term => term is null))
            throw new ArgumentException("A mystery offer requires 1..10 complete grant terms.", nameof(grants));
        return Array.AsReadOnly(grants.ToArray());
    }
}

/// <summary>A complete immutable six-offer candidate resolved from one build's native catalog and configured prices.</summary>
/// <param name="CatalogRevision">Canonical SHA-256 fingerprint of the complete effective reward and metadata policy.</param>
/// <param name="CoinItemId">Inventory item whose counted ownership pays for mutations.</param>
/// <param name="RefreshCoinCost">Exact configured coin quote for replacing this board.</param>
/// <param name="GroupStockVariants">Native configuration value retained with the selected offers.</param>
/// <param name="Offers">Exactly six distinct offers copied in bitmap order.</param>
public sealed record MysteryStoreBoard(string CatalogRevision, int CoinItemId, ushort RefreshCoinCost,
    bool GroupStockVariants, IReadOnlyList<MysteryStoreOffer> Offers)
{
    /// <summary>Gets copied offers in the exact order addressed by the purchased bitmap.</summary>
    public IReadOnlyList<MysteryStoreOffer> Offers { get; } = CopyOffers(Offers);

    /// <summary>Rejects unbounded or incompatible candidates before they can be advertised or stored.</summary>
    /// <param name="userNum">Authenticated owner used for shared inventory term validation.</param>
    /// <param name="scope">Current build's complete supported inventory catalog.</param>
    public void Validate(long userNum, InventoryLoadoutScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ValidateStored(userNum);
        if (!scope.Supports(CoinItemId) || Offers.Any(offer => offer.Grants.Any(term => !scope.Supports(term.ItemId))))
            throw new ArgumentException("A mystery board contains an item outside its current native catalog.");
    }

    // Persisted old revisions must remain decodable after their item catalog changes, so Ensure can replace them.
    internal void ValidateStored(long userNum)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userNum);
        if (CatalogRevision is null || CatalogRevision.Length != 64
            || CatalogRevision.Any(character => !char.IsAsciiHexDigit(character)))
            throw new ArgumentException("The catalog revision must be a fixed SHA-256 hex value.");
        if (CoinItemId <= 0 || RefreshCoinCost > short.MaxValue
            || Offers.Count != 6 || Offers.Any(offer => offer is null)
            || Offers.Select(offer => offer.NativeKey).Distinct().Count() != 6)
            throw new ArgumentException("A mystery board requires six distinct native offers and a supported coin item.");
        foreach (var offer in Offers)
        {
            if (offer.NativeKey <= 0 || offer.CoinPrice > short.MaxValue || offer.UnknownName is null
                || offer.UnknownName.Length > 128 || offer.Grants.Count is < 1 or > 10
                || offer.Grants.Any(term => term is null))
                throw new ArgumentException("A mystery offer exceeds its native member or presentation bounds.");
            // Check the terms through the same planner used at purchase time, without allocating or committing rows.
            _ = InventoryGrantPlanner.Plan(userNum, offer.Grants, [], DateTimeOffset.UnixEpoch);
        }
    }

    private static IReadOnlyList<MysteryStoreOffer> CopyOffers(IReadOnlyList<MysteryStoreOffer> offers)
    {
        ArgumentNullException.ThrowIfNull(offers);
        if (offers.Count != 6 || offers.Any(offer => offer is null))
            throw new ArgumentException("A mystery board requires exactly six complete offers.", nameof(offers));
        return Array.AsReadOnly(offers.ToArray());
    }
}

/// <summary>The durable authoritative board, whose token is opaque to clients and never reused by its provider.</summary>
/// <param name="UserNum">Authenticated owner of the selection.</param>
/// <param name="ScopeKey">Case-sensitive native catalog scope.</param>
/// <param name="Token">Nonzero opaque client token; providers retain its monotonic history.</param>
/// <param name="ExpiresAtUnixMilliseconds">Exclusive UTC admission deadline.</param>
/// <param name="Board">Frozen ordered offers and quoted configuration.</param>
/// <param name="PurchasedMask">Only the lowest six bits may identify committed purchases.</param>
public sealed record MysteryStoreSelection(long UserNum, string ScopeKey, uint Token, long ExpiresAtUnixMilliseconds,
    MysteryStoreBoard Board, uint PurchasedMask)
{
    /// <summary>Checks persisted ownership, bitmap and frozen shape without interpreting an old revision through current policy.</summary>
    /// <param name="userNum">Expected authenticated owner.</param>
    /// <param name="scope">Expected scope key; retired catalog items remain decodable until replacement.</param>
    public void Validate(long userNum, InventoryLoadoutScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(Board);
        if (userNum <= 0 || UserNum != userNum || ScopeKey != scope.Key || Token == 0 || PurchasedMask > 63
            || ExpiresAtUnixMilliseconds <= 0)
            throw new InvalidDataException("Mystery selection ownership, token or bitmap is invalid.");
        _ = DateTimeOffset.FromUnixTimeMilliseconds(ExpiresAtUnixMilliseconds);
        Board.ValidateStored(userNum);
    }
}

/// <summary>Offers a trusted candidate for bootstrap, natural expiry or deliberate catalog revision replacement without coins.</summary>
/// <param name="UserNum">Owner derived from the originating session.</param>
/// <param name="Scope">Complete supported catalog and build key.</param>
/// <param name="Candidate">Server-selected immutable candidate from the current effective revision.</param>
/// <param name="DurationSeconds">Positive configured lifetime for a newly installed board.</param>
public sealed record MysteryStoreEnsureCommand(long UserNum, InventoryLoadoutScope Scope,
    MysteryStoreBoard Candidate, ushort DurationSeconds);

/// <summary>Requests a paid replacement using the current token and echoed refresh quote.</summary>
/// <param name="Inventory">Owner, scope and immutable metadata policies from the candidate's effective revision.</param>
/// <param name="ExpectedToken">Token echoed by the originating client.</param>
/// <param name="QuotedCoinCost">Echoed quote, which must match the stored refresh cost.</param>
/// <param name="Candidate">Server-selected complete replacement from the same effective revision.</param>
/// <param name="DurationSeconds">Positive configured lifetime for the replacement.</param>
public sealed record MysteryStoreRefreshCommand(InventoryMetadataPreparationCommand Inventory,
    uint ExpectedToken, ushort QuotedCoinCost, MysteryStoreBoard Candidate, ushort DurationSeconds);

/// <summary>Requests one frozen offer; neither reward terms nor a server debit amount can be supplied by the client.</summary>
/// <param name="Inventory">Owner, scope and immutable metadata policies from one effective catalog snapshot.</param>
/// <param name="CatalogRevision">Server fingerprint of that same effective catalog snapshot.</param>
/// <param name="ExpectedToken">Token echoed by the originating client.</param>
/// <param name="NativeKey">Requested native stock identity; the stored board supplies its complete grants.</param>
/// <param name="QuotedCoinPrice">Echoed quote, compared exactly with the stored price.</param>
public sealed record MysteryStorePurchaseCommand(InventoryMetadataPreparationCommand Inventory,
    string CatalogRevision, uint ExpectedToken, int NativeKey, ushort QuotedCoinPrice);

/// <summary>Separates committed effects and harmless replays from refusals before any provider write.</summary>
public enum MysteryStoreOutcome
{
    /// <summary>A free bootstrap, natural expiry or deliberate server catalog revision replacement was installed.</summary>
    SelectionCreated,
    /// <summary>Coins were consumed and a replacement board was installed atomically.</summary>
    Refreshed,
    /// <summary>Coins, all rewards and the purchased bit committed atomically.</summary>
    Purchased,
    /// <summary>The current unexpired board was returned without writes.</summary>
    Unchanged,
    /// <summary>This exact current-token offer already committed; no debit or reward is repeated.</summary>
    AlreadyPurchased,
    /// <summary>No board exists yet; the caller must bootstrap rather than purchase or refresh.</summary>
    MissingSelection,
    /// <summary>The request token no longer identifies the durable current board.</summary>
    StaleToken,
    /// <summary>The current board expired before mutation admission.</summary>
    Expired,
    /// <summary>The deployed catalog revision cannot execute the stored board safely.</summary>
    CatalogChanged,
    /// <summary>The native request key is absent from the six frozen offers.</summary>
    UnknownOffer,
    /// <summary>The echoed quote differs from the frozen authoritative coin price.</summary>
    QuoteMismatch,
    /// <summary>The durable uint token reached its maximum; replacing the board would reuse or wrap a token.</summary>
    TokenExhausted,
    /// <summary>The player lacks enough compatible counted coin ownership.</summary>
    InsufficientCoins,
    /// <summary>An inventory grant or capacity rule refuses the whole purchase.</summary>
    GrantRefused,
}

/// <summary>A provider result whose inventory, when present, comes from the same locked or committed state as its board.</summary>
/// <param name="Outcome">Committed action, harmless replay or refusal classification.</param>
/// <param name="Selection">Current authoritative selection, or null before bootstrap.</param>
/// <param name="Inventory">Committed complete inventory, mandatory for purchases, refreshes and exact replays.</param>
/// <param name="GrantedItems">Post-metadata reward owners; empty for replays and refusals.</param>
/// <param name="ReceiptNativeKey">Exact native Stock/StockSet key retained independently of item identities.</param>
/// <param name="GrantRefusal">Shared inventory refusal classification when applicable.</param>
public sealed record MysteryStoreResult(MysteryStoreOutcome Outcome, MysteryStoreSelection? Selection,
    InventorySnapshot? Inventory, IReadOnlyList<InventorySlotRecord> GrantedItems, int ReceiptNativeKey = 0,
    ShopPurchaseRefusal GrantRefusal = ShopPurchaseRefusal.Unspecified)
{
    /// <summary>Gets committed inventory or the mandatory current locked snapshot for an exact purchase replay.</summary>
    public InventorySnapshot? Inventory { get; } = Outcome is MysteryStoreOutcome.Purchased or MysteryStoreOutcome.Refreshed
        or MysteryStoreOutcome.AlreadyPurchased
        ? Inventory ?? throw new ArgumentNullException(nameof(Inventory)) : Inventory;

    /// <summary>Gets copied post-commit reward rows; replays and refusals have no newly granted rows.</summary>
    public IReadOnlyList<InventorySlotRecord> GrantedItems { get; } = CopyGrants(GrantedItems);

    private static IReadOnlyList<InventorySlotRecord> CopyGrants(IReadOnlyList<InventorySlotRecord> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count > 10 || rows.Any(row => row is null))
            throw new ArgumentException("A mystery receipt exceeds its bounded stock members.", nameof(rows));
        return Array.AsReadOnly(rows.ToArray());
    }
}

/// <summary>Serializes mystery selection and inventory mutations together under the original account's authority.</summary>
public interface IMysteryStore
{
    /// <summary>Returns the current board or installs a free candidate when missing, expired or deliberately revised by the server.</summary>
    /// <param name="command">Trusted owner and catalog candidate; never constructed directly from wire reward data.</param>
    /// <param name="ct">Cancellation checked again after account-lock admission and before commit.</param>
    /// <returns>The locked current selection or the atomically installed replacement.</returns>
    ValueTask<MysteryStoreResult> EnsureAsync(MysteryStoreEnsureCommand command, CancellationToken ct);

    /// <summary>Atomically consumes the stored refresh coin cost and replaces the current board with a new token.</summary>
    /// <param name="command">Original owner, expected token, echoed quote and trusted candidate.</param>
    /// <param name="ct">Cancellation checked after lock or allocator waits and before commit.</param>
    /// <returns>The committed board and inventory, or a refusal without partial effects.</returns>
    ValueTask<MysteryStoreResult> RefreshAsync(MysteryStoreRefreshCommand command, CancellationToken ct);

    /// <summary>Atomically consumes stored-price coins, grants every frozen stock member and marks the offer purchased.</summary>
    /// <param name="command">Original owner and immutable catalog context with the client's requested key and quote.</param>
    /// <param name="ct">Cancellation checked after lock or allocator waits and before commit.</param>
    /// <returns>Committed grants, an authoritative current-inventory replay, or a refusal without effects.</returns>
    ValueTask<MysteryStoreResult> PurchaseAsync(MysteryStorePurchaseCommand command, CancellationToken ct);
}
