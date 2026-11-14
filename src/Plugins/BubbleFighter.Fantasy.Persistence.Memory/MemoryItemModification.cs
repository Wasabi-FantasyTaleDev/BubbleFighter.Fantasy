using BubbleFighter.Fantasy.Contracts.Inventory;

namespace BubbleFighter.Fantasy.Persistence.Memory;

public sealed partial class MemoryInventoryRepository : IItemModificationStore
{
    /// <inheritdoc />
    public ValueTask<ItemModificationResult?> TryModifyAsync(ItemModificationCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();
        ct.ThrowIfCancellationRequested();
        var entry = table.Find(command.UserNum);
        if (entry is null) return ValueTask.FromResult<ItemModificationResult?>(null);
        lock (entry.Gate)
        {
            var wallet = entry.Money ?? throw new InvalidOperationException("Modification requires an initialized wallet.");
            if (wallet.Lucci < command.LucciCost) return ValueTask.FromResult<ItemModificationResult?>(null);
            var charged = wallet with { Lucci = checked(wallet.Lucci - command.LucciCost) };
            var result = MutateArticles(entry, command.UserNum, command.Scope,
                command.ArticleInitialization, command.PetInitialization, prepared =>
                {
                    var rows = command.Apply(prepared.Rows, prepared.Loadout);
                    return rows is null ? null : new ArticleMutation(rows, prepared.Loadout);
                }, ct);
            if (result is null) return ValueTask.FromResult<ItemModificationResult?>(null);
            entry.Money = charged;
            return ValueTask.FromResult<ItemModificationResult?>(new(result, charged));
        }
    }
}
