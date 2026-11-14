using BubbleFighter.Fantasy.Contracts.Cluster;
using BubbleFighter.Fantasy.Abstractions.Persistence;
using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Contracts.Titles;
using SqlSugar;

namespace BubbleFighter.Fantasy.Persistence.MySql;

/// <summary>MySQL progress, reward and battle-statistics store with one account-locked transaction per match receipt.</summary>
public sealed class MySqlPlayerProgressRepository : IPlayerBattleRecordsRepository, IRoundTitleMedalSettlementRepository
{
    private readonly IDbConnectionFactory connections;
    private readonly IAccountMutationFence? authority;
    private readonly int maxBattleModes;
    private readonly TitleMedalLimits titleMedals;

    /// <summary>Creates a progress store whose battle aggregates share the account mutation transaction.</summary>
    /// <param name="connections">Connections to the account database.</param>
    /// <param name="authority">Optional account ownership fence for clustered writes.</param>
    /// <param name="maxBattleModesPerPlayer">Maximum distinct recorded modes per player.</param>
    public MySqlPlayerProgressRepository(IDbConnectionFactory connections, IAccountMutationFence? authority = null,
        int maxBattleModesPerPlayer = 128)
        : this(connections, authority, maxBattleModesPerPlayer, new TitleMedalLimits())
    {
    }

    /// <summary>Creates a progress store with the inventory store's medal limits for atomic match awards.</summary>
    public MySqlPlayerProgressRepository(IDbConnectionFactory connections, IAccountMutationFence? authority,
        int maxBattleModesPerPlayer, TitleMedalLimits titleMedals)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBattleModesPerPlayer, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxBattleModesPerPlayer, 4096);
        this.connections = connections;
        this.authority = authority;
        maxBattleModes = maxBattleModesPerPlayer;
        ArgumentNullException.ThrowIfNull(titleMedals);
        titleMedals.Validate();
        this.titleMedals = titleMedals;
    }

    /// <inheritdoc />
    public bool IsDurable => true;

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<PlayerBattleRecord>?> ListBattleRecordsAsync(long userNum, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(userNum, 1);
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var rows = await session.Db.Queryable<BattleRecordRow>().Where(row => row.UserNum == userNum)
            .OrderBy(row => row.ModeId).Take(maxBattleModes + 1).ToListAsync(ct).ConfigureAwait(false);
        if (rows.Count > maxBattleModes)
            throw new InvalidDataException("The player's battle records exceed max_battle_modes_per_player.");
        return rows.Count == 0 ? null : rows.Select(row => row.ToRecord()).ToArray();
    }

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
        await using var tx = command.TitleMedals is null
            ? await session.BeginWriteAsync(user, authority, ct).ConfigureAwait(false)
            : await session.BeginWriteAsync(user, authority, System.Data.IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
        // The player's row lock serializes settlements and every other progress write of this player, so the receipt
        // lookup and the read below see the latest committed state (the primary key is the backstop).
        await MySqlPlayerLock.AcquireAsync(db, user, ct).ConfigureAwait(false);
        var receipts = await db.Queryable<RoundSettlementRow>().Where(r => r.MatchId == match && r.UserNum == user)
            .Take(1).ToListAsync(ct).ConfigureAwait(false);
        if (receipts.Count == 1)
        {
            var receipt = receipts[0];
            if (command.TitleMedals is { } repeatedMedals)
                await MySqlInventoryRepository.ApplyTitleGrantInTransactionAsync(db, repeatedMedals, titleMedals, ct,
                    requireExisting: true).ConfigureAwait(false);
            return new(RoundSettlementOutcome.AlreadySettled, new(user, receipt.BeforeLevel, receipt.BeforeExp),
                new(user, receipt.AfterLevel, receipt.AfterExp), receipt.LucciEarned, await BalanceAsync(db, user, ct).ConfigureAwait(false));
        }
        var players = await db.Queryable<PlayerRow>().Where(p => p.UserNum == user).Take(1).ToListAsync(ct).ConfigureAwait(false);
        if (players.Count == 0)
            throw new InvalidOperationException($"players row for user_num {user} not found.");
        var before = new PlayerProgress(user, players[0].Level, players[0].Exp);
        var after = command.AdvanceChecked(before);
        if (command.TitleMedals is { } medals)
        {
            var applied = await MySqlInventoryRepository.ApplyTitleGrantInTransactionAsync(db, medals, titleMedals, ct)
                .ConfigureAwait(false);
            if (!applied.Applied) throw new InvalidDataException("The round receipt and its medal receipt disagree.");
        }
        if (command.BattleRecord is { } battle)
        {
            var rows = await db.Queryable<BattleRecordRow>().Where(row => row.UserNum == user && row.ModeId == battle.ModeId)
                .Take(1).ToListAsync(ct).ConfigureAwait(false);
            if (rows.Count == 0 && await db.Queryable<BattleRecordRow>().Where(row => row.UserNum == user)
                    .CountAsync(ct).ConfigureAwait(false) >= maxBattleModes)
                throw new InvalidOperationException("The player's battle mode capacity is full; no settlement was stored.");
            var aggregate = (rows.Count == 0 ? new PlayerBattleRecord(battle.ModeId) : rows[0].ToRecord()).Add(battle);
            var replacement = BattleRecordRow.From(user, aggregate);
            var changed = rows.Count == 0
                ? await db.Insertable(replacement).ExecuteCommandAsync(ct).ConfigureAwait(false)
                : await db.Updateable(replacement).ExecuteCommandAsync(ct).ConfigureAwait(false);
            if (changed != 1)
                throw new InvalidOperationException($"Battle record for user_num {user} wrote no row.");
        }
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
        var settlementRow = new RoundSettlementRow
        {
            MatchId = match, UserNum = user, BeforeLevel = before.Level, BeforeExp = before.Exp,
            AfterLevel = after.Level, AfterExp = after.Exp, LucciEarned = lucci,
            SettledAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        if (await db.Insertable(settlementRow).ExecuteCommandAsync(ct).ConfigureAwait(false) != 1)
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
