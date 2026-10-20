using BubbleFighter.Fantasy.Abstractions.Persistence;
using BubbleFighter.Fantasy.Contracts.Progress;
using SqlSugar;

namespace BubbleFighter.Fantasy.Persistence.MySql;

/// <summary>MySQL store for <c>players.level</c> / <c>players.exp</c> and round Lucci credits.</summary>
public sealed class MySqlPlayerProgressRepository(IDbConnectionFactory connections) : IPlayerProgressRepository
{
    /// <inheritdoc />
    public async ValueTask<PlayerProgress?> FindAsync(long userNum, CancellationToken ct)
    {
        if (userNum <= 0)
            throw new ArgumentOutOfRangeException(nameof(userNum), userNum, "user_num must be a positive login id.");

        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var rows = await session.Db.Queryable<PlayerRow>()
            .Where(p => p.UserNum == userNum)
            .Take(1)
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Count == 0 ? null : new PlayerProgress(rows[0].UserNum, rows[0].Level, rows[0].Exp);
    }

    /// <inheritdoc />
    public async ValueTask<RoundSettlementResult> SettleRoundAsync(RoundSettlementCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();
        var user = command.UserNum;
        var match = command.MatchId;
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var db = session.Db;
        await using var tx = await session.BeginAsync().ConfigureAwait(false);
        // The player's row lock serializes settlements and every other progress write of this player, so the receipt
        // lookup and the read below see the latest committed state (the primary key is the backstop).
        await MySqlPlayerLock.AcquireAsync(db, user, ct).ConfigureAwait(false);
        var receipts = await db.Queryable<RoundSettlementRow>().Where(r => r.MatchId == match && r.UserNum == user)
            .Take(1).ToListAsync(ct).ConfigureAwait(false);
        if (receipts.Count == 1)
        {
            var receipt = receipts[0];
            return new(RoundSettlementOutcome.AlreadySettled, new(user, receipt.BeforeLevel, receipt.BeforeExp),
                new(user, receipt.AfterLevel, receipt.AfterExp), receipt.LucciEarned, await BalanceAsync(db, user, ct).ConfigureAwait(false));
        }
        var players = await db.Queryable<PlayerRow>().Where(p => p.UserNum == user).Take(1).ToListAsync(ct).ConfigureAwait(false);
        if (players.Count == 0)
            throw new InvalidOperationException($"players row for user_num {user} not found.");
        var before = new PlayerProgress(user, players[0].Level, players[0].Exp);
        var after = command.AdvanceChecked(before);
        var level = after.Level;
        var exp = after.Exp;
        if (await db.Updateable<PlayerRow>().SetColumns(p => p.Level == level).SetColumns(p => p.Exp == exp)
                .Where(p => p.UserNum == user).ExecuteCommandAsync(ct).ConfigureAwait(false) != 1)
            throw new InvalidOperationException($"players row for user_num {user} not found.");
        var lucci = command.LucciEarned;
        // The credit is one in-database increment, so it composes with every other wallet change.
        if (await db.Updateable<WalletRow>()
                .SetColumns(w => w.Lucci == w.Lucci + lucci)
                .SetColumns(w => w.UpdatedAt == SqlFunc.MappingColumn(default(DateTime), "UTC_TIMESTAMP()"))
                .Where(w => w.UserNum == user).ExecuteCommandAsync(ct).ConfigureAwait(false) != 1)
            throw new InvalidOperationException($"player_wallets row for user_num {user} not found.");
        var row = new RoundSettlementRow
        {
            MatchId = match, UserNum = user, BeforeLevel = before.Level, BeforeExp = before.Exp,
            AfterLevel = after.Level, AfterExp = after.Exp, LucciEarned = lucci,
            SettledAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        if (await db.Insertable(row).ExecuteCommandAsync(ct).ConfigureAwait(false) != 1)
            throw new InvalidOperationException($"round_settlements insert for user_num {user} wrote no row.");
        var balance = await BalanceAsync(db, user, ct).ConfigureAwait(false);
        await tx.CommitAsync().ConfigureAwait(false);
        return new(RoundSettlementOutcome.Settled, before, after, lucci, balance);
    }

    private static async ValueTask<int> BalanceAsync(SqlSugarClient db, long user, CancellationToken ct)
    {
        var balance = await db.Queryable<WalletRow>().Where(w => w.UserNum == user).Select(w => w.Lucci)
            .Take(1).ToListAsync(ct).ConfigureAwait(false);
        return balance.Count == 1 ? balance[0] : throw new InvalidOperationException($"player_wallets row for user_num {user} not found.");
    }
}
