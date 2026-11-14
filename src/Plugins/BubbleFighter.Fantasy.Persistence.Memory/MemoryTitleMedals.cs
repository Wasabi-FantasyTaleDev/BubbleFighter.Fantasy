using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Titles;

namespace BubbleFighter.Fantasy.Persistence.Memory;

public sealed partial class MemoryInventoryRepository : ITitleMedalStore
{
    private readonly TitleMedalLimits _titleMedalLimits = ValidatedTitleLimits(titleMedalLimits);

    /// <inheritdoc />
    public bool SupportsAcquisitionBonuses => true;

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<TitleMedalCount>> ReadAsync(long userNum, string catalogScope, CancellationToken ct)
    {
        RequireUserNum(userNum);
        InventoryLoadoutScope.ValidateKey(catalogScope);
        ct.ThrowIfCancellationRequested();
        var entry = table.Find(userNum);
        if (entry is null) return ValueTask.FromResult<IReadOnlyList<TitleMedalCount>>([]);
        lock (entry.Gate)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ReadTitleLedger(entry, catalogScope).Medals);
        }
    }

    /// <inheritdoc />
    public ValueTask<TitleMedalGrantResult> GrantAsync(TitleMedalGrantCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();
        ct.ThrowIfCancellationRequested();
        var entry = table.Find(command.UserNum) ?? throw new InvalidOperationException("The medal recipient does not exist.");
        lock (entry.Gate)
        {
            var prepared = PrepareTitleGrant(entry, command, _titleMedalLimits);
            ct.ThrowIfCancellationRequested();
            CommitTitleGrant(entry, command, prepared);
            return ValueTask.FromResult(new TitleMedalGrantResult(prepared.Applied, prepared.Ledger.Medals));
        }
    }

    /// <inheritdoc />
    public ValueTask<TitleClaimResult?> TryClaimAsync(TitleClaimCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();
        ct.ThrowIfCancellationRequested();
        var entry = table.Find(command.UserNum);
        if (entry is null) return ValueTask.FromResult<TitleClaimResult?>(null);
        lock (entry.Gate)
        {
            var acquisition = command.BonusGrants.Count != 0 || command.BonusMedals.Count != 0
                ? TitleAcquisitionReceipt.For(command) : null;
            var includeBonuses = acquisition is not null;
            if (acquisition is not null && entry.TitleAcquisitions.TryGetValue((command.Scope.Key, command.TitleId), out var previous))
            {
                acquisition.RequireSameAcquisition(previous);
                includeBonuses = false;
            }
            var ledger = ReadTitleLedger(entry, command.Scope.Key).Claim(command, _titleMedalLimits, includeBonuses);
            if (ledger is null) return ValueTask.FromResult<TitleClaimResult?>(null);
            var loadout = ReadLoadout(entry, command.Scope);
            var grant = command.Plan(ArticleRows(entry), loadout, entry.LastPurchaseSlotKey, includeBonuses);
            if (grant is null) return ValueTask.FromResult<TitleClaimResult?>(null);
            RequireTitleScopeCapacity(entry, command.Scope.Key);
            var allocate = InventoryMetadataPreparation.NeedsIdentity(command.UserNum, command.Scope, grant.Rows,
                command.ArticleInitialization, command.PetInitialization);
            if (allocate) { lock (table.PetAllocatorGate) return Commit(); }
            return Commit();

            ValueTask<TitleClaimResult?> Commit()
            {
                var lastId = table.LastPetEntitlementId;
                var lastArticle = table.LastPetArticleKey;
                var prepared = InventoryMetadataPreparation.Prepare(command.UserNum, command.Scope, grant.Rows, loadout,
                    command.ArticleInitialization, command.PetInitialization, () =>
                    {
                        if (!allocate || lastArticle < PetArticleKeys.First - 1 || lastArticle >= PetArticleKeys.Last)
                            throw new InvalidOperationException("Native Article allocation is exhausted or corrupt.");
                        return new(checked(++lastId), checked(++lastArticle));
                    });
                var inventory = InventorySnapshot.Project(prepared.Rows, prepared.Loadout, command.Scope);
                var result = new TitleClaimResult(inventory, ledger.Medals,
                    inventory.CharacterBag.Single(row => row.ItemId == command.GrantTerm.ItemId));
                CommitArticleRows(entry, command.Scope, prepared.Rows, prepared.Loadout, ct);
                entry.TitleMedals[command.Scope.Key] = ledger;
                if (includeBonuses) entry.TitleAcquisitions.Add((command.Scope.Key, command.TitleId), acquisition!);
                entry.LastPurchaseSlotKey = grant.LastSlotKey;
                if (allocate) { table.LastPetEntitlementId = lastId; table.LastPetArticleKey = lastArticle; }
                return ValueTask.FromResult<TitleClaimResult?>(result);
            }
        }
    }

    internal static (TitleMedalLedger Ledger, TitleMedalGrantReceipt Receipt, bool Applied) PrepareTitleGrant(
        PlayerEntry entry, TitleMedalGrantCommand command, TitleMedalLimits limits)
    {
        command.Validate();
        limits.Validate();
        if (entry.TitleMedals.Count > limits.MaxScopes)
            throw new InvalidDataException("Stored medal catalogue scopes exceed their configured capacity.");
        var receipt = TitleMedalGrantReceipt.For(command);
        var current = entry.TitleMedals.GetValueOrDefault(command.CatalogScope) ?? TitleMedalLedger.Empty;
        current.Validate(limits);
        if (entry.TitleReceipts.TryGetValue((command.CatalogScope, command.GrantKey), out var stored))
        {
            receipt.RequireSameEvent(stored);
            return (current, receipt, false);
        }
        var ledger = current.Credit(command, limits);
        if (!entry.TitleMedals.ContainsKey(command.CatalogScope) && entry.TitleMedals.Count >= limits.MaxScopes)
            throw new InvalidOperationException("The player's medal catalogue scope budget is exhausted.");
        return (ledger, receipt, true);
    }

    internal static void CommitTitleGrant(PlayerEntry entry, TitleMedalGrantCommand command,
        (TitleMedalLedger Ledger, TitleMedalGrantReceipt Receipt, bool Applied) prepared)
    {
        if (!prepared.Applied) return;
        entry.TitleMedals[command.CatalogScope] = prepared.Ledger;
        entry.TitleReceipts.Add((command.CatalogScope, command.GrantKey), prepared.Receipt);
    }

    private TitleMedalLedger ReadTitleLedger(PlayerEntry entry, string scope)
    {
        if (entry.TitleMedals.Count > _titleMedalLimits.MaxScopes)
            throw new InvalidDataException("Stored medal catalogue scopes exceed their configured capacity.");
        var ledger = entry.TitleMedals.GetValueOrDefault(scope) ?? TitleMedalLedger.Empty;
        ledger.Validate(_titleMedalLimits);
        return ledger;
    }

    private void RequireTitleScopeCapacity(PlayerEntry entry, string scope)
    {
        if (!entry.TitleMedals.ContainsKey(scope) && entry.TitleMedals.Count >= _titleMedalLimits.MaxScopes)
            throw new InvalidOperationException("The player's medal catalogue scope budget is exhausted.");
    }

    private static TitleMedalLimits ValidatedTitleLimits(TitleMedalLimits? limits)
    {
        limits ??= new();
        limits.Validate();
        return limits;
    }
}
