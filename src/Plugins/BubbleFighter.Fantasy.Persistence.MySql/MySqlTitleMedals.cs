using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Titles;
using SqlSugar;

namespace BubbleFighter.Fantasy.Persistence.MySql;

public sealed partial class MySqlInventoryRepository : ITitleMedalStore
{
    // The player row serializes one account; READ COMMITTED avoids cross-account gap locks when a ledger is first inserted.
    private const IsolationLevel TitleTransactionIsolation = IsolationLevel.ReadCommitted;
    private readonly TitleMedalLimits _titleMedalLimits = ValidatedTitleLimits(titleMedalLimits);
    private static readonly JsonSerializerOptions TitleMedalJson = new()
    {
        MaxDepth = 8,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <inheritdoc />
    public bool SupportsAcquisitionBonuses => true;

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<TitleMedalCount>> ReadAsync(long userNum, string catalogScope, CancellationToken ct)
    {
        RequireUserNum(userNum);
        InventoryLoadoutScope.ValidateKey(catalogScope);
        ct.ThrowIfCancellationRequested();
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        await using var transaction = await session.BeginAsync(TitleTransactionIsolation).ConfigureAwait(false);
        var owner = await MySqlPlayerLock.LockQuery(session.Db, userNum).ToListAsync(ct).ConfigureAwait(false);
        if (owner.Count == 0)
        {
            ct.ThrowIfCancellationRequested();
            await transaction.CommitAsync().ConfigureAwait(false);
            return [];
        }
        var (ledger, _) = await ReadTitleLedgerAsync(session.Db, userNum, catalogScope, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        await transaction.CommitAsync().ConfigureAwait(false);
        return ledger.Medals;
    }

    /// <inheritdoc />
    public async ValueTask<TitleMedalGrantResult> GrantAsync(TitleMedalGrantCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();
        ct.ThrowIfCancellationRequested();
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var db = session.Db;
        await using var transaction = await session.BeginWriteAsync(command.UserNum, authority, TitleTransactionIsolation, ct).ConfigureAwait(false);
        await MySqlPlayerLock.AcquireAsync(db, command.UserNum, ct).ConfigureAwait(false);
        var result = await ApplyTitleGrantInTransactionAsync(db, command, _titleMedalLimits, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        await transaction.CommitAsync().ConfigureAwait(false);
        return result;
    }

    internal static async ValueTask<TitleMedalGrantResult> ApplyTitleGrantInTransactionAsync(SqlSugarClient db,
        TitleMedalGrantCommand command, TitleMedalLimits limits, CancellationToken ct, bool requireExisting = false)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();
        limits.Validate();
        var (stored, exists) = await ReadTitleLedgerAsync(db, command.UserNum, command.CatalogScope, limits, ct).ConfigureAwait(false);
        var receipt = TitleMedalGrantReceipt.For(command);
        var previous = await db.Queryable<TitleMedalReceiptRow>()
            .Where(row => row.UserNum == command.UserNum && row.CatalogScope == command.CatalogScope && row.GrantKey == command.GrantKey)
            .Take(1).ToListAsync(ct).ConfigureAwait(false);
        if (previous.Count == 1)
        {
            receipt.RequireSameEvent(new(previous[0].Source, previous[0].GrantKey, previous[0].Fingerprint));
            ct.ThrowIfCancellationRequested();
            return new(false, stored.Medals);
        }
        if (requireExisting) throw new InvalidDataException("A settled round is missing its atomic medal receipt.");
        var ledger = stored.Credit(command, limits);
        await SaveTitleLedgerAsync(db, command.UserNum, command.CatalogScope, ledger, exists, limits, ct).ConfigureAwait(false);
        var inserted = await db.Insertable(new TitleMedalReceiptRow
        {
            UserNum = command.UserNum, CatalogScope = command.CatalogScope, GrantKey = receipt.GrantKey,
            Source = receipt.Source, Fingerprint = receipt.Fingerprint,
        }).ExecuteCommandAsync(ct).ConfigureAwait(false);
        if (inserted != 1) throw new InvalidOperationException("Medal grant receipt insert affected an invalid row count.");
        ct.ThrowIfCancellationRequested();
        return new(true, ledger.Medals);
    }

    /// <inheritdoc />
    public async ValueTask<TitleClaimResult?> TryClaimAsync(TitleClaimCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();
        ct.ThrowIfCancellationRequested();
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var db = session.Db;
        await using var transaction = await session.BeginWriteAsync(command.UserNum, authority, TitleTransactionIsolation, ct).ConfigureAwait(false);
        await MySqlPlayerLock.AcquireAsync(db, command.UserNum, ct).ConfigureAwait(false);
        var (storedLedger, ledgerExists) = await ReadTitleLedgerAsync(db, command.UserNum, command.Scope.Key, ct).ConfigureAwait(false);
        var acquisition = command.BonusGrants.Count != 0 || command.BonusMedals.Count != 0
            ? TitleAcquisitionReceipt.For(command) : null;
        var includeBonuses = acquisition is not null;
        if (acquisition is not null)
        {
            var prior = await db.Queryable<TitleAcquisitionReceiptRow>()
                .Where(row => row.UserNum == command.UserNum && row.CatalogScope == command.Scope.Key && row.TitleId == command.TitleId)
                .Take(1).ToListAsync(ct).ConfigureAwait(false);
            if (prior.Count == 1)
            {
                acquisition.RequireSameAcquisition(new(prior[0].TitleId, prior[0].StockId, prior[0].Fingerprint));
                includeBonuses = false;
            }
        }
        var ledger = storedLedger.Claim(command, _titleMedalLimits, includeBonuses);
        if (ledger is null) return null;
        var (loadout, loadoutExists) = await ReadLoadoutAsync(db, command.UserNum, command.Scope, ct).ConfigureAwait(false);
        var stored = await ReadOwnedInventoryRowsAsync(db, command.UserNum, ct).ConfigureAwait(false);
        var keys = await MySqlInventorySlotKeys.ReadAsync(db, command.UserNum, ct).ConfigureAwait(false);
        var grant = command.Plan(stored, loadout, keys.Highest, includeBonuses);
        if (grant is null) return null;
        var prepared = await PreparePetInventoryAsync(db, grant.Rows, loadout, command.Scope,
            command.PetInitialization, ct, command.ArticleInitialization).ConfigureAwait(false);
        // Preparation starts after the grant; comparison must retain the actual pre-transaction inventory.
        prepared = prepared with { Stored = stored };
        var inventory = InventorySnapshot.Project(prepared.Rows, prepared.Loadout, command.Scope);
        var result = new TitleClaimResult(inventory, ledger.Medals,
            inventory.CharacterBag.Single(row => row.ItemId == command.GrantTerm.ItemId));
        await CommitPetPreparationAsync(db, command.UserNum, command.Scope, prepared, prepared.Rows,
            prepared.Loadout, loadoutExists, ct).ConfigureAwait(false);
        await SaveTitleLedgerAsync(db, command.UserNum, command.Scope.Key, ledger, ledgerExists, ct).ConfigureAwait(false);
        await MySqlInventorySlotKeys.AdvanceAsync(db, command.UserNum, keys.Stored, grant.LastSlotKey, ct).ConfigureAwait(false);
        if (includeBonuses && await db.Insertable(new TitleAcquisitionReceiptRow
        {
            UserNum = command.UserNum, CatalogScope = command.Scope.Key, TitleId = command.TitleId,
            StockId = command.StockId, Fingerprint = acquisition!.Fingerprint,
        }).ExecuteCommandAsync(ct).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("Title acquisition receipt insert affected an invalid row count.");
        ct.ThrowIfCancellationRequested();
        await transaction.CommitAsync().ConfigureAwait(false);
        return result;
    }

    private ValueTask<(TitleMedalLedger Ledger, bool Exists)> ReadTitleLedgerAsync(SqlSugarClient db,
        long userNum, string scope, CancellationToken ct) => ReadTitleLedgerAsync(db, userNum, scope, _titleMedalLimits, ct);

    private ValueTask SaveTitleLedgerAsync(SqlSugarClient db, long userNum, string scope,
        TitleMedalLedger ledger, bool exists, CancellationToken ct) =>
        SaveTitleLedgerAsync(db, userNum, scope, ledger, exists, _titleMedalLimits, ct);

    private static async ValueTask<(TitleMedalLedger Ledger, bool Exists)> ReadTitleLedgerAsync(SqlSugarClient db,
        long userNum, string scope, TitleMedalLimits limits, CancellationToken ct)
    {
        var scopes = await db.Queryable<TitleMedalLedgerRow>().Where(row => row.UserNum == userNum)
            .Select(row => row.CatalogScope).Take(limits.MaxScopes + 1).TranLock(DbLockType.Wait)
            .ToListAsync(ct).ConfigureAwait(false);
        if (scopes.Count > limits.MaxScopes)
            throw new InvalidDataException("Stored medal catalogue scopes exceed their configured capacity.");
        if (!scopes.Contains(scope, StringComparer.Ordinal)) return (TitleMedalLedger.Empty, false);
        var rows = await db.Queryable<TitleMedalLedgerRow>()
            .Where(row => row.UserNum == userNum && row.CatalogScope == scope).Take(1).TranLock(DbLockType.Wait)
            .ToListAsync(ct).ConfigureAwait(false);
        return (DecodeTitleLedger(rows.Single().StateJson, limits), true);
    }

    private static async ValueTask SaveTitleLedgerAsync(SqlSugarClient db, long userNum, string scope,
        TitleMedalLedger ledger, bool exists, TitleMedalLimits limits, CancellationToken ct)
    {
        ledger.Validate(limits);
        var row = new TitleMedalLedgerRow { UserNum = userNum, CatalogScope = scope, StateJson = JsonSerializer.Serialize(ledger, TitleMedalJson) };
        if (!exists)
        {
            var count = await db.Queryable<TitleMedalLedgerRow>().Where(value => value.UserNum == userNum)
                .Select(value => value.CatalogScope).Take(limits.MaxScopes + 1).TranLock(DbLockType.Wait)
                .ToListAsync(ct).ConfigureAwait(false);
            if (count.Count >= limits.MaxScopes)
                throw new InvalidOperationException("The player's medal catalogue scope budget is exhausted.");
        }
        var affected = exists
            ? await db.Updateable(row).ExecuteCommandAsync(ct).ConfigureAwait(false)
            : await db.Insertable(row).ExecuteCommandAsync(ct).ConfigureAwait(false);
        if (affected != 1) throw new InvalidOperationException("Medal ledger write affected an invalid row count.");
    }

    internal static TitleMedalLedger DecodeTitleLedger(string json, TitleMedalLimits limits)
    {
        limits.Validate();
        if (json.Length > 64L * limits.MaxMedals + 128)
            throw new InvalidDataException("Stored medal ledger exceeds its serialized bound.");
        var ledger = JsonSerializer.Deserialize<TitleMedalLedger>(json, TitleMedalJson)
            ?? throw new InvalidDataException("Stored medal ledger is JSON null.");
        ledger.Validate(limits);
        return ledger;
    }

    private static TitleMedalLimits ValidatedTitleLimits(TitleMedalLimits? limits)
    {
        limits ??= new();
        limits.Validate();
        return limits;
    }
}

/// <summary>Maps one account's native catalogue medal balances.</summary>
[SugarTable("player_title_medals")]
internal sealed class TitleMedalLedgerRow
{
    [SugarColumn(ColumnName = "user_num", IsPrimaryKey = true)]
    public long UserNum { get; set; }

    [SugarColumn(ColumnName = "catalog_scope", IsPrimaryKey = true)]
    public string CatalogScope { get; set; } = "";

    [SugarColumn(ColumnName = "state_json")]
    public string StateJson { get; set; } = "";
}

/// <summary>Maps one applied medal credit event, keyed by account, catalogue scope and event identity.</summary>
[SugarTable("player_title_medal_receipts")]
internal sealed class TitleMedalReceiptRow
{
    [SugarColumn(ColumnName = "user_num", IsPrimaryKey = true)]
    public long UserNum { get; set; }

    [SugarColumn(ColumnName = "catalog_scope", IsPrimaryKey = true)]
    public string CatalogScope { get; set; } = "";

    [SugarColumn(ColumnName = "grant_key", IsPrimaryKey = true)]
    public string GrantKey { get; set; } = "";

    [SugarColumn(ColumnName = "source")]
    public string Source { get; set; } = "";

    [SugarColumn(ColumnName = "fingerprint")]
    public string Fingerprint { get; set; } = "";
}

/// <summary>Maps one title acquisition whose bonus receipt survives inventory deletion and expiry.</summary>
[SugarTable("player_title_acquisition_receipts")]
internal sealed class TitleAcquisitionReceiptRow
{
    [SugarColumn(ColumnName = "user_num", IsPrimaryKey = true)]
    public long UserNum { get; set; }

    [SugarColumn(ColumnName = "catalog_scope", IsPrimaryKey = true)]
    public string CatalogScope { get; set; } = "";

    [SugarColumn(ColumnName = "title_id", IsPrimaryKey = true)]
    public int TitleId { get; set; }

    [SugarColumn(ColumnName = "stock_id")]
    public int StockId { get; set; }

    [SugarColumn(ColumnName = "fingerprint")]
    public string Fingerprint { get; set; } = "";
}
