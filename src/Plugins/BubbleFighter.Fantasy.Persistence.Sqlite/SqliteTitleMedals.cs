using System.Globalization;
using Microsoft.Data.Sqlite;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Titles;

namespace BubbleFighter.Fantasy.Persistence.Sqlite;

internal sealed partial class SqliteRepositories : ITitleMedalStore
{
    // Schema 13 has no durable title acquisition receipt table.
    public bool SupportsAcquisitionBonuses => false;

    public ValueTask<IReadOnlyList<TitleMedalCount>> ReadAsync(long userNum, string catalogScope, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userNum);
        InventoryLoadoutScope.ValidateKey(catalogScope);
        return new(database.Read<IReadOnlyList<TitleMedalCount>>(db => db.State(userNum) is { } state
            ? ReadTitleLedger(state, catalogScope).Medals : [], ct));
    }

    public ValueTask<TitleMedalGrantResult> GrantAsync(TitleMedalGrantCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();
        return new(database.Write((db, transaction) =>
        {
            var state = db.RequireState(command.UserNum);
            var result = ApplyTitleGrant(db, transaction, state, command);
            if (result.Applied) db.Save(command.UserNum, state);
            return result;
        }, ct));
    }

    private TitleMedalGrantResult ApplyTitleGrant(SqliteDatabase db, SqliteTransaction transaction,
        SaveState state, TitleMedalGrantCommand command, bool requireExisting = false)
    {
        command.Validate();
        var receipt = TitleMedalGrantReceipt.For(command);
        var current = ReadTitleLedger(state, command.CatalogScope);
        using (var query = db.RawCommand("SELECT source,fingerprint FROM title_medal_receipts WHERE user_num=$user AND catalog_scope=$scope AND grant_key=$key",
            transaction, ("$user", command.UserNum), ("$scope", command.CatalogScope), ("$key", command.GrantKey)))
        using (var reader = query.ExecuteReader())
        {
            if (reader.Read())
            {
                receipt.RequireSameEvent(new(reader.GetString(0), command.GrantKey, reader.GetString(1)));
                return new TitleMedalGrantResult(false, current.Medals);
            }
        }
        if (requireExisting) throw new InvalidDataException("A settled round is missing its atomic medal receipt.");
        var ledger = current.Credit(command, database.Options.TitleMedals);
        RequireTitleScopeCapacity(state, command.CatalogScope);
        state.TitleMedals[command.CatalogScope] = ledger;
        if (db.RawExecute("INSERT INTO title_medal_receipts(user_num,catalog_scope,grant_key,source,fingerprint) VALUES($user,$scope,$key,$source,$fingerprint)",
            transaction, ("$user", command.UserNum), ("$scope", command.CatalogScope), ("$key", command.GrantKey),
            ("$source", receipt.Source), ("$fingerprint", receipt.Fingerprint)) != 1)
            throw new InvalidDataException("Medal grant receipt insert affected an invalid row count.");
        return new TitleMedalGrantResult(true, ledger.Medals);
    }

    public ValueTask<TitleClaimResult?> TryClaimAsync(TitleClaimCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();
        if (command.BonusGrants.Count != 0 || command.BonusMedals.Count != 0)
            throw new NotSupportedException("This SQLite schema does not support atomic title acquisition bonuses.");
        return new(database.Write<TitleClaimResult?>((db, transaction) =>
        {
            var state = db.RequireState(command.UserNum);
            var ledger = ReadTitleLedger(state, command.Scope.Key).Claim(command, database.Options.TitleMedals, false);
            if (ledger is null) return null;
            var loadout = ReadLoadout(state, command.Scope);
            var grant = command.Plan(state.Slots, loadout, state.LastPurchaseSlotKey, false);
            if (grant is null) return null;
            RequireTitleScopeCapacity(state, command.Scope.Key);
            var allocate = InventoryMetadataPreparation.NeedsIdentity(command.UserNum, command.Scope, grant.Rows,
                command.ArticleInitialization, command.PetInitialization);
            ulong lastId = 0, lastArticle = 0;
            if (allocate)
            {
                using var query = db.RawCommand("SELECT entitlement_id,article_key FROM pet_identity_allocator WHERE singleton=1", transaction);
                using var reader = query.ExecuteReader();
                if (!reader.Read()) throw new InvalidDataException("Shared Article allocator is missing.");
                lastId = ulong.Parse(reader.GetString(0), NumberStyles.None, CultureInfo.InvariantCulture);
                lastArticle = ulong.Parse(reader.GetString(1), NumberStyles.None, CultureInfo.InvariantCulture);
            }
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
            state.TitleMedals[command.Scope.Key] = ledger;
            state.Slots = prepared.Rows.ToList();
            state.Loadouts[command.Scope.Key] = prepared.Loadout;
            state.LastPurchaseSlotKey = grant.LastSlotKey;
            db.Save(command.UserNum, state);
            if (allocate && db.RawExecute("UPDATE pet_identity_allocator SET entitlement_id=$id,article_key=$article WHERE singleton=1", transaction,
                ("$id", lastId.ToString(CultureInfo.InvariantCulture)), ("$article", lastArticle.ToString(CultureInfo.InvariantCulture))) != 1)
                throw new InvalidDataException("Shared Article allocator disappeared during title acquisition.");
            return result;
        }, ct));
    }

    private TitleMedalLedger ReadTitleLedger(SaveState state, string scope)
    {
        var ledger = state.TitleMedals.GetValueOrDefault(scope) ?? TitleMedalLedger.Empty;
        ledger.Validate(database.Options.TitleMedals);
        return ledger;
    }

    private void RequireTitleScopeCapacity(SaveState state, string scope)
    {
        if (!state.TitleMedals.ContainsKey(scope) && state.TitleMedals.Count >= database.Options.TitleMedals.MaxScopes)
            throw new InvalidOperationException("The player's medal catalogue scope budget is exhausted.");
    }
}
