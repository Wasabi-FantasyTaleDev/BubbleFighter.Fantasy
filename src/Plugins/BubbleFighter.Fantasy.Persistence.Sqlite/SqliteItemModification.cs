using BubbleFighter.Fantasy.Contracts.Inventory;

namespace BubbleFighter.Fantasy.Persistence.Sqlite;

internal sealed partial class SqliteRepositories : IItemModificationStore
{
    public ValueTask<ItemModificationResult?> TryModifyAsync(ItemModificationCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();
        return new(database.Write<ItemModificationResult?>((db, transaction) =>
        {
            var state = db.RequireState(command.UserNum);
            var wallet = state.Wallet ?? throw new InvalidOperationException("Modification requires an initialized wallet.");
            if (wallet.Lucci < command.LucciCost) return null;
            var charged = wallet with { Lucci = checked(wallet.Lucci - command.LucciCost) };
            var result = MutateArticles(db, transaction, state, command.UserNum, command.Scope,
                command.ArticleInitialization, command.PetInitialization, prepared =>
                {
                    var rows = command.Apply(prepared.Rows, prepared.Loadout);
                    if (rows is null) return null;
                    state.Wallet = charged;
                    return new ArticleMutation(rows, prepared.Loadout);
                });
            return result is null ? null : new(result, charged);
        }, ct));
    }
}
