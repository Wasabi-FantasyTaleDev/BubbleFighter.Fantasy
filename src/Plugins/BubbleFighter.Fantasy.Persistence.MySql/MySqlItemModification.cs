using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Wallet;
using SqlSugar;

namespace BubbleFighter.Fantasy.Persistence.MySql;

public sealed partial class MySqlInventoryRepository : IItemModificationStore
{
    /// <inheritdoc />
    public async ValueTask<ItemModificationResult?> TryModifyAsync(ItemModificationCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();
        ct.ThrowIfCancellationRequested();
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var db = session.Db;
        await using var transaction = await session.BeginWriteAsync(command.UserNum, authority, ct).ConfigureAwait(false);
        await MySqlPlayerLock.AcquireAsync(db, command.UserNum, ct).ConfigureAwait(false);
        var user = command.UserNum;
        var wallets = await db.Queryable<WalletRow>().Where(row => row.UserNum == user)
            .Take(1).TranLock(DbLockType.Wait).ToListAsync(ct).ConfigureAwait(false);
        if (wallets.Count != 1) throw new InvalidOperationException("Modification requires an initialized wallet.");
        var wallet = new PlayerWallet(user, wallets[0].Lucci, wallets[0].Cash);
        if (wallet.Lucci < command.LucciCost) return null;
        var charged = wallet with { Lucci = checked(wallet.Lucci - command.LucciCost) };
        var (loadout, exists) = await ReadLoadoutAsync(db, user, command.Scope, ct).ConfigureAwait(false);
        var stored = await ReadOwnedInventoryRowsAsync(db, user, ct).ConfigureAwait(false);
        var result = await MutateArticlesAsync(db, user, command.Scope, stored, loadout, exists,
            command.ArticleInitialization, command.PetInitialization, prepared =>
            {
                var rows = command.Apply(prepared.Rows, prepared.Loadout);
                return rows is null ? null : new ArticleMutation(rows, prepared.Loadout);
            }, ct).ConfigureAwait(false);
        if (result is null) return null;
        if (command.LucciCost > 0 && await MySqlShopPurchaseStore.ChargeUpdate(db, charged).ExecuteCommandAsync(ct).ConfigureAwait(false) != 1)
            throw new InvalidDataException("Modification wallet debit affected an invalid row count.");
        ct.ThrowIfCancellationRequested();
        await transaction.CommitAsync().ConfigureAwait(false);
        return new(result, charged);
    }
}
