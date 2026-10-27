using System.Linq.Expressions;
using BubbleFighter.Fantasy.Contracts.Presents;
using BubbleFighter.Fantasy.Contracts.Shop;

namespace BubbleFighter.Fantasy.Persistence.Sqlite;

/// <summary>
/// The save's gift fragment. Gifts live in the presents table (schema version 4; once-only system
/// claims in present_claims from version 5), every operation runs in one transaction of the owned
/// connection — charge, list-size check and gift row are one change — and a delivery marks the gift
/// inside the purchase transaction (<see cref="PurchaseAsync"/>).
/// </summary>
// Gifts live in the save's presents table (schema version 4; present_claims from version 5). Every operation runs in one transaction of the owned
// connection, so a charge, a list-size check and the gift row are one change; a delivery marks the gift inside the
// purchase transaction (SqliteRepositories.PurchaseAsync).
internal sealed partial class SqliteRepositories : IPresentStore
{
    public ValueTask<PresentPlayer?> FindPlayerAsync(string nickname, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(nickname);
        return new(database.Read(db =>
        {
            var row = db.Orm.Queryable<PlayerRow>().Where(player => player.Nickname == nickname).ToList().FirstOrDefault();
            return row is null ? null : new PresentPlayer(row.UserNum, row.Nickname);
        }, ct));
    }

    public ValueTask<PresentSendResult> SendAsync(PresentSendCommand command, CancellationToken ct)
    {
        command.Validate();
        return new(database.Write((db, _) =>
        {
            if (db.Orm.Queryable<PlayerRow>().Where(player => player.UserNum == command.Receiver.UserNum).Count() != 1)
                return new PresentSendResult(PresentSendOutcome.ReceiverMissing, null, null);
            if (VisibleCount(db, command.ProfileId, command.Receiver.UserNum, PresentBox.Received) >= command.MaxPerPlayer)
                return new PresentSendResult(PresentSendOutcome.ReceiverBoxFull, null, null);
            if (VisibleCount(db, command.ProfileId, command.Sender.UserNum, PresentBox.Sent) >= command.MaxPerPlayer)
                return new PresentSendResult(PresentSendOutcome.SenderBoxFull, null, null);

            var state = db.RequireState(command.Sender.UserNum);
            var wallet = state.Wallet ?? command.SeedWallet;
            var cash = command.MoneyType == "cash";
            var balance = cash ? wallet.Cash : wallet.Lucci;
            if (balance < command.Price)
                return new PresentSendResult(PresentSendOutcome.InsufficientFunds, null, null);
            state.Wallet = cash ? wallet with { Cash = balance - command.Price } : wallet with { Lucci = balance - command.Price };
            state.Progress ??= new(wallet.UserNum, state.Level, 0);
            var audit = new ShopPurchaseRecord(command.Sender.UserNum, command.StockSetId, command.ItemId,
                command.MoneyType, command.Price, 0, command.StockKey);
            state.Purchases = state.Purchases.Prepend(audit).Take(database.Options.MaxPurchaseHistoryPerPlayer).ToList();
            db.Save(command.Sender.UserNum, state);

            var id = db.Orm.Insertable(new PresentRow
            {
                ProfileId = command.ProfileId, SenderUserNum = command.Sender.UserNum, SenderNickname = command.Sender.Nickname,
                ReceiverUserNum = command.Receiver.UserNum, ReceiverNickname = command.Receiver.Nickname, StockKey = command.StockKey,
                Message = command.Message, SentAt = command.SentAtUnixMilliseconds, State = 0, SenderHidden = 0, ReceiverHidden = 0,
            }).ExecuteReturnBigIdentity();
            var present = new PresentRecord(id, command.ProfileId, command.Sender.UserNum, command.Sender.Nickname,
                command.Receiver.UserNum, command.Receiver.Nickname, command.StockKey, command.Message,
                command.SentAtUnixMilliseconds, PresentState.Pending);
            return new PresentSendResult(PresentSendOutcome.Sent, present, state.Wallet);
        }, ct));
    }

    public ValueTask<PresentRewardResult> GrantRewardAsync(PresentRewardCommand command, CancellationToken ct)
    {
        command.Validate();
        return new(database.Write((db, transaction) =>
        {
            var nickname = db.Orm.Queryable<PlayerRow>().Where(player => player.UserNum == command.ReceiverUserNum)
                .ToList().FirstOrDefault()?.Nickname;
            if (nickname is null)
                return new PresentRewardResult(PresentRewardOutcome.ReceiverMissing, []);
            // INSERT OR IGNORE stays raw: SqlSugar has no insert-ignore, and the primary key makes the claim once-only.
            if (db.RawExecute("INSERT OR IGNORE INTO present_claims(profile,user_num,claim_key,claimed_at) VALUES($profile,$user,$key,$at)",
                    transaction, ("$profile", command.ProfileId), ("$user", command.ReceiverUserNum),
                    ("$key", command.ClaimKey), ("$at", command.SentAtUnixMilliseconds)) != 1)
                return new PresentRewardResult(PresentRewardOutcome.AlreadyClaimed, []);
            var granted = new List<PresentRecord>(command.StockKeys.Count);
            foreach (var key in command.StockKeys)
            {
                // The system has no sent list, so the sender's view starts hidden.
                var id = db.Orm.Insertable(new PresentRow
                {
                    ProfileId = command.ProfileId, SenderUserNum = PresentLimits.SystemSenderUserNum, SenderNickname = command.SenderNickname,
                    ReceiverUserNum = command.ReceiverUserNum, ReceiverNickname = nickname, StockKey = key, Message = command.Message,
                    SentAt = command.SentAtUnixMilliseconds, State = 0, SenderHidden = 1, ReceiverHidden = 0,
                }).ExecuteReturnBigIdentity();
                if (id is <= 0 or > PresentLimits.MaxId)
                    throw new InvalidOperationException("Gift identities have exhausted the client's unsigned word range.");
                granted.Add(new PresentRecord(id, command.ProfileId, PresentLimits.SystemSenderUserNum, command.SenderNickname,
                    command.ReceiverUserNum, nickname, key, command.Message, command.SentAtUnixMilliseconds,
                    PresentState.Pending) { SenderHidden = true });
            }
            return new PresentRewardResult(PresentRewardOutcome.Granted, granted);
        }, ct));
    }

    public ValueTask<PresentPage> ListReceivedAsync(string profileId, long userNum, int start, int count, CancellationToken ct) =>
        List(profileId, userNum, PresentBox.Received, start, count, ct);

    public ValueTask<PresentPage> ListSentAsync(string profileId, long userNum, int start, int count, CancellationToken ct) =>
        List(profileId, userNum, PresentBox.Sent, start, count, ct);

    private ValueTask<PresentPage> List(string profileId, long userNum, PresentBox box, int start, int count, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, PresentLimits.MaxPageCount);
        return new(database.Read(db =>
        {
            var total = VisibleCount(db, profileId, userNum, box);
            var rows = db.Orm.Queryable<PresentRow>().Where(BoxFilter(profileId, userNum, box))
                .OrderByDescending(row => row.Id).Skip(start).Take(count).ToList();
            return new PresentPage(total, rows.Select(ToRecord).ToList());
        }, ct));
    }

    public ValueTask<int> CountPendingAsync(string profileId, long userNum, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        return new(database.Read(db => db.Orm.Queryable<PresentRow>().Where(row => row.ProfileId == profileId
            && row.ReceiverUserNum == userNum && row.ReceiverHidden == 0 && row.State == 0).Count(), ct));
    }

    public ValueTask<PresentRecord?> FindReceivedAsync(string profileId, long userNum, long id, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        return new(database.Read(db =>
        {
            var row = db.Orm.Queryable<PresentRow>().Where(present => present.Id == id && present.ProfileId == profileId
                && present.ReceiverUserNum == userNum && present.ReceiverHidden == 0).ToList().FirstOrDefault();
            return row is null ? null : ToRecord(row);
        }, ct));
    }

    public ValueTask<bool> DiscardAsync(string profileId, long userNum, long id, CancellationToken ct) =>
        Change(profileId, id, row => new PresentRow { State = 2, ReceiverHidden = 1 },
            row => row.Id == id && row.ProfileId == profileId && row.ReceiverUserNum == userNum && row.ReceiverHidden == 0 && row.State == 0, ct);

    public ValueTask<bool> DeleteReceivedAsync(string profileId, long userNum, long id, CancellationToken ct) =>
        Change(profileId, id, row => new PresentRow { ReceiverHidden = 1 },
            row => row.Id == id && row.ProfileId == profileId && row.ReceiverUserNum == userNum && row.ReceiverHidden == 0, ct);

    public ValueTask<bool> DeleteSentAsync(string profileId, long userNum, long id, CancellationToken ct) =>
        Change(profileId, id, row => new PresentRow { SenderHidden = 1 },
            row => row.Id == id && row.ProfileId == profileId && row.SenderUserNum == userNum && row.SenderHidden == 0, ct);

    // A gift both sides have hidden is removed, so the table keeps no row nobody can reach.
    private ValueTask<bool> Change(string profileId, long id, Expression<Func<PresentRow, PresentRow>> set,
        Expression<Func<PresentRow, bool>> where, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        return new(database.Write((db, _) =>
        {
            var changed = db.Orm.Updateable<PresentRow>().SetColumns(set).Where(where).ExecuteCommand() == 1;
            if (changed)
                db.Orm.Deleteable<PresentRow>().Where(row => row.Id == id && row.SenderHidden == 1 && row.ReceiverHidden == 1).ExecuteCommand();
            return changed;
        }, ct));
    }

    // Marks a gift taken inside the purchase transaction that delivers it; nothing is changed when it is not pending.
    private static void AcceptPresent(SqliteDatabase db, long id, long userNum)
    {
        if (db.Orm.Updateable<PresentRow>().SetColumns(row => new PresentRow { State = 1 })
                .Where(row => row.Id == id && row.ReceiverUserNum == userNum && row.ReceiverHidden == 0 && row.State == 0)
                .ExecuteCommand() != 1)
            throw new PresentUnavailableException(id);
    }

    private static int VisibleCount(SqliteDatabase db, string profileId, long userNum, PresentBox box) =>
        db.Orm.Queryable<PresentRow>().Where(BoxFilter(profileId, userNum, box)).Count();

    // The box is the gifts a player can still see: received ones by receiver, sent ones by sender, each hidden per side.
    private static Expression<Func<PresentRow, bool>> BoxFilter(string profileId, long userNum, PresentBox box) =>
        box == PresentBox.Received
            ? row => row.ProfileId == profileId && row.ReceiverUserNum == userNum && row.ReceiverHidden == 0
            : row => row.ProfileId == profileId && row.SenderUserNum == userNum && row.SenderHidden == 0;

    private static PresentRecord ToRecord(PresentRow row) => new(row.Id, row.ProfileId, row.SenderUserNum,
        row.SenderNickname, row.ReceiverUserNum, row.ReceiverNickname, row.StockKey, row.Message, row.SentAt,
        (PresentState)row.State)
    {
        SenderHidden = row.SenderHidden != 0,
        ReceiverHidden = row.ReceiverHidden != 0,
    };
}
