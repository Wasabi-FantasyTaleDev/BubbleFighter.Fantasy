using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Enhancement;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Contracts.Pets;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.ItemEnhancement.Data;

namespace BubbleFighter.Fantasy.ItemEnhancement;

internal sealed class ModificationRuntime(ItemModificationOptions options, ModificationPolicy policy, EnhancementCatalog catalog,
    IItemModificationStore store, IItemEnhancementStore inventory, IEquipmentArticleResolver articles, InventoryLoadoutScope scope,
    IMessageSender<LabOperationRefused> refusals, IMessageSender<LabModificationCompleted> completions,
    IMessageSender<InventoryReplaced> inventories, IMessageSender<ShopBalanceUpdated> balances,
    TimeProvider time, Func<int, int> sample, IPetEntitlements? pets, FavoriteWeaponCatalog? favorite = null, FavoriteAwards? awards = null)
{
    internal async ValueTask HandleAsync(ISession session, IPlayerSession player, long generation,
        LabOperationRequested request, CancellationToken ct)
    {
        var deleting = request.Operation == LabOperation.ModificationDelete;
        if (request.Keys.Count != (deleting ? 1 : 4) || (deleting ? request.Argument is < 0 or > 2 : request.Argument != 0))
        { await RefuseAsync().ConfigureAwait(false); return; }
        var owner = player.Identity.UserNum;
        var key = unchecked((ulong)request.Keys[0]);
        var target = await articles.ResolveAsync(owner, key, ct).ConfigureAwait(false);
        if (target is null || !catalog.Items.TryGetValue(target.ItemId, out var definition) || !definition.IsModificationWeapon
            || target.PetIdentity is not null || target.Lifetime.Kind != InventoryLifetimeKind.Permanent
            || (target.Flags & InventorySlotRecord.WornFlagBit) != 0)
        { await RefuseAsync().ConfigureAwait(false); return; }
        var prior = target.Modifications?.Find(scope.Key);
        var installed = prior?.Attributes ?? [];
        if (installed.Any(attribute => !policy.Recognizes(attribute)))
        { await RefuseAsync().ConfigureAwait(false); return; }
        var debits = new List<ItemEnhancementDebit>();
        ItemModificationAttribute? added = null;
        ItemModificationAttribute? deleted = null;
        if (deleting)
        {
            deleted = installed.SingleOrDefault(attribute => attribute.Group == request.Argument);
            if (deleted is null) { await RefuseAsync().ConfigureAwait(false); return; }
        }
        else
        {
            if (installed.Count >= 3 || request.Keys.Skip(1).Any(part => part <= 0 || part >= (long)PetArticleKeys.First || !policy.HasPart((int)part)))
            { await RefuseAsync().ConfigureAwait(false); return; }
            // kr-1256 sub_DF4120 rejects each selected part whose effect category is already installed on the target.
            if (request.Keys.Skip(1).Any(part => policy.HasInstalledEffect((int)part, installed)))
            { await RefuseAsync().ConfigureAwait(false); return; }
            var snapshot = await inventory.ReadEnhancementInventoryAsync(owner, scope, ct).ConfigureAwait(false);
            foreach (var group in request.Keys.Skip(1).GroupBy(part => checked((int)part)))
            {
                var matches = snapshot.CharacterBag.Where(row => row.ItemId == group.Key).Take(2).ToArray();
                var quantity = checked(group.Count() * options.PartQuantity!.Value);
                if (matches.Length != 1 || matches[0].Quantity < quantity || InventoryArticleIdentityResolver.Of(matches[0]) is not null
                    || matches[0].Lifetime.Kind == InventoryLifetimeKind.Life || InventoryExpiry.IsExpired(matches[0], time.GetUtcNow()))
                { await RefuseAsync().ConfigureAwait(false); return; }
                debits.Add(new(matches[0], quantity));
            }
        }
        if (player.Phase != SessionPhase.MainMenu || player.Generation != generation)
        { await RefuseAsync().ConfigureAwait(false); return; }
        if (!deleting)
        {
            var group = Enumerable.Range(0, 3).First(candidate => installed.All(attribute => attribute.Group != candidate));
            added = policy.Select(request.Keys.Skip(1).Select(part => checked((int)part)).ToArray(), installed.Count, group, sample);
        }
        var now = time.GetUtcNow();
        var result = await store.TryModifyAsync(new(target, scope, debits, added, deleting ? deleted!.Group : null,
            deleting ? options.DeleteLucciCost!.Value : options.ApplyLucciCost!.Value, now)
            {
                ArticleInitialization = articles.CreateInitialization(owner, now), PetInitialization = pets?.CreateInitialization(owner, now),
                // The grade index is the drawn first category (kr-1256 sub_10EE880 keys the sheet by it); deletion awards nothing.
                FavoritePoints = favorite is not null && added is not null ? favorite.ModificationPoints[added.NumericTag] : 0,
            }, ct)
            .ConfigureAwait(false);
        if (!CurrentScene()) return;
        if (result is null) { await RefuseAsync().ConfigureAwait(false); return; }
        await balances.SendAsync(session, new(player.Identity, result.Wallet), ct).ConfigureAwait(false);
        if (!CurrentScene()) return;
        // kr-1256 sub_DF9760 resolves the installed Article; kr-1256 sub_9C5A70 refreshes the same inventory after deletion.
        await inventories.SendAsync(session, new(result.Inventory.CharacterBag, result.Inventory.EquippedBag) { IsCompleteSnapshot = true }, ct)
            .ConfigureAwait(false);
        if (!CurrentScene()) return;
        var outcome = added ?? deleted!;
        await completions.SendAsync(session, new(deleting, key, outcome.Id, outcome.Group), ct).ConfigureAwait(false);
        if (!CurrentScene()) return;
        if (added is not null && favorite is not null && awards is not null)
            await awards.NotifyAsync(session, target, favorite.ModificationPoints[added.NumericTag], ct).ConfigureAwait(false);

        ValueTask RefuseAsync() => refusals.SendAsync(session, new(request.Operation), ct);
        bool CurrentScene() => player.Phase == SessionPhase.MainMenu && player.Generation == generation;
    }
}

internal sealed class ModificationPolicy
{
    private readonly IReadOnlyDictionary<int, ModificationPartConfiguration> _parts;
    private readonly IReadOnlyDictionary<(int NumericTag, int Id), int> _attributeCategories;
    internal ModificationConfiguration Configuration { get; }

    internal ModificationPolicy(ItemModificationOptions options, EnhancementCatalog catalog, InventoryLoadoutScope scope)
    {
        var errors = options.Validate().ToArray();
        if (!options.Enabled || errors.Length != 0) throw new ConfigException("Modification policy is disabled or invalid.");
        Configuration = options.Configuration();
        _parts = Configuration.Parts.ToDictionary(part => part.ItemId);
        _attributeCategories = Configuration.Parts.SelectMany(part => part.AttributeIds.Select(id =>
            (Key: (part.NumericTag, Id: id), part.EffectCategory))).ToDictionary(entry => entry.Key, entry => entry.EffectCategory);
        foreach (var part in Configuration.Parts)
        {
            // kr-1256 sub_D44520 action 12 admits ordinary inventory in subcategory 507 without the Article target flag.
            if (!scope.Supports(part.ItemId) || !catalog.Items.TryGetValue(part.ItemId, out var item)
                || item.DurationType != "count" || item.SubCategory != "SienceLab")
                throw new ConfigException($"Modification part {part.ItemId} is not an eligible count-based native laboratory item.");
            foreach (var id in part.AttributeIds) catalog.RequireModificationAttribute(id);
        }
    }

    internal bool HasPart(int id) => _parts.ContainsKey(id);
    internal bool Recognizes(ItemModificationAttribute attribute) => _attributeCategories.ContainsKey((attribute.NumericTag, attribute.Id));

    internal bool HasInstalledEffect(int partId, IReadOnlyList<ItemModificationAttribute> installed) => installed.Any(attribute =>
        _attributeCategories[(attribute.NumericTag, attribute.Id)] == _parts[partId].EffectCategory);

    internal ItemModificationAttribute Select(IReadOnlyList<int> selected, int installedCount, int group, Func<int, int> sample)
    {
        if (selected.Count != 3 || installedCount is < 0 or > 2) throw new ArgumentException("Modification selection has invalid native bounds.");
        var tags = new SortedDictionary<int, int>();
        var effects = new SortedDictionary<int, int>();
        // kr-1256 sub_DE9260 sums each selected part's category weights at the current installed count, then combines both distributions.
        foreach (var id in selected)
        {
            var part = _parts[id];
            tags[part.NumericTag] = checked(tags.GetValueOrDefault(part.NumericTag) + Configuration.TagWeights[part.NumericTag][installedCount]);
            effects[part.EffectCategory] = checked(effects.GetValueOrDefault(part.EffectCategory) + Configuration.EffectWeights[part.EffectCategory][installedCount]);
        }
        var tag = Pick(tags, sample);
        var effect = Pick(effects, sample);
        var result = Configuration.Parts.Single(part => part.NumericTag == tag && part.EffectCategory == effect);
        var index = CheckedSample(7, sample);
        var selector = result.AttributeIds[index];
        return new(selector, result.Value0, result.NegativeValues[selector], group, tag);
    }

    private static int Pick(SortedDictionary<int, int> weights, Func<int, int> sample)
    {
        if (weights.Count == 1) return weights.First().Key;
        var selected = CheckedSample(weights.Values.Sum(), sample);
        foreach (var (key, weight) in weights)
        {
            if (selected < weight) return key;
            selected -= weight;
        }
        throw new InvalidOperationException("Modification weight selection exhausted its distribution.");
    }

    private static int CheckedSample(int bound, Func<int, int> sample)
    {
        var value = sample(bound);
        return value >= 0 && value < bound ? value : throw new InvalidOperationException("Modification random source returned an out-of-range sample.");
    }
}
