using BubbleFighter.Fantasy.Abstractions.Persistence;
using BubbleFighter.Fantasy.Contracts.CashInbox;
using BubbleFighter.Fantasy.Contracts.Cluster;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Contracts.Wallet;
using SqlSugar;

namespace BubbleFighter.Fantasy.Persistence.MySql;

/// <summary>
/// Durable MySQL implementation of <see cref="ICashInboxStore"/> backed by <c>cash_inbox_entries</c>
/// and <c>cash_inbox_claims</c> (migration 0062). Pickups acquire locks in the canonical player -&gt; wallet -&gt;
/// character bag -&gt; cash inbox order so concurrent operations never deadlock.
/// </summary>
public sealed class MySqlCashInboxStore(
    IDbConnectionFactory connections,
    IAccountMutationFence? authority = null) : ICashInboxStore
{
    /// <inheritdoc />
    public async ValueTask<CashInboxGrantResult> GrantAsync(CashInboxGrantCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();
        ct.ThrowIfCancellationRequested();

        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        await using var tx = await session.BeginWriteAsync(command.UserNum, authority, ct).ConfigureAwait(false);
        var db = session.Db;

        var locked = await MySqlPlayerLock.LockQuery(db, command.UserNum).ToListAsync(ct).ConfigureAwait(false);
        if (locked.Count == 0)
            return new(CashInboxGrantOutcome.OwnerMissing, null);

        if (command.ClaimKey is not null
            && await ClaimQuery(db, command.ProfileId, command.UserNum, command.ClaimKey).AnyAsync(ct).ConfigureAwait(false))
        {
            return new(CashInboxGrantOutcome.AlreadyClaimed, null);
        }

        var now = command.CreatedAtUnixMilliseconds;
        var activeCount = await ActiveOwnerQuery(db, command.ProfileId, command.UserNum, now)
            .CountAsync(ct).ConfigureAwait(false);
        if (!command.IgnoreInboxLimit && activeCount >= command.MaxEntriesPerPlayer)
            return new(CashInboxGrantOutcome.BoxFull, null);

        var row = new CashInboxEntryRow
        {
            Profile = command.ProfileId,
            UserNum = command.UserNum,
            StockKey = command.StockKey,
            ItemKey = command.ItemKey,
            Quantity = command.Quantity,
            Days = command.Days,
            SecondaryId = command.SecondaryId,
            Note = command.Note,
            CreatedAtMs = command.CreatedAtUnixMilliseconds,
            ExpiresAtMs = command.ExpiresAtUnixMilliseconds,
            State = (int)CashInboxEntryState.Active,
        };

        var slotId = await db.Insertable(row).ExecuteReturnBigIdentityAsync().ConfigureAwait(false);
        if (slotId is <= 0 or > CashInboxLimits.MaxCashSlotId)
            throw new InvalidOperationException("MySQL cash inbox slot identifiers are exhausted or corrupt.");

        row.CashSlotId = slotId;

        if (command.ClaimKey is not null)
        {
            await db.Insertable(new CashInboxClaimRow
            {
                Profile = command.ProfileId,
                UserNum = command.UserNum,
                ClaimKey = command.ClaimKey,
                CashSlotId = slotId,
            }).ExecuteCommandAsync(ct).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
        await tx.CommitAsync().ConfigureAwait(false);
        return new(CashInboxGrantOutcome.Granted, ToRecord(row));
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<CashInboxEntryRecord>> ListActiveAsync(
        string profileId,
        long userNum,
        int maxEntries,
        long nowUnixMilliseconds,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        if (profileId.Length > 32)
            throw new ArgumentOutOfRangeException(nameof(profileId), "Profile identifier exceeds maximum length.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userNum);
        if (maxEntries is < 1 or > InventoryLimits.MaxCashInventorySlots)
            throw new ArgumentOutOfRangeException(nameof(maxEntries));
        ArgumentOutOfRangeException.ThrowIfNegative(nowUnixMilliseconds);
        ct.ThrowIfCancellationRequested();

        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var rows = await ActiveOwnerQuery(session.Db, profileId, userNum, nowUnixMilliseconds)
            .OrderBy(r => r.CashSlotId)
            .Take(maxEntries)
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(ToRecord).ToArray();
    }

    /// <inheritdoc />
    public async ValueTask<CashInboxEntryRecord?> FindBySlotIdAsync(string profileId, long cashSlotId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        if (profileId.Length > 32)
            throw new ArgumentOutOfRangeException(nameof(profileId), "Profile identifier exceeds maximum length.");
        if (cashSlotId is <= 0 or > CashInboxLimits.MaxCashSlotId)
            throw new ArgumentOutOfRangeException(nameof(cashSlotId));
        ct.ThrowIfCancellationRequested();

        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var row = await SlotQuery(session.Db, profileId, cashSlotId).FirstAsync(ct).ConfigureAwait(false);
        return row is null ? null : ToRecord(row);
    }

    /// <inheritdoc />
    public async ValueTask<CashInboxPickupResult> PickUpAsync(CashInboxPickupCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();
        ct.ThrowIfCancellationRequested();

        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var preview = await SlotQuery(session.Db, command.ProfileId, command.CashSlotId)
            .FirstAsync(ct).ConfigureAwait(false);
        if (preview is null || preview.State != (int)CashInboxEntryState.Active)
            return new(CashInboxPickupOutcome.NotFound, preview is null ? null : ToRecord(preview), null);

        var ownerToLock = preview.UserNum;
        if (ownerToLock != command.UserNum
            && (preview.ExpiresAtMs == 0 || preview.ExpiresAtMs > command.PickedUpAtUnixMilliseconds))
        {
            return new(CashInboxPickupOutcome.OwnerMismatch, ToRecord(preview), null);
        }

        await using var tx = await session.BeginWriteAsync(ownerToLock, authority, ct).ConfigureAwait(false);
        var db = session.Db;
        await MySqlPlayerLock.AcquireAsync(db, ownerToLock, ct).ConfigureAwait(false);

        WalletRow? wallet = null;
        if (ownerToLock == command.UserNum)
        {
            var seed = new WalletRow
            {
                UserNum = command.UserNum,
                Lucci = command.SeedWallet.Lucci,
                Cash = command.SeedWallet.Cash,
            };
            await SugarSql.UpsertAsync(db, seed, [nameof(WalletRow.UpdatedAt)], [nameof(WalletRow.UserNum)], [], ct)
                .ConfigureAwait(false);
            var wallets = await db.Queryable<WalletRow>().Where(w => w.UserNum == command.UserNum).Take(1)
                .TranLock(DbLockType.Wait).ToListAsync(ct).ConfigureAwait(false);
            if (wallets.Count != 1)
                throw new InvalidOperationException($"player_wallets row for user_num {command.UserNum} is missing after seed insert.");
            wallet = wallets[0];
        }

        var bagRows = ownerToLock == command.UserNum
            ? await CharacterBagQuery(db, command.UserNum).TranLock(DbLockType.Wait).ToListAsync(ct).ConfigureAwait(false)
            : [];

        var row = await SlotQuery(db, command.ProfileId, command.CashSlotId)
            .TranLock(DbLockType.Wait).FirstAsync(ct).ConfigureAwait(false);
        if (row is null || row.State != (int)CashInboxEntryState.Active)
            return new(CashInboxPickupOutcome.NotFound, row is null ? null : ToRecord(row), null);

        var record = ToRecord(row);
        if (row.ExpiresAtMs > 0 && row.ExpiresAtMs <= command.PickedUpAtUnixMilliseconds)
        {
            var expiredRows = await StateUpdate(db, command.ProfileId, command.CashSlotId, CashInboxEntryState.Expired)
                .ExecuteCommandAsync(ct).ConfigureAwait(false);
            if (expiredRows != 1)
                throw new InvalidOperationException($"Cash inbox expiry transition for slot {command.CashSlotId} updated {expiredRows} rows.");
            ct.ThrowIfCancellationRequested();
            await tx.CommitAsync().ConfigureAwait(false);
            return new(CashInboxPickupOutcome.NotFound, record with { State = CashInboxEntryState.Expired }, null);
        }

        if (row.UserNum != command.UserNum)
            return new(CashInboxPickupOutcome.OwnerMismatch, record, null);

        var keys = await MySqlInventorySlotKeys.ReadAsync(db, command.UserNum, ct).ConfigureAwait(false);
        var currentBag = bagRows.Select(MySqlInventoryRepository.ToRecord).ToArray();
        var plan = command.PlanCharacterBagGrants(currentBag, keys.Highest);
        if (plan.Outcome != CashInboxPickupOutcome.PickedUp)
            return new(plan.Outcome, record, null);

        var walletAfter = command.ResolveWalletAfter(new PlayerWallet(wallet!.UserNum, wallet.Lucci, wallet.Cash));
        if (walletAfter is null)
            return new(CashInboxPickupOutcome.Failed, record, null);

        var updated = await StateUpdate(db, command.ProfileId, command.CashSlotId, CashInboxEntryState.PickedUp)
            .ExecuteCommandAsync(ct).ConfigureAwait(false);
        if (updated != 1)
            throw new InvalidOperationException($"Cash inbox pickup transition for slot {command.CashSlotId} updated {updated} rows.");

        foreach (var renewed in plan.UpdatedSlots)
        {
            var affected = await MySqlShopPurchaseStore.SlotUpdate(db, renewed).ExecuteCommandAsync(ct).ConfigureAwait(false);
            if (affected != 1)
                throw new InvalidOperationException($"Cash inbox pickup slot update for item {renewed.ItemId} updated {affected} rows.");
        }

        if (plan.AddedSlots.Count > 0)
        {
            var nextOrdinal = bagRows.Count == 0 ? 0 : bagRows.Max(r => r.Ordinal) + 1;
            var inserts = plan.AddedSlots.Select(slot => MySqlInventoryRepository.ToRow(slot, nextOrdinal++)).ToArray();
            var inserted = await db.Insertable(inserts).ExecuteCommandAsync(ct).ConfigureAwait(false);
            if (inserted != inserts.Length)
                throw new InvalidOperationException($"Cash inbox pickup bag insert wrote {inserted} rows; expected {inserts.Length}.");
        }

        await MySqlInventorySlotKeys.AdvanceAsync(db, command.UserNum, keys.Stored, plan.HighestSlotKey, ct).ConfigureAwait(false);

        if (command.LucciReward > 0)
            await MySqlShopPurchaseStore.ChargeUpdate(db, walletAfter).ExecuteCommandAsync(ct).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();
        await tx.CommitAsync().ConfigureAwait(false);

        return new(
            CashInboxPickupOutcome.PickedUp,
            record with { State = CashInboxEntryState.PickedUp },
            new ShopPurchaseResult(walletAfter, plan.AppliedSlots));
    }

    internal static ISugarQueryable<CashInboxClaimRow> ClaimQuery(
        SqlSugarClient db, string profileId, long userNum, string claimKey) =>
        db.Queryable<CashInboxClaimRow>()
            .Where(r => r.Profile == profileId && r.UserNum == userNum && r.ClaimKey == claimKey);

    internal static ISugarQueryable<CashInboxEntryRow> ActiveOwnerQuery(
        SqlSugarClient db, string profileId, long userNum, long nowMs) =>
        db.Queryable<CashInboxEntryRow>()
            .Where(r => r.Profile == profileId
                && r.UserNum == userNum
                && r.State == (int)CashInboxEntryState.Active
                && (r.ExpiresAtMs == 0 || r.ExpiresAtMs > nowMs));

    internal static ISugarQueryable<CashInboxEntryRow> SlotQuery(
        SqlSugarClient db, string profileId, long cashSlotId) =>
        db.Queryable<CashInboxEntryRow>()
            .Where(r => r.Profile == profileId && r.CashSlotId == cashSlotId)
            .Take(1);

    internal static ISugarQueryable<InventorySlotRow> CharacterBagQuery(SqlSugarClient db, long userNum)
    {
        var bagId = (int)InventoryBag.CharacterBag;
        return db.Queryable<InventorySlotRow>()
            .Where(s => s.UserNum == userNum && s.Bag == bagId)
            .OrderBy(s => s.Ordinal)
            .Take(InventoryLimits.MaxInventorySlotsPerBag);
    }

    internal static IUpdateable<CashInboxEntryRow> StateUpdate(
        SqlSugarClient db, string profileId, long cashSlotId, CashInboxEntryState nextState)
    {
        var next = (int)nextState;
        var active = (int)CashInboxEntryState.Active;
        return db.Updateable<CashInboxEntryRow>()
            .SetColumns(r => r.State == next)
            .Where(r => r.Profile == profileId && r.CashSlotId == cashSlotId && r.State == active);
    }

    private static CashInboxEntryRecord ToRecord(CashInboxEntryRow row) =>
        new(
            row.CashSlotId,
            row.Profile,
            row.UserNum,
            row.StockKey,
            row.ItemKey,
            row.Quantity,
            row.Days,
            row.Note,
            row.CreatedAtMs,
            row.ExpiresAtMs,
            (CashInboxEntryState)row.State)
        {
            SecondaryId = row.SecondaryId,
        };
}
