using BubbleFighter.Fantasy.Contracts.Cluster;
using BubbleFighter.Fantasy.Contracts.Wallet;
using BubbleFighter.Fantasy.Abstractions.Persistence;

namespace BubbleFighter.Fantasy.Persistence.MySql;

/// <summary>MySQL store for the <c>player_wallets</c> table from migration 0020.</summary>
public sealed class MySqlPlayerWalletRepository(IDbConnectionFactory connections, IAccountMutationFence? authority = null) : IPlayerWalletRepository
{
    /// <inheritdoc />
    public async ValueTask<PlayerWallet?> FindByUserNumAsync(long userNum, CancellationToken ct)
    {
        if (userNum <= 0)
            throw new ArgumentOutOfRangeException(nameof(userNum), userNum, "user_num must be a positive login id.");

        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var rows = await session.Db.Queryable<WalletRow>()
            .Where(w => w.UserNum == userNum)
            .Take(1)
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Count == 0 ? null : new PlayerWallet(rows[0].UserNum, rows[0].Lucci, rows[0].Cash);
    }

    /// <inheritdoc />
    public async ValueTask<PlayerWallet> GetOrCreateAsync(PlayerWallet initialWallet, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(initialWallet);
        Validate(initialWallet);

        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        await using var transaction = await session.BeginWriteAsync(initialWallet.UserNum, authority, ct).ConfigureAwait(false);
        var row = new WalletRow
        {
            UserNum = initialWallet.UserNum,
            Lucci = initialWallet.Lucci,
            Cash = initialWallet.Cash,
        };
        await SugarSql.UpsertAsync(session.Db, row, [nameof(WalletRow.UpdatedAt)],
            [nameof(WalletRow.UserNum)], [], ct).ConfigureAwait(false);

        var rows = await session.Db.Queryable<WalletRow>()
            .Where(wallet => wallet.UserNum == initialWallet.UserNum)
            .Take(1)
            .ToListAsync(ct).ConfigureAwait(false);
        if (rows.Count == 0)
            throw new InvalidOperationException($"player_wallets get-or-create for user_num {initialWallet.UserNum} returned no row.");
        await transaction.CommitAsync().ConfigureAwait(false);
        return new PlayerWallet(rows[0].UserNum, rows[0].Lucci, rows[0].Cash);
    }

    /// <inheritdoc />
    public async ValueTask UpsertAsync(PlayerWallet wallet, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(wallet);
        Validate(wallet);

        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        await using var transaction = await session.BeginWriteAsync(wallet.UserNum, authority, ct).ConfigureAwait(false);
        var row = new WalletRow { UserNum = wallet.UserNum, Lucci = wallet.Lucci, Cash = wallet.Cash, UpdatedAt = DateTime.UtcNow };
        var rows = await SugarSql.UpsertAsync(session.Db, row, [],
            [nameof(WalletRow.Lucci), nameof(WalletRow.Cash)],
            [(nameof(WalletRow.UpdatedAt), "UTC_TIMESTAMP()")], ct).ConfigureAwait(false);
        if (rows < 1)
            throw new InvalidOperationException($"player_wallets upsert for user_num {wallet.UserNum} wrote {rows} row(s).");
        await transaction.CommitAsync().ConfigureAwait(false);
    }

    private static void Validate(PlayerWallet wallet)
    {
        if (wallet.UserNum <= 0)
            throw new ArgumentOutOfRangeException(nameof(wallet), wallet.UserNum, "user_num must be a positive login id.");
        if (wallet.Lucci < 0 || wallet.Cash < 0)
            throw new ArgumentOutOfRangeException(nameof(wallet), "A wallet balance cannot be negative.");
    }
}
