namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>Removes an exact ownership or debits a counted material in one laboratory transaction.</summary>
/// <param name="ExpectedSlot">The complete projected row used to plan this operation.</param>
/// <param name="Amount">The positive counted debit, or zero when removing the complete ownership.</param>
/// <param name="RemoveOwnership">Whether every bag mirror of the ownership is removed.</param>
public sealed record InventoryLaboratoryDebit(InventorySlotRecord ExpectedSlot, int Amount, bool RemoveOwnership);

/// <summary>Defines a laboratory reward with fractional native days retained.</summary>
/// <param name="ItemId">The native catalog identity granted by the recipe.</param>
/// <param name="Kind">Count, days, or permanent; life rewards are not admitted by this recipe surface.</param>
/// <param name="Amount">The positive integral count, fractional day duration, or one for permanent ownership.</param>
/// <param name="Maximum">The configured maximum resulting count or duration; one for permanent ownership.</param>
public sealed record InventoryLaboratoryGrantTerm(int ItemId, InventoryGrantKind Kind, double Amount, double Maximum)
{
    /// <summary>Rejects nonfinite, ambiguous or unsupported reward quantities before a store write.</summary>
    public void Validate()
    {
        if (ItemId <= 0 || !double.IsFinite(Amount) || !double.IsFinite(Maximum) || Amount <= 0 || Maximum < Amount
            || Kind is not (InventoryGrantKind.Count or InventoryGrantKind.Days or InventoryGrantKind.Permanent)
            || Kind == InventoryGrantKind.Count && (Amount != Math.Truncate(Amount) || Maximum != Math.Truncate(Maximum) || Maximum > int.MaxValue)
            || Kind == InventoryGrantKind.Days && Maximum > int.MaxValue
            || Kind == InventoryGrantKind.Permanent && (Amount != 1 || Maximum != 1))
            throw new ArgumentException("Laboratory reward terms are invalid or exceed their native bounds.");
    }
}

/// <summary>Pairs a reward with the exact pre-transaction ownership expected for its identity.</summary>
/// <param name="Term">The validated reward chosen before entering persistence.</param>
/// <param name="ExpectedOwned">Existing projected ownership, or null to require that the reward was previously unowned.</param>
public sealed record InventoryLaboratoryGrant(InventoryLaboratoryGrantTerm Term, InventorySlotRecord? ExpectedOwned);

/// <summary>Plans a complete consume-and-grant operation under the provider's account lock.</summary>
/// <param name="UserNum">The authenticated owner of all inputs and outputs.</param>
/// <param name="Scope">The native catalog and equipment-selection scope.</param>
/// <param name="Debits">Exact input ownerships and counted material debits.</param>
/// <param name="Grants">Preselected rewards and their expected ownership guards.</param>
/// <param name="Now">The authoritative instant used for expiry and duration calculations.</param>
public sealed record InventoryLaboratoryCommand(long UserNum, InventoryLoadoutScope Scope,
    IReadOnlyList<InventoryLaboratoryDebit> Debits, IReadOnlyList<InventoryLaboratoryGrant> Grants, DateTimeOffset Now)
{
    /// <summary>Gets immutable debit plans that cannot be replaced during a transaction.</summary>
    public IReadOnlyList<InventoryLaboratoryDebit> Debits { get; } = Array.AsReadOnly(Debits.ToArray());

    /// <summary>Gets immutable grant plans that cannot be rerolled during a transaction.</summary>
    public IReadOnlyList<InventoryLaboratoryGrant> Grants { get; } = Array.AsReadOnly(Grants.ToArray());

    /// <summary>Gets ordinary Article initialization committed after new reward rows exist.</summary>
    public ArticleInitializationCommand? ArticleInitialization { get; init; }

    /// <summary>Gets pet initialization required by the complete inventory publication.</summary>
    public PetInitializationCommand? PetInitialization { get; init; }

    /// <summary>
    /// Gets the guards of a same-identity permanent conversion, or empty for an ordinary recipe. Every guard names one
    /// target, and the plan must grant exactly those targets permanently over their expected timed rows and debit no
    /// target; an individual item has one guard and a set has one per member. The guards hold only values.
    /// </summary>
    public IReadOnlyList<InventoryConversionGuard> Conversions
    {
        get => _conversions;
        init => _conversions = Array.AsReadOnly((value ?? throw new ArgumentNullException(nameof(value))).ToArray());
    }

    private readonly IReadOnlyList<InventoryConversionGuard> _conversions = [];

    /// <summary>Rejects duplicate, unsupported and inconsistent ownership plans before opening a transaction.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(UserNum);
        ArgumentNullException.ThrowIfNull(Scope);
        if (Debits.Count < 1 || Debits.Count > InventoryLimits.MaxInventorySlotsPerBag
            || Grants.Count < 1 || Grants.Count > InventoryLimits.MaxInventorySlotsPerBag)
            throw new ArgumentException("Laboratory input or output count exceeds inventory storage capacity.");
        if (Debits.Any(debit => debit is null || !ValidRow(debit.ExpectedSlot)
                || (debit.RemoveOwnership ? debit.Amount != 0 : debit.Amount <= 0))
            || Debits.Select(debit => debit.ExpectedSlot.ItemId).Distinct().Count() != Debits.Count
            || Grants.Any(grant => grant is null || grant.Term is null || !Scope.Supports(grant.Term.ItemId)
                || grant.ExpectedOwned is { } owned && (!ValidRow(owned) || owned.ItemId != grant.Term.ItemId))
            || Grants.Select(grant => grant.Term.ItemId).Distinct().Count() != Grants.Count)
            throw new ArgumentException("Laboratory plan contains invalid or duplicate ownership references.");
        foreach (var grant in Grants) grant.Term.Validate();
        if (Conversions.Count > 0) ValidateConversions();
        ArticleInitialization?.ValidateFor(UserNum, Scope);
        PetInitialization?.ValidateFor(UserNum, Scope);
    }

    private void ValidateConversions()
    {
        var guards = Conversions.ToArray();
        if (guards.Any(guard => guard is null) || guards.Length != Grants.Count
            || guards.Select(guard => guard.TargetItemId).Distinct().Count() != guards.Length)
            throw new ArgumentException("A conversion needs exactly one distinct guard for each granted target.");
        foreach (var guard in guards)
        {
            guard.Validate();
            var grant = Grants.SingleOrDefault(candidate => candidate.Term.ItemId == guard.TargetItemId);
            if (grant is null || grant.Term.Kind != InventoryGrantKind.Permanent || grant.ExpectedOwned is null
                || Debits.Any(debit => debit.ExpectedSlot.ItemId == guard.TargetItemId))
                throw new ArgumentException("A conversion grants exactly its own targets permanently, over their expected rows, and debits other items.");
        }
    }

    /// <summary>Creates complete staged rows and a slot watermark, or refuses stale inputs without changing them.</summary>
    public InventoryLaboratoryPlan? Plan(IReadOnlyList<InventorySlotRecord> rows, InventoryLoadoutState loadout, long lastSlotKey)
    {
        Validate();
        InventoryArticles.ValidateRows(UserNum, rows);
        var snapshot = InventorySnapshot.Project(rows, loadout, Scope);
        foreach (var debit in Debits)
        {
            var owned = snapshot.CharacterBag.Where(row => row.ItemId == debit.ExpectedSlot.ItemId).Take(2).ToArray();
            if (owned.Length != 1 || owned[0] != debit.ExpectedSlot || owned[0].Quantity <= 0
                || InventoryExpiry.IsExpired(owned[0], Now) || owned[0].Lifetime.Kind is InventoryLifetimeKind.Unspecified or InventoryLifetimeKind.Life
                || owned[0].PetIdentity is not null || owned[0].Pet is not null
                || (owned[0].Flags & InventorySlotRecord.WornFlagBit) != 0
                || !debit.RemoveOwnership && (owned[0].Quantity < debit.Amount || owned[0].ArticleIdentity is not null
                    || owned[0].Lifetime.Kind != InventoryLifetimeKind.Permanent)) return null;
            RequireMirrors(owned[0]);
        }
        foreach (var grant in Grants)
        {
            var owned = snapshot.CharacterBag.Where(row => row.ItemId == grant.Term.ItemId).Take(2).ToArray();
            if (owned.Length > 1 || owned.SingleOrDefault() != grant.ExpectedOwned) return null;
            if (grant.ExpectedOwned is { } expected) RequireMirrors(expected);
            else if (rows.Any(row => row.ItemId == grant.Term.ItemId)) return null;
        }
        if (!ConversionAdmitted(snapshot)) return null;
        var removals = Debits.ToDictionary(debit => debit.ExpectedSlot.ItemId);
        var staged = new List<InventorySlotRecord>(rows.Count + Grants.Count);
        foreach (var row in rows)
        {
            if (!removals.TryGetValue(row.ItemId, out var debit)) staged.Add(row);
            else if (!debit.RemoveOwnership && debit.ExpectedSlot.Quantity > debit.Amount)
                staged.Add(row with { Quantity = debit.ExpectedSlot.Quantity - debit.Amount });
        }
        var next = Math.Max(lastSlotKey, rows.Select(row => row.SlotKey).DefaultIfEmpty().Max());
        foreach (var grant in Grants)
        {
            var term = grant.Term;
            var remaining = staged.SingleOrDefault(row => row.Bag == InventoryBag.CharacterBag && row.ItemId == term.ItemId);
            if (remaining is { } existing && (existing.PetIdentity is not null || existing.Pet is not null
                || existing.Lifetime.Kind is InventoryLifetimeKind.Unspecified or InventoryLifetimeKind.Life)) return null;
            var quantity = 1;
            InventoryLifetime lifetime;
            if (term.Kind == InventoryGrantKind.Count)
            {
                if (remaining is not null && (remaining.ArticleIdentity is not null
                    || remaining.Lifetime.Kind != InventoryLifetimeKind.Permanent || InventoryExpiry.IsExpired(remaining, Now))) return null;
                var amount = (remaining?.Quantity ?? 0) + term.Amount;
                if (amount > term.Maximum) return null;
                quantity = checked((int)amount);
                lifetime = InventoryLifetime.Permanent;
            }
            else if (term.Kind == InventoryGrantKind.Permanent)
            {
                if (remaining?.Lifetime.Kind == InventoryLifetimeKind.Permanent) return null;
                lifetime = InventoryLifetime.Permanent;
                quantity = int.MaxValue;
            }
            else
            {
                if (remaining?.Lifetime.Kind == InventoryLifetimeKind.Permanent) return null;
                var held = remaining?.Lifetime.ExpiresAtUnixMilliseconds is { } deadline
                    ? Math.Max(0, (DateTimeOffset.FromUnixTimeMilliseconds(deadline) - Now).TotalDays) : 0;
                var duration = held + term.Amount;
                if (duration > term.Maximum || duration > (DateTimeOffset.MaxValue - Now).TotalDays) return null;
                lifetime = InventoryLifetime.Until(Now.AddDays(duration));
                quantity = checked((int)Math.Max(1, Math.Ceiling(duration)));
            }
            // A selected synthesis survivor keeps its ownership metadata even when all inputs are consumed first.
            var template = remaining ?? (grant.ExpectedOwned is null ? null
                : rows.Single(row => row.Bag == InventoryBag.CharacterBag && row.ItemId == term.ItemId));
            var created = template is null
                ? new InventorySlotRecord(UserNum, InventoryBag.CharacterBag, checked(++next), term.ItemId, quantity, 0) { Lifetime = lifetime }
                : template with { Quantity = quantity, Lifetime = lifetime };
            if (remaining is null)
            {
                staged.Add(created);
                if (template is not null)
                    staged.AddRange(rows.Where(row => row.Bag == InventoryBag.EquippedBag && row.ItemId == term.ItemId)
                        .Select(row => row with { Quantity = quantity, Lifetime = lifetime }));
            }
            else
                for (var index = 0; index < staged.Count; index++)
                    if (staged[index].ItemId == term.ItemId) staged[index] = staged[index] with { Quantity = quantity, Lifetime = lifetime };
        }
        InventoryArticles.ValidateRows(UserNum, staged);
        if (staged.GroupBy(row => row.Bag).Any(group => group.Count() > InventoryLimits.MaxInventorySlotsPerBag)) return null;
        return new(staged.AsReadOnly(), next);

        void RequireMirrors(InventorySlotRecord expected) => InventoryArticles.RequireMirrors(expected,
            snapshot.EquippedBag.Where(row => row.ItemId == expected.ItemId).ToArray());
    }

    /// <summary>
    /// Judges every conversion target again at one fresh instant, never earlier than <see cref="Now"/>, for a provider that waits on another
    /// lock between planning and commit. Always true for an ordinary recipe.
    /// </summary>
    /// <param name="rows">The same locked rows the plan was made from.</param>
    /// <param name="loadout">The same locked loadout the plan was made from.</param>
    public bool ConversionAdmitted(IReadOnlyList<InventorySlotRecord> rows, InventoryLoadoutState loadout) =>
        ConversionAdmitted(InventorySnapshot.Project(rows, loadout, Scope));

    private bool ConversionAdmitted(InventorySnapshot snapshot)
    {
        if (Conversions.Count == 0) return true;
        var at = DateTimeOffset.UtcNow is var now && now > Now ? now : Now;
        foreach (var conversion in Conversions)
        {
            var owned = snapshot.CharacterBag.Where(row => row.ItemId == conversion.TargetItemId).Take(2).ToArray();
            if (owned.Length != 1 || owned[0] != Grants.Single(grant => grant.Term.ItemId == conversion.TargetItemId).ExpectedOwned
                || !conversion.Admits(owned[0], at)) return false;
        }
        return true;
    }

    private bool ValidRow(InventorySlotRecord? row) => row is not null && row.UserNum == UserNum
        && row.Bag == InventoryBag.CharacterBag && row.SlotKey > 0 && row.ItemId > 0 && Scope.Supports(row.ItemId);
}

/// <summary>Contains both complete staged bags and the monotonic slot allocation watermark.</summary>
/// <param name="Rows">Uncommitted rows preserving unrelated ownership and metadata.</param>
/// <param name="LastSlotKey">The slot watermark to save atomically with these rows.</param>
public sealed record InventoryLaboratoryPlan(IReadOnlyList<InventorySlotRecord> Rows, long LastSlotKey);

/// <summary>Contains a committed complete inventory and the installed rows used by the result adapter.</summary>
/// <param name="Inventory">Both projected bags after mutation and metadata preparation.</param>
/// <param name="GrantedItems">The complete installed character-bag rows for the requested rewards.</param>
public sealed record InventoryLaboratoryResult(InventorySnapshot Inventory, IReadOnlyList<InventorySlotRecord> GrantedItems);

/// <summary>Serializes legacy laboratory consumption and rewards with every account inventory mutation.</summary>
public interface ILaboratoryInventoryStore
{
    /// <summary>Reads both projected bags consistently without granting or consuming anything.</summary>
    ValueTask<InventorySnapshot> ReadLaboratoryInventoryAsync(long userNum, InventoryLoadoutScope scope, CancellationToken ct);

    /// <summary>Commits the entire planned recipe or returns null when any ownership or reward prerequisite changed.</summary>
    ValueTask<InventoryLaboratoryResult?> TryLaboratoryAsync(InventoryLaboratoryCommand command, CancellationToken ct);
}
