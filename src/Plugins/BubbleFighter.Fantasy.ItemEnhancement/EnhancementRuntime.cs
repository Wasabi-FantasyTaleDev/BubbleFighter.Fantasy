using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Enhancement;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Contracts.Pets;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.ItemEnhancement.Data;

namespace BubbleFighter.Fantasy.ItemEnhancement;

internal sealed class EnhancementRuntime(ItemEnhancementOptions options, EnhancementCatalog catalog,
    IItemEnhancementStore store, IEquipmentArticleResolver articles, InventoryLoadoutScope scope,
    IMessageSender<LabOperationRefused> refusals, IMessageSender<LabEnchantCompleted> completions,
    IMessageSender<InventoryReplaced> inventories, TimeProvider time, Func<int> roll, IPetEntitlements? pets = null,
    ModificationRuntime? modification = null, ConversionRuntime? conversion = null, FavoriteWeaponCatalog? favorite = null,
    AdvancementRuntime? advancement = null, FavoriteAwards? awards = null) : IDisposable
{
    private readonly SemaphoreSlim _capacity = new(options.MaxInFlightAccounts);
    private readonly ConcurrentDictionary<long, byte> _active = new();

    internal async ValueTask HandleAsync(ISession session, LabOperationRequested request, CancellationToken ct)
    {
        if (!session.TryFeature<IPlayerSession>(out var player))
            throw new InvalidOperationException("Enhancement requires an authenticated player.");
        var modifying = request.Operation is LabOperation.ModificationApply or LabOperation.ModificationDelete;
        var converting = request.Operation == LabOperation.ChangeUnlimited;
        var advancing = request.Operation == LabOperation.MakeItemAdvanced;
        var supported = modifying ? options.Modification.Enabled && modification is not null
            : converting ? options.Conversion.Enabled && conversion is not null
            : advancing ? options.Advancement.Enabled && advancement is not null
            : request.Operation == LabOperation.Enchant && request.Keys.Count == 1 && request.Argument is 0 or 1;
        if (!supported
            || player.Phase != SessionPhase.MainMenu || !await _capacity.WaitAsync(0, ct).ConfigureAwait(false))
        {
            await RefuseAsync().ConfigureAwait(false);
            return;
        }
        var owner = player.Identity.UserNum;
        var generation = player.Generation;
        if (!_active.TryAdd(owner, 0))
        {
            _capacity.Release();
            await RefuseAsync().ConfigureAwait(false);
            return;
        }
        try
        {
            if (modifying)
            {
                await modification!.HandleAsync(session, player, generation, request, ct).ConfigureAwait(false);
                return;
            }
            if (converting)
            {
                await conversion!.HandleAsync(session, player, generation, request, ct).ConfigureAwait(false);
                return;
            }
            if (advancing)
            {
                await advancement!.HandleAsync(session, player, generation, request, ct).ConfigureAwait(false);
                return;
            }
            var key = unchecked((ulong)request.Keys[0]);
            var target = await articles.ResolveAsync(owner, key, ct).ConfigureAwait(false);
            if (target is null || !catalog.Items.TryGetValue(target.ItemId, out var definition)
                || !definition.IsEnchant || definition.Kind is not (ItemEnhancementKind.Weapon or ItemEnhancementKind.Clothing)
                || target.Lifetime.Kind != InventoryLifetimeKind.Permanent || (target.Flags & InventorySlotRecord.WornFlagBit) != 0
                || target.Enhancements?.Find(scope.Key)?.Kind is ItemEnhancementKind.Weapon or ItemEnhancementKind.Clothing)
            {
                await RefuseAsync().ConfigureAwait(false);
                return;
            }
            var snapshot = await store.ReadEnhancementInventoryAsync(owner, scope, ct).ConfigureAwait(false);
            var quantities = new Dictionary<int, int> { [options.CardItemId] = options.CardQuantity!.Value };
            foreach (var item in options.KitItemIds) quantities.Add(item, options.KitQuantity!.Value);
            if (request.Argument == 1) quantities.Add(options.AdditiveItemId, options.AdditiveQuantity!.Value);
            var debits = new List<ItemEnhancementDebit>();
            foreach (var (item, quantity) in quantities)
            {
                var material = snapshot.CharacterBag.Where(row => row.ItemId == item).Take(2).ToArray();
                if (material.Length != 1 || material[0].Quantity < quantity)
                {
                    await RefuseAsync().ConfigureAwait(false);
                    return;
                }
                debits.Add(new(material[0], quantity));
            }
            if (player.Phase != SessionPhase.MainMenu || player.Generation != generation)
            {
                await RefuseAsync().ConfigureAwait(false);
                return;
            }
            // Selecting the additive (argument 1) applies its own configured chance; neither chance is assumed.
            var chance = request.Argument == 1 ? options.AdditiveSuccessBasisPoints!.Value : options.SuccessBasisPoints!.Value;
            var succeeded = chance == 10000;
            if (!succeeded && chance > 0)
            {
                var sample = roll();
                if (sample is < 0 or >= 10000) throw new InvalidOperationException("Enhancement probability source returned an invalid sample.");
                succeeded = sample < chance;
            }
            var now = time.GetUtcNow();
            var committed = await store.TryEnhanceAsync(new([target], scope, debits, definition.Kind, succeeded, now)
                {
                    ArticleInitialization = articles.CreateInitialization(owner, now), PetInitialization = pets?.CreateInitialization(owner, now),
                    // The points commit with the revision and debits; the store adds them only for a successful Weapon outcome.
                    FavoritePoints = favorite is not null && definition.Kind == ItemEnhancementKind.Weapon ? favorite.EnchantPoints : 0,
                }, ct)
                .ConfigureAwait(false);
            if (!CurrentScene()) return;
            if (committed is null)
            {
                await RefuseAsync().ConfigureAwait(false);
                return;
            }
            // kr-1256 sub_DF9DA0 resolves the result key from the installed Article inventory.
            await inventories.SendAsync(session, new(committed.CharacterBag, committed.EquippedBag) { IsCompleteSnapshot = true }, ct)
                .ConfigureAwait(false);
            if (!CurrentScene()) return;
            await completions.SendAsync(session, new(succeeded, [key]), ct).ConfigureAwait(false);
            if (!CurrentScene()) return;
            // kr-1256 sub_9D0E60 is a chat notice only; it follows the committed refresh so the next bar read matches it.
            if (succeeded && favorite is not null && definition.Kind == ItemEnhancementKind.Weapon && awards is not null)
                await awards.NotifyAsync(session, target, favorite.EnchantPoints, ct).ConfigureAwait(false);

            bool CurrentScene() => player.Phase == SessionPhase.MainMenu && player.Generation == generation;
        }
        finally { _active.TryRemove(owner, out _); _capacity.Release(); }

        ValueTask RefuseAsync() => refusals.SendAsync(session, new(request.Operation), ct);
    }

    public void Dispose() => _capacity.Dispose();
}
