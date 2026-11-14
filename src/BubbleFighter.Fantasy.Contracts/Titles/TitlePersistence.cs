using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BubbleFighter.Fantasy.Contracts.Inventory;

namespace BubbleFighter.Fantasy.Contracts.Titles;

/// <summary>Bounds retained medal balances independently of title ownership.</summary>
public sealed record TitleMedalLimits
{
    /// <summary>Gets the maximum native catalogue scopes retained for one account.</summary>
    [Range(1, 256)]
    public int MaxScopes { get; init; } = 32;

    /// <summary>Gets the maximum distinct medal balances retained in each catalogue scope.</summary>
    [Range(1, 4096)]
    public int MaxMedals { get; init; } = 1024;

    /// <summary>Rejects invalid configured budgets before a provider accepts mutations.</summary>
    public void Validate() => Validator.ValidateObject(this, new(this), true);
}

/// <summary>Credits catalogue medals once for a stable authoritative event identity.</summary>
/// <param name="UserNum">The existing account receiving the medals.</param>
/// <param name="CatalogScope">The native catalogue defining the medal identities.</param>
/// <param name="Source">The bounded producer identity retained with the grant receipt.</param>
/// <param name="GrantKey">An immutable event identity unique within the account and catalogue scope.</param>
/// <param name="Credits">Positive credits, with each medal appearing once.</param>
public sealed record TitleMedalGrantCommand(long UserNum, string CatalogScope, string Source,
    string GrantKey, IReadOnlyList<TitleMedalCount> Credits)
{
    /// <summary>Gets a copied list of credits so callers cannot alter an in-flight transaction.</summary>
    public IReadOnlyList<TitleMedalCount> Credits { get; } = Array.AsReadOnly(Credits.ToArray());

    /// <summary>Rejects invalid identity, duplicate medals and unbounded event fields.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(UserNum);
        InventoryLoadoutScope.ValidateKey(CatalogScope);
        TitleMedalLedger.ValidateEvent(Source, GrantKey);
        TitleMedalLedger.ValidateCounts(Credits, true);
    }
}

/// <summary>Reports whether a new credit event was applied and returns the complete resulting medal balances.</summary>
/// <param name="Applied">False when the same event and payload were already committed.</param>
/// <param name="Medals">The complete ordered medal balance map for this catalogue scope.</param>
public sealed record TitleMedalGrantResult(bool Applied, IReadOnlyList<TitleMedalCount> Medals);

/// <summary>Atomically acquires a native title stock item by spending its resolved catalogue medal costs.</summary>
/// <param name="UserNum">The authenticated account claiming the title.</param>
/// <param name="Scope">The native item catalogue and loadout scope.</param>
/// <param name="TitleId">The native title definition identity used to resolve the recipe.</param>
/// <param name="StockId">The native stock identity associated with that title definition.</param>
/// <param name="MedalDebits">Positive required medal costs, with each medal appearing once.</param>
/// <param name="GrantTerm">The native stock grant terms, including the actual inventory item identity.</param>
/// <param name="ExpectedTitle">The exact projected character-bag row, or null when ownership was absent.</param>
/// <param name="Now">The authoritative instant used to reject active ownership and calculate expiry.</param>
public sealed record TitleClaimCommand(long UserNum, InventoryLoadoutScope Scope, int TitleId, int StockId,
    IReadOnlyList<TitleMedalCount> MedalDebits, InventoryGrantTerm GrantTerm,
    InventorySlotRecord? ExpectedTitle, DateTimeOffset Now)
{
    /// <summary>Gets copied catalogue costs, preventing a caller from changing a pending debit.</summary>
    public IReadOnlyList<TitleMedalCount> MedalDebits { get; } = Array.AsReadOnly(MedalDebits.ToArray());

    private IReadOnlyList<InventoryGrantTerm> _bonusGrants = Array.Empty<InventoryGrantTerm>();
    private IReadOnlyList<TitleMedalCount> _bonusMedals = Array.Empty<TitleMedalCount>();

    /// <summary>Gets copied stock or stock-set grants committed atomically on this title's first bonus-bearing acquisition.</summary>
    public IReadOnlyList<InventoryGrantTerm> BonusGrants
    {
        get => _bonusGrants;
        init => _bonusGrants = Array.AsReadOnly(value.ToArray());
    }

    /// <summary>Gets copied native bonus medal credits, applied once with the title and its item bonuses.</summary>
    public IReadOnlyList<TitleMedalCount> BonusMedals
    {
        get => _bonusMedals;
        init => _bonusMedals = Array.AsReadOnly(value.ToArray());
    }

    /// <summary>
    /// Gets whether the title is granted for an authoritative condition instead of for spent medals. An automatic grant debits
    /// nothing and carries no bonuses; its idempotence is the permanent ownership row, which a second claim finds and refuses.
    /// </summary>
    public bool IsAutomaticGrant { get; init; }

    /// <summary>Gets ordinary Article metadata to initialize atomically before publishing the committed inventory.</summary>
    public ArticleInitializationCommand? ArticleInitialization { get; init; }

    /// <summary>Gets pet metadata to initialize within the same account mutation and identity allocation.</summary>
    public PetInitializationCommand? PetInitialization { get; init; }

    /// <summary>Rejects malformed identity, costs, unsupported grant terms and foreign initialization commands.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(UserNum);
        ArgumentNullException.ThrowIfNull(Scope);
        ArgumentNullException.ThrowIfNull(GrantTerm);
        if (TitleId <= 0 || StockId <= 0 || !Scope.Supports(GrantTerm.ItemId)
            || !Enum.IsDefined(GrantTerm.Kind) || GrantTerm.Amount <= 0 || GrantTerm.Maximum < GrantTerm.Amount
            || GrantTerm.Kind == InventoryGrantKind.Permanent && GrantTerm.Amount != 1
            || ExpectedTitle is { } expected && (expected.UserNum != UserNum
                || expected.Bag != InventoryBag.CharacterBag || expected.ItemId != GrantTerm.ItemId))
            throw new ArgumentException("A title claim requires resolved native stock terms and matching ownership.");
        if (IsAutomaticGrant)
        {
            if (MedalDebits.Count != 0 || BonusMedals.Count != 0 || BonusGrants.Count != 0)
                throw new ArgumentException("An automatic title grant debits no medals and carries no bonuses.");
        }
        else TitleMedalLedger.ValidateCounts(MedalDebits, true);
        TitleMedalLedger.ValidateCounts(BonusMedals, false);
        if (BonusMedals.Any(row => row.Count <= 0) || BonusGrants.Count >= InventoryLimits.MaxInventorySlotsPerBag
            || BonusGrants.Any(term => term is null || term.ItemId == GrantTerm.ItemId || !Scope.Supports(term.ItemId)
                || !Enum.IsDefined(term.Kind) || term.Amount <= 0 || term.Maximum < term.Amount
                || term.Kind == InventoryGrantKind.Permanent && term.Amount != 1))
            throw new ArgumentException("Title bonuses require bounded native medal or stock grant terms.");
        ArticleInitialization?.ValidateFor(UserNum, Scope);
        PetInitialization?.ValidateFor(UserNum, Scope);
    }

    /// <summary>Stages the ordinary inventory grant under the provider's account lock without spending medals or allocating identities.</summary>
    public TitleInventoryGrant? Plan(IReadOnlyList<InventorySlotRecord> rows, InventoryLoadoutState loadout,
        long lastSlotKey, bool includeBonuses = true)
    {
        Validate();
        var snapshot = InventorySnapshot.Project(rows, loadout, Scope);
        var found = snapshot.CharacterBag.Where(row => row.ItemId == GrantTerm.ItemId).ToArray();
        if (found.Length > 1 || found.SingleOrDefault() != ExpectedTitle) return null;
        if (ExpectedTitle is { } owned && (owned.Lifetime.Kind == InventoryLifetimeKind.Permanent
            || owned.Quantity > 0 && !(owned.Lifetime.Kind == InventoryLifetimeKind.ExpiresAt
                && owned.Lifetime.ExpiresAtUnixMilliseconds <= Now.ToUnixTimeMilliseconds()))) return null;
        // An unspecified historical lifetime is not evidence that a title may be renewed.
        if (ExpectedTitle?.Lifetime.Kind == InventoryLifetimeKind.Unspecified) return null;
        var mirrors = rows.Where(row => row.ItemId == GrantTerm.ItemId).ToArray();
        if (ExpectedTitle is null && mirrors.Length != 0) return null;
        if (ExpectedTitle is not null) InventoryArticles.RequireMirrors(
            mirrors.Single(row => row.Bag == InventoryBag.CharacterBag),
            mirrors.Where(row => row.Bag == InventoryBag.EquippedBag).ToArray());
        IReadOnlyList<BubbleFighter.Fantasy.Contracts.Shop.ShopPurchaseGrant> grants;
        var terms = includeBonuses ? new[] { GrantTerm }.Concat(BonusGrants).ToArray() : [GrantTerm];
        try { grants = InventoryGrantPlanner.Plan(UserNum, terms, snapshot.CharacterBag, Now); }
        catch (InventoryGrantRejectedException) { return null; }
        if (snapshot.CharacterBag.Count + grants.Count(grant => grant.ExpectedSlot is null) > InventoryLimits.MaxInventorySlotsPerBag)
            throw new InvalidOperationException("The title and its bonuses exceed the character-bag capacity.");
        var staged = rows.ToList();
        var next = Math.Max(lastSlotKey, rows.Select(row => row.SlotKey).DefaultIfEmpty().Max());
        foreach (var grant in grants)
        {
            var raw = staged.Where(row => row.ItemId == grant.ItemId).ToArray();
            if (grant.ExpectedSlot is null)
            {
                if (raw.Length != 0) return null;
                next = checked(next + 1);
                staged.Add(new(UserNum, InventoryBag.CharacterBag, next, grant.ItemId, grant.Quantity, 0)
                    { Lifetime = grant.Lifetime });
            }
            else
            {
                InventoryArticles.RequireMirrors(raw.Single(row => row.Bag == InventoryBag.CharacterBag),
                    raw.Where(row => row.Bag == InventoryBag.EquippedBag).ToArray());
                for (var i = 0; i < staged.Count; i++)
                    if (staged[i].ItemId == grant.ItemId)
                        staged[i] = staged[i] with { Quantity = grant.Quantity, Lifetime = grant.Lifetime };
            }
        }
        var granted = staged.Single(row => row.Bag == InventoryBag.CharacterBag && row.ItemId == GrantTerm.ItemId);
        return new(staged.AsReadOnly(), granted, next);
    }
}

/// <summary>Contains an uncommitted native stock grant and its ordinary slot-key watermark.</summary>
/// <param name="Rows">Both complete raw inventory bags after the staged grant.</param>
/// <param name="GrantedItem">The character-bag row created or renewed by the grant.</param>
/// <param name="LastSlotKey">The watermark to commit with the new inventory rows.</param>
public sealed record TitleInventoryGrant(IReadOnlyList<InventorySlotRecord> Rows,
    InventorySlotRecord GrantedItem, long LastSlotKey);

/// <summary>Returns all state needed to publish a committed title claim in native consumer order.</summary>
/// <param name="Inventory">Both complete projected inventory bags, including initialized Article and pet metadata.</param>
/// <param name="Medals">The complete medal balance map after every recipe debit.</param>
/// <param name="GrantedItem">The granted character-bag row after metadata initialization.</param>
public sealed record TitleClaimResult(InventorySnapshot Inventory, IReadOnlyList<TitleMedalCount> Medals,
    InventorySlotRecord GrantedItem);

/// <summary>Persists catalogue medal balances and commits their exchange for ordinary inventory ownership atomically.</summary>
public interface ITitleMedalStore
{
    /// <summary>Gets whether this provider atomically retains one-time acquisition bonuses independently of inventory lifetime.</summary>
    bool SupportsAcquisitionBonuses => false;

    /// <summary>Reads a complete catalogue medal map without creating balances or ownership.</summary>
    ValueTask<IReadOnlyList<TitleMedalCount>> ReadAsync(long userNum, string catalogScope, CancellationToken ct);

    /// <summary>
    /// Credits an authoritative event once. An exact replay changes nothing; a replay with another producer or payload fails
    /// without changes. Receipts are keyed rows, so their number never limits later grants.
    /// </summary>
    ValueTask<TitleMedalGrantResult> GrantAsync(TitleMedalGrantCommand command, CancellationToken ct);

    /// <summary>Spends all medal costs and grants the resolved stock item in one mutation; stale or unaffordable claims return null.</summary>
    ValueTask<TitleClaimResult?> TryClaimAsync(TitleClaimCommand command, CancellationToken ct);
}

/// <summary>
/// Records one applied credit event. Stores keep receipts as rows keyed by account, catalogue scope and
/// <see cref="GrantKey"/>, apart from the balances, so deduplication is a keyed lookup and is never evicted.
/// </summary>
/// <param name="Source">The authoritative credit producer.</param>
/// <param name="GrantKey">The account-and-scope unique event identity.</param>
/// <param name="Fingerprint">The SHA-256 of sorted positive medal credits.</param>
public sealed record TitleMedalGrantReceipt(string Source, string GrantKey, string Fingerprint)
{
    /// <summary>Builds the receipt a command records once it is applied.</summary>
    public static TitleMedalGrantReceipt For(TitleMedalGrantCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();
        var text = string.Join(";", command.Credits.OrderBy(row => row.MedalId)
            .Select(row => row.MedalId.ToString(CultureInfo.InvariantCulture) + ":" + row.Count.ToString(CultureInfo.InvariantCulture)));
        return new(command.Source, command.GrantKey, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))));
    }

    /// <summary>Accepts a stored receipt for the same event only when it records this exact producer and payload.</summary>
    public void RequireSameEvent(TitleMedalGrantReceipt stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        stored.Validate();
        if (stored.GrantKey != GrantKey) throw new ArgumentException("The stored receipt belongs to another event.", nameof(stored));
        if (stored.Source != Source || stored.Fingerprint != Fingerprint)
            throw new InvalidOperationException("A medal grant event was replayed with a different producer or credit payload.");
    }

    /// <summary>Checks a persisted receipt's identity bounds and fingerprint form.</summary>
    public void Validate()
    {
        TitleMedalLedger.ValidateEvent(Source, GrantKey);
        if (Fingerprint is null || Fingerprint.Length != 64 || Fingerprint.Any(c => !char.IsAsciiHexDigit(c)))
            throw new InvalidDataException("Title medal receipt fingerprint is malformed.");
    }
}

/// <summary>Immutable medal balances for one account's native catalogue scope.</summary>
public sealed class TitleMedalLedger
{
    /// <summary>Copies persisted balances before they are validated or used by a transaction.</summary>
    public TitleMedalLedger(IReadOnlyList<TitleMedalCount> medals)
    {
        Medals = Array.AsReadOnly(medals.ToArray());
    }

    /// <summary>Gets the complete medal map; zero balances may remain to preserve a stable stored identity.</summary>
    public IReadOnlyList<TitleMedalCount> Medals { get; }

    /// <summary>Gets an empty ledger for a scope with no medal events.</summary>
    public static TitleMedalLedger Empty { get; } = new([]);

    /// <summary>Checks persisted values, uniqueness and deployment limits before use.</summary>
    public void Validate(TitleMedalLimits limits)
    {
        limits.Validate();
        ValidateCounts(Medals, false);
        if (Medals.Count > limits.MaxMedals) throw new InvalidDataException("Title medal state exceeds its capacity.");
    }

    /// <summary>Stages the balances after a new credit event; the store records its receipt in the same transaction.</summary>
    public TitleMedalLedger Credit(TitleMedalGrantCommand command, TitleMedalLimits limits)
    {
        command.Validate();
        Validate(limits);
        var counts = Medals.ToDictionary(row => row.MedalId, row => row.Count);
        foreach (var credit in command.Credits) counts[credit.MedalId] = checked(counts.GetValueOrDefault(credit.MedalId) + credit.Count);
        var result = new TitleMedalLedger(counts.OrderBy(pair => pair.Key).Select(pair => new TitleMedalCount(pair.Key, pair.Value)).ToArray());
        result.Validate(limits);
        return result;
    }

    /// <summary>Stages every debit together, returning null when any required balance is insufficient.</summary>
    public TitleMedalLedger? Debit(IReadOnlyList<TitleMedalCount> debits, TitleMedalLimits limits)
    {
        Validate(limits);
        ValidateCounts(debits, true);
        var counts = Medals.ToDictionary(row => row.MedalId, row => row.Count);
        if (debits.Any(row => counts.GetValueOrDefault(row.MedalId) < row.Count)) return null;
        foreach (var debit in debits) counts[debit.MedalId] -= debit.Count;
        return new(counts.OrderBy(pair => pair.Key).Select(pair => new TitleMedalCount(pair.Key, pair.Value)).ToArray());
    }

    /// <summary>Stages medal costs and optional bonus credits; the provider owns the keyed acquisition receipt in the same transaction.</summary>
    public TitleMedalLedger? Claim(TitleClaimCommand command, TitleMedalLimits limits, bool includeBonuses)
    {
        command.Validate();
        if (command.IsAutomaticGrant)
        {
            Validate(limits);
            return this;
        }
        var debited = Debit(command.MedalDebits, limits);
        if (debited is null || !includeBonuses || command.BonusMedals.Count == 0) return debited;
        var counts = debited.Medals.ToDictionary(row => row.MedalId, row => row.Count);
        foreach (var bonus in command.BonusMedals)
            counts[bonus.MedalId] = checked(counts.GetValueOrDefault(bonus.MedalId) + bonus.Count);
        var ledger = new TitleMedalLedger(counts.OrderBy(pair => pair.Key)
            .Select(pair => new TitleMedalCount(pair.Key, pair.Value)).ToArray());
        ledger.Validate(limits);
        return ledger;
    }

    /// <summary>Checks a bounded native medal map or a positive, nonempty credit/debit list.</summary>
    public static void ValidateCounts(IReadOnlyList<TitleMedalCount> counts, bool positive)
    {
        ArgumentNullException.ThrowIfNull(counts);
        if (counts.Count > 4096 || positive && counts.Count == 0
            || counts.Any(row => row is null || row.MedalId <= 0 || row.Count < (positive ? 1 : 0))
            || counts.Select(row => row.MedalId).Distinct().Count() != counts.Count)
            throw new ArgumentException("Medal entries must be bounded, unique and have valid positive identities and counts.");
    }

    /// <summary>Checks the schema bounds and printable ASCII representation of event identities.</summary>
    public static void ValidateEvent(string source, string grantKey)
    {
        if (string.IsNullOrEmpty(source) || source.Length > 64 || source.Any(c => c is < '!' or > '~')
            || string.IsNullOrEmpty(grantKey) || grantKey.Length > 128 || grantKey.Any(c => c is < '!' or > '~'))
            throw new ArgumentException("Medal grant source and event identity must be bounded printable ASCII without spaces.");
    }
}
