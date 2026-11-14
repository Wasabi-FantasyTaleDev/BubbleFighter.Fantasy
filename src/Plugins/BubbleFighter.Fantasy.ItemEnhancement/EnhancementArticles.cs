using BubbleFighter.Fantasy.Contracts.Enhancement;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.ItemEnhancement.Data;

namespace BubbleFighter.Fantasy.ItemEnhancement;

internal sealed class EnhancementArticles(EnhancementCatalog catalog, IItemEnhancementStore store,
    InventoryLoadoutScope scope, TimeProvider time, bool initializeArticles = true) : IEquipmentArticleResolver
{
    private readonly int[] _eligible = initializeArticles ? catalog.Items.Values.Where(item => item.RequiresArticle)
        .Select(item => item.Id).ToArray() : [];

    internal ValueTask<InventorySnapshot> PrepareAsync(long userNum, CancellationToken ct) =>
        initializeArticles ? store.InitializeArticlesAsync(CreateInitialization(userNum, time.GetUtcNow()), ct)
            : store.ReadEnhancementInventoryAsync(userNum, scope, ct);

    /// <summary>Supplies the configured allocation candidates; disabling initialization keeps this set empty for every mutation path.</summary>
    public ArticleInitializationCommand CreateInitialization(long userNum, DateTimeOffset now) => new(userNum, scope, _eligible, now);

    /// <summary>Reads the single live ownership row for a durable equipment Article without allocating or spending anything.</summary>
    public async ValueTask<InventorySlotRecord?> ResolveAsync(long userNum, ulong articleKey, CancellationToken ct)
    {
        if (!PetArticleKeys.IsAddressable(articleKey)) return null;
        var snapshot = await store.ReadEnhancementInventoryAsync(userNum, scope, ct).ConfigureAwait(false);
        var matches = snapshot.CharacterBag.Where(row => row.PetIdentity is null
            && InventoryArticleIdentityResolver.Of(row)?.ArticleKey == articleKey).Take(2).ToArray();
        if (matches.Length != 1) return null;
        var row = matches[0];
        return row.Quantity > 0 && !InventoryExpiry.IsExpired(row, time.GetUtcNow()) ? row : null;
    }
}
