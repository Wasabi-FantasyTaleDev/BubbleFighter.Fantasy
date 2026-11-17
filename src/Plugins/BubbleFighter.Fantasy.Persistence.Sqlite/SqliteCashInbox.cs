using BubbleFighter.Fantasy.Contracts.CashInbox;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Shop;

namespace BubbleFighter.Fantasy.Persistence.Sqlite;

internal sealed partial class SqliteRepositories : ICashInboxStore
{
    public ValueTask<CashInboxGrantResult> GrantAsync(CashInboxGrantCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();

        return new(database.Write<CashInboxGrantResult>((db, _) =>
        {
            if (db.State(command.UserNum) is null)
                return new(CashInboxGrantOutcome.OwnerMissing, null);

            if (command.ClaimKey is not null
                && db.Orm.Queryable<CashInboxClaimRow>()
                    .Any(r => r.Profile == command.ProfileId
                        && r.UserNum == command.UserNum
                        && r.ClaimKey == command.ClaimKey))
            {
                return new(CashInboxGrantOutcome.AlreadyClaimed, null);
            }

            var now = command.CreatedAtUnixMilliseconds;
            var activeCount = db.Orm.Queryable<CashInboxEntryRow>()
                .Where(r => r.Profile == command.ProfileId
                    && r.UserNum == command.UserNum
                    && r.State == (int)CashInboxEntryState.Active
                    && (r.ExpiresAtMs == 0 || r.ExpiresAtMs > now))
                .Count();

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

            var slotId = db.Orm.Insertable(row).ExecuteReturnBigIdentity();
            if (slotId is <= 0 or > CashInboxLimits.MaxCashSlotId)
                throw new InvalidOperationException("SQLite cash inbox slot identifiers are exhausted or corrupt.");

            row.CashSlotId = slotId;

            if (command.ClaimKey is not null)
            {
                db.Orm.Insertable(new CashInboxClaimRow
                {
                    Profile = command.ProfileId,
                    UserNum = command.UserNum,
                    ClaimKey = command.ClaimKey,
                    CashSlotId = slotId,
                }).ExecuteCommand();
            }

            return new(CashInboxGrantOutcome.Granted, ToRecord(row));
        }, ct));
    }

    public ValueTask<IReadOnlyList<CashInboxEntryRecord>> ListActiveAsync(
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

        return new(database.Read<IReadOnlyList<CashInboxEntryRecord>>(db =>
        {
            var rows = db.Orm.Queryable<CashInboxEntryRow>()
                .Where(r => r.Profile == profileId
                    && r.UserNum == userNum
                    && r.State == (int)CashInboxEntryState.Active
                    && (r.ExpiresAtMs == 0 || r.ExpiresAtMs > nowUnixMilliseconds))
                .OrderBy(r => r.CashSlotId)
                .Take(maxEntries)
                .ToList();
            return rows.Select(ToRecord).ToArray();
        }, ct));
    }

    public ValueTask<CashInboxEntryRecord?> FindBySlotIdAsync(string profileId, long cashSlotId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        if (profileId.Length > 32)
            throw new ArgumentOutOfRangeException(nameof(profileId), "Profile identifier exceeds maximum length.");
        if (cashSlotId is <= 0 or > CashInboxLimits.MaxCashSlotId)
            throw new ArgumentOutOfRangeException(nameof(cashSlotId));

        return new(database.Read(db =>
        {
            var row = db.Orm.Queryable<CashInboxEntryRow>()
                .Where(r => r.Profile == profileId && r.CashSlotId == cashSlotId)
                .First();
            return row is null ? null : ToRecord(row);
        }, ct));
    }

    public ValueTask<CashInboxPickupResult> PickUpAsync(CashInboxPickupCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();

        return new(database.Write<CashInboxPickupResult>((db, _) =>
        {
            var row = db.Orm.Queryable<CashInboxEntryRow>()
                .Where(r => r.Profile == command.ProfileId && r.CashSlotId == command.CashSlotId)
                .First();
            if (row is null || row.State != (int)CashInboxEntryState.Active)
                return new(CashInboxPickupOutcome.NotFound, row is null ? null : ToRecord(row), null);

            var record = ToRecord(row);
            if (row.ExpiresAtMs > 0 && row.ExpiresAtMs <= command.PickedUpAtUnixMilliseconds)
            {
                db.Orm.Updateable<CashInboxEntryRow>()
                    .SetColumns(r => new CashInboxEntryRow { State = (int)CashInboxEntryState.Expired })
                    .Where(r => r.Profile == command.ProfileId
                        && r.CashSlotId == command.CashSlotId
                        && r.State == (int)CashInboxEntryState.Active)
                    .ExecuteCommand();
                return new(CashInboxPickupOutcome.NotFound, record with { State = CashInboxEntryState.Expired }, null);
            }

            if (row.UserNum != command.UserNum)
                return new(CashInboxPickupOutcome.OwnerMismatch, record, null);

            var state = db.RequireState(command.UserNum);
            var currentBag = state.Slots.Where(s => s.Bag == InventoryBag.CharacterBag).ToArray();
            var plan = command.PlanCharacterBagGrants(currentBag, state.LastPurchaseSlotKey);
            if (plan.Outcome != CashInboxPickupOutcome.PickedUp)
                return new(plan.Outcome, record, null);

            var walletAfter = command.ResolveWalletAfter(state.Wallet);
            if (walletAfter is null)
                return new(CashInboxPickupOutcome.Failed, record, null);

            var changed = db.Orm.Updateable<CashInboxEntryRow>()
                .SetColumns(r => new CashInboxEntryRow { State = (int)CashInboxEntryState.PickedUp })
                .Where(r => r.Profile == command.ProfileId
                    && r.CashSlotId == command.CashSlotId
                    && r.State == (int)CashInboxEntryState.Active)
                .ExecuteCommand();
            if (changed != 1)
                throw new InvalidDataException("Cash inbox pickup transition affected an invalid row count.");

            var otherBags = state.Slots.Where(s => s.Bag != InventoryBag.CharacterBag).ToList();
            state.Slots = [.. otherBags, .. plan.BagAfter];
            state.LastPurchaseSlotKey = plan.HighestSlotKey;
            state.Wallet = walletAfter;
            db.Save(command.UserNum, state);

            return new(
                CashInboxPickupOutcome.PickedUp,
                record with { State = CashInboxEntryState.PickedUp },
                new ShopPurchaseResult(walletAfter, plan.AppliedSlots));
        }, ct));
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
