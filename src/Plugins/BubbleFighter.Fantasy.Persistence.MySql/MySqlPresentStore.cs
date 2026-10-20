using BubbleFighter.Fantasy.Abstractions.Persistence;
using BubbleFighter.Fantasy.Contracts.Presents;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Contracts.Wallet;
using SqlSugar;

namespace BubbleFighter.Fantasy.Persistence.MySql;

/// <summary>
/// Keeps gifts in <c>presents</c> (migration 0029). A send is one transaction that takes the row locks of both players
/// (lower user number first, so two opposite gifts cannot deadlock) before it counts either list, charges the sender
/// and inserts the gift; a delivery marks the gift taken inside the purchase transaction
/// (<see cref="MySqlShopPurchaseStore"/>).
/// </summary>
public sealed class MySqlPresentStore(IDbConnectionFactory connections) : IPresentStore
{
    private const byte Pending = (byte)PresentState.Pending;

    /// <inheritdoc />
    public async ValueTask<PresentPlayer?> FindPlayerAsync(string nickname, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(nickname);
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        // players.nickname is utf8mb4_unicode_ci, so the comparison ignores case like the unique key does.
        var found = await session.Db.Queryable<PresentPlayerRow>().Where(p => p.Nickname == nickname).Take(1)
            .ToListAsync(ct).ConfigureAwait(false);
        return found.Count == 0 ? null : new(found[0].UserNum, found[0].Nickname);
    }

    /// <inheritdoc />
    public async ValueTask<PresentSendResult> SendAsync(PresentSendCommand command, CancellationToken ct)
    {
        command.Validate();
        var sender = command.Sender.UserNum;
        var receiver = command.Receiver.UserNum;
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var db = session.Db;
        await using var tx = await session.BeginAsync().ConfigureAwait(false);
        foreach (var user in sender < receiver ? new[] { sender, receiver } : new[] { receiver, sender })
        {
            if ((await MySqlPlayerLock.LockQuery(db, user).ToListAsync(ct).ConfigureAwait(false)).Count != 0)
                continue;
            if (user == receiver)
                return new(PresentSendOutcome.ReceiverMissing, null, null);
            throw new InvalidOperationException($"Player {user} does not exist; refusing to charge it for a gift.");
        }

        if (await CountVisibleAsync(db, command.ProfileId, receiver, PresentBox.Received, ct).ConfigureAwait(false) >= command.MaxPerPlayer)
            return new(PresentSendOutcome.ReceiverBoxFull, null, null);
        if (await CountVisibleAsync(db, command.ProfileId, sender, PresentBox.Sent, ct).ConfigureAwait(false) >= command.MaxPerPlayer)
            return new(PresentSendOutcome.SenderBoxFull, null, null);

        var seed = new WalletRow { UserNum = sender, Lucci = command.SeedWallet.Lucci, Cash = command.SeedWallet.Cash };
        await SugarSql.UpsertAsync(db, seed, [nameof(WalletRow.UpdatedAt)], [nameof(WalletRow.UserNum)], [], ct)
            .ConfigureAwait(false);
        var walletRows = await db.Queryable<WalletRow>().Where(w => w.UserNum == sender).Take(1)
            .TranLock(DbLockType.Wait).ToListAsync(ct).ConfigureAwait(false);
        if (walletRows.Count != 1)
            throw new InvalidOperationException($"player_wallets row for user_num {sender} is missing after the seed insert.");
        var wallet = new PlayerWallet(walletRows[0].UserNum, walletRows[0].Lucci, walletRows[0].Cash);
        var cash = command.MoneyType == "cash";
        var balance = cash ? wallet.Cash : wallet.Lucci;
        if (balance < command.Price)
            return new(PresentSendOutcome.InsufficientFunds, null, null);

        var row = new PresentRow
        {
            Profile = command.ProfileId, SenderUserNum = sender, SenderNickname = command.Sender.Nickname,
            ReceiverUserNum = receiver, ReceiverNickname = command.Receiver.Nickname, StockKey = command.StockKey,
            Message = command.Message, SentAtUnixMilliseconds = command.SentAtUnixMilliseconds, State = Pending,
        };
        var id = await db.Insertable(row).ExecuteReturnBigIdentityAsync().ConfigureAwait(false);
        if (id <= 0)
            throw new InvalidOperationException($"presents insert for user_num {sender} returned no identity.");
        var charged = cash ? wallet with { Cash = balance - command.Price } : wallet with { Lucci = balance - command.Price };
        await MySqlShopPurchaseStore.ChargeUpdate(db, charged).ExecuteCommandAsync(ct).ConfigureAwait(false);
        var audit = new PurchaseLogRow
        {
            UserNum = sender, StockSetId = command.StockSetId, ItemId = command.ItemId, MoneyType = command.MoneyType,
            Price = command.Price, SlotKey = 0, PurchaseKey = command.StockKey, LastSlotKey = 0,
        };
        if (await db.Insertable(audit).ExecuteCommandAsync(ct).ConfigureAwait(false) < 1)
            throw new InvalidOperationException($"purchase_log insert for user_num {sender} wrote no row.");
        row.Id = id;
        await tx.CommitAsync().ConfigureAwait(false);
        return new(PresentSendOutcome.Sent, ToRecord(row), charged);
    }

    /// <inheritdoc />
    public async ValueTask<PresentRewardResult> GrantRewardAsync(PresentRewardCommand command, CancellationToken ct)
    {
        command.Validate();
        var receiver = command.ReceiverUserNum;
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var db = session.Db;
        await using var tx = await session.BeginAsync().ConfigureAwait(false);
        // The receiver's row lock serializes two grants for one player, so the claim lookup below cannot race an insert
        // (the primary key is the backstop, not the mechanism).
        if ((await MySqlPlayerLock.LockQuery(db, receiver).ToListAsync(ct).ConfigureAwait(false)).Count == 0)
            return new(PresentRewardOutcome.ReceiverMissing, []);
        var nickname = (await db.Queryable<PresentPlayerRow>().Where(p => p.UserNum == receiver).Take(1)
            .ToListAsync(ct).ConfigureAwait(false)).Single().Nickname;
        if (await ClaimQuery(db, command.ProfileId, receiver, command.ClaimKey).CountAsync(ct).ConfigureAwait(false) != 0)
            return new(PresentRewardOutcome.AlreadyClaimed, []);
        var claim = new PresentClaimRow
        {
            Profile = command.ProfileId, UserNum = receiver, ClaimKey = command.ClaimKey,
            ClaimedAtUnixMilliseconds = command.SentAtUnixMilliseconds,
        };
        if (await db.Insertable(claim).ExecuteCommandAsync(ct).ConfigureAwait(false) < 1)
            throw new InvalidOperationException($"present_claims insert for user_num {receiver} wrote no row.");
        var granted = new List<PresentRecord>(command.StockKeys.Count);
        foreach (var key in command.StockKeys)
        {
            var row = new PresentRow
            {
                Profile = command.ProfileId, SenderUserNum = PresentLimits.SystemSenderUserNum,
                SenderNickname = command.SenderNickname, ReceiverUserNum = receiver,
                ReceiverNickname = nickname, StockKey = key, Message = command.Message,
                SentAtUnixMilliseconds = command.SentAtUnixMilliseconds, State = Pending,
                // The system has no sent list, so the sender's view starts hidden.
                SenderHidden = 1,
            };
            var id = await db.Insertable(row).ExecuteReturnBigIdentityAsync().ConfigureAwait(false);
            if (id is <= 0 or > PresentLimits.MaxId)
                throw new InvalidOperationException($"presents insert for user_num {receiver} returned an identity outside the client range.");
            row.Id = id;
            granted.Add(ToRecord(row));
        }
        await tx.CommitAsync().ConfigureAwait(false);
        return new(PresentRewardOutcome.Granted, granted);
    }

    /// <summary>Selects the claim row of one reward key.</summary>
    /// <param name="db">The client.</param>
    /// <param name="profileId">The build the reward belongs to.</param>
    /// <param name="userNum">The receiving player.</param>
    /// <param name="claimKey">The once-only key.</param>
    /// <returns>The query, not yet executed.</returns>
    internal static ISugarQueryable<PresentClaimRow> ClaimQuery(SqlSugarClient db, string profileId, long userNum, string claimKey) =>
        db.Queryable<PresentClaimRow>().Where(c => c.Profile == profileId && c.UserNum == userNum && c.ClaimKey == claimKey);

    /// <inheritdoc />
    public ValueTask<PresentPage> ListReceivedAsync(string profileId, long userNum, int start, int count, CancellationToken ct) =>
        ListAsync(profileId, userNum, PresentBox.Received, start, count, ct);

    /// <inheritdoc />
    public ValueTask<PresentPage> ListSentAsync(string profileId, long userNum, int start, int count, CancellationToken ct) =>
        ListAsync(profileId, userNum, PresentBox.Sent, start, count, ct);

    private async ValueTask<PresentPage> ListAsync(string profileId, long userNum, PresentBox box, int start, int count, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, PresentLimits.MaxPageCount);
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var total = await CountVisibleAsync(session.Db, profileId, userNum, box, ct).ConfigureAwait(false);
        var rows = await VisibleQuery(session.Db, profileId, userNum, box).OrderBy(p => p.Id, OrderByType.Desc)
            .Skip(start).Take(count).ToListAsync(ct).ConfigureAwait(false);
        return new(total, rows.Select(ToRecord).ToArray());
    }

    /// <inheritdoc />
    public async ValueTask<int> CountPendingAsync(string profileId, long userNum, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        return await VisibleQuery(session.Db, profileId, userNum, PresentBox.Received).Where(p => p.State == Pending)
            .CountAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<PresentRecord?> FindReceivedAsync(string profileId, long userNum, long id, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var rows = await VisibleQuery(session.Db, profileId, userNum, PresentBox.Received).Where(p => p.Id == id).Take(1)
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Count == 0 ? null : ToRecord(rows[0]);
    }

    /// <inheritdoc />
    public ValueTask<bool> DiscardAsync(string profileId, long userNum, long id, CancellationToken ct) =>
        ChangeAsync(profileId, id, db => DiscardUpdate(db, profileId, userNum, id), ct);

    /// <inheritdoc />
    public ValueTask<bool> DeleteReceivedAsync(string profileId, long userNum, long id, CancellationToken ct) =>
        ChangeAsync(profileId, id, db => HideUpdate(db, profileId, userNum, id, PresentBox.Received), ct);

    /// <inheritdoc />
    public ValueTask<bool> DeleteSentAsync(string profileId, long userNum, long id, CancellationToken ct) =>
        ChangeAsync(profileId, id, db => HideUpdate(db, profileId, userNum, id, PresentBox.Sent), ct);

    private async ValueTask<bool> ChangeAsync(string profileId, long id, Func<SqlSugarClient, IUpdateable<PresentRow>> update, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        await using var tx = await session.BeginAsync().ConfigureAwait(false);
        var changed = await update(session.Db).ExecuteCommandAsync(ct).ConfigureAwait(false) == 1;
        if (changed)
            await PurgeDelete(session.Db, id).ExecuteCommandAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync().ConfigureAwait(false);
        return changed;
    }

    /// <summary>
    /// Marks a gift taken, inside the purchase transaction that delivers it; the caller holds the receiver's
    /// player lock.
    /// </summary>
    /// <param name="db">The client bound to the purchase transaction.</param>
    /// <param name="id">The gift.</param>
    /// <param name="userNum">The receiving player.</param>
    /// <returns>The update, not yet executed; it must change exactly one row.</returns>
    internal static IUpdateable<PresentRow> AcceptUpdate(SqlSugarClient db, long id, long userNum)
    {
        var taken = (byte)PresentState.Accepted;
        var pending = Pending;
        byte visible = 0;
        return db.Updateable<PresentRow>().SetColumns(p => p.State == taken)
            .Where(p => p.Id == id && p.ReceiverUserNum == userNum && p.State == pending && p.ReceiverHidden == visible);
    }

    /// <summary>Throws a pending gift away: the state becomes discarded and the receiver's view is hidden.</summary>
    /// <param name="db">The client.</param>
    /// <param name="profileId">The build the gift belongs to.</param>
    /// <param name="userNum">The receiving player.</param>
    /// <param name="id">The gift.</param>
    /// <returns>The update, not yet executed.</returns>
    internal static IUpdateable<PresentRow> DiscardUpdate(SqlSugarClient db, string profileId, long userNum, long id)
    {
        var discarded = (byte)PresentState.Discarded;
        var pending = Pending;
        byte hidden = 1;
        byte visible = 0;
        return db.Updateable<PresentRow>().SetColumns(p => p.State == discarded).SetColumns(p => p.ReceiverHidden == hidden)
            .Where(p => p.Id == id && p.Profile == profileId && p.ReceiverUserNum == userNum && p.State == pending
                && p.ReceiverHidden == visible);
    }

    /// <summary>Hides a gift from one player's list.</summary>
    /// <param name="db">The client.</param>
    /// <param name="profileId">The build the gift belongs to.</param>
    /// <param name="userNum">The player whose list loses the gift.</param>
    /// <param name="id">The gift.</param>
    /// <param name="box">Which list.</param>
    /// <returns>The update, not yet executed.</returns>
    internal static IUpdateable<PresentRow> HideUpdate(SqlSugarClient db, string profileId, long userNum, long id, PresentBox box)
    {
        byte hidden = 1;
        byte visible = 0;
        return box == PresentBox.Received
            ? db.Updateable<PresentRow>().SetColumns(p => p.ReceiverHidden == hidden)
                .Where(p => p.Id == id && p.Profile == profileId && p.ReceiverUserNum == userNum && p.ReceiverHidden == visible)
            : db.Updateable<PresentRow>().SetColumns(p => p.SenderHidden == hidden)
                .Where(p => p.Id == id && p.Profile == profileId && p.SenderUserNum == userNum && p.SenderHidden == visible);
    }

    /// <summary>Removes a gift both players have hidden.</summary>
    /// <param name="db">The client.</param>
    /// <param name="id">The gift.</param>
    /// <returns>The delete, not yet executed.</returns>
    internal static IDeleteable<PresentRow> PurgeDelete(SqlSugarClient db, long id)
    {
        byte hidden = 1;
        return db.Deleteable<PresentRow>().Where(p => p.Id == id && p.SenderHidden == hidden && p.ReceiverHidden == hidden);
    }

    private static ISugarQueryable<PresentRow> VisibleQuery(SqlSugarClient db, string profileId, long userNum, PresentBox box)
    {
        byte visible = 0;
        return box == PresentBox.Received
            ? db.Queryable<PresentRow>().Where(p => p.Profile == profileId && p.ReceiverUserNum == userNum && p.ReceiverHidden == visible)
            : db.Queryable<PresentRow>().Where(p => p.Profile == profileId && p.SenderUserNum == userNum && p.SenderHidden == visible);
    }

    private static async Task<int> CountVisibleAsync(SqlSugarClient db, string profileId, long userNum, PresentBox box, CancellationToken ct) =>
        await VisibleQuery(db, profileId, userNum, box).CountAsync(ct).ConfigureAwait(false);

    private static PresentRecord ToRecord(PresentRow row) => new(row.Id, row.Profile, row.SenderUserNum, row.SenderNickname,
        row.ReceiverUserNum, row.ReceiverNickname, row.StockKey, row.Message, row.SentAtUnixMilliseconds, (PresentState)row.State)
    {
        SenderHidden = row.SenderHidden != 0,
        ReceiverHidden = row.ReceiverHidden != 0,
    };
}
