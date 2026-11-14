using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Enhancement;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Contracts.Pets;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.ItemEnhancement.Data;

namespace BubbleFighter.Fantasy.ItemEnhancement;

/// <summary>
/// Grades up one owned native clothing set for upgrade cards. The result is an Article attribute on the set's first member,
/// merged with the other members' effects by the client (kr-1256 sub_D3B2C0, sub_10E7EC0); no item is replaced.
/// </summary>
internal sealed class AdvancementRuntime(AdvancementPolicy policy, EnhancementCatalog catalog, IItemEnhancementStore store,
    IEquipmentArticleResolver articles, InventoryLoadoutScope scope, IMessageSender<LabOperationRefused> refusals,
    IMessageSender<LabAdvancementCompleted> completions, IMessageSender<InventoryReplaced> inventories, TimeProvider time,
    IPetEntitlements? pets)
{
    internal async ValueTask HandleAsync(ISession session, IPlayerSession player, long generation,
        LabOperationRequested request, CancellationToken ct)
    {
        // kr-1256 sub_DDA160 sends the set identity then the set card's member Article keys, and the selected card as the argument.
        if (request.Keys.Count < 2 || request.Keys[0] is <= 0 or > int.MaxValue || request.Argument is <= 0 or > int.MaxValue
            || !catalog.Sets.TryGetValue((int)request.Keys[0], out var set) || request.Keys.Count - 1 != set.Members.Count
            || set.Members.Any(member => !catalog.Items.TryGetValue(member, out var item) || item.Kind != ItemEnhancementKind.Clothing)
            || request.Keys.Skip(1).Any(key => key <= 0 || !PetArticleKeys.IsAddressable((ulong)key)))
        { await RefuseAsync().ConfigureAwait(false); return; }
        var owner = player.Identity.UserNum;
        var rows = new List<InventorySlotRecord>(set.Members.Count);
        foreach (var key in request.Keys.Skip(1))
        {
            var row = await articles.ResolveAsync(owner, (ulong)key, ct).ConfigureAwait(false);
            // Every listed Article must be a distinct member of the set, so the request covers the whole set.
            if (row is null || row.PetIdentity is not null || row.Lifetime.Kind != InventoryLifetimeKind.Permanent
                || (row.Flags & InventorySlotRecord.WornFlagBit) != 0 || !set.Members.Contains(row.ItemId)
                || rows.Any(other => other.ItemId == row.ItemId))
            { await RefuseAsync().ConfigureAwait(false); return; }
            rows.Add(row);
        }
        // The first member in the set row's column order carries the attribute and is the key the reply names.
        rows = [.. rows.OrderBy(row => set.Members.ToList().IndexOf(row.ItemId))];
        var effects = rows.Select(row => row.Enhancements?.Find(scope.Key)?.Advancement ?? 0).Where(effect => effect != 0).ToHashSet();
        if (policy.Match(set.Id, AdvancementPolicy.ClassOf(catalog, set), effects, (int)request.Argument) is not { } rule)
        { await RefuseAsync().ConfigureAwait(false); return; }
        var (quantity, effect) = rule;
        var snapshot = await store.ReadEnhancementInventoryAsync(owner, scope, ct).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var cards = snapshot.CharacterBag.Where(row => row.ItemId == (int)request.Argument).Take(2).ToArray();
        if (cards.Length != 1 || cards[0].Quantity < quantity || InventoryArticleIdentityResolver.Of(cards[0]) is not null
            || cards[0].Lifetime.Kind == InventoryLifetimeKind.Life || InventoryExpiry.IsExpired(cards[0], now) || !CurrentScene())
        { await RefuseAsync().ConfigureAwait(false); return; }
        var committed = await store.TryEnhanceAsync(new(rows, scope, [new(cards[0], quantity)], ItemEnhancementKind.None, false, now)
            {
                Advancement = effect,
                ArticleInitialization = articles.CreateInitialization(owner, now),
                PetInitialization = pets?.CreateInitialization(owner, now),
            }, ct).ConfigureAwait(false);
        if (!CurrentScene()) return;
        if (committed is null) { await RefuseAsync().ConfigureAwait(false); return; }
        // kr-1256 sub_DF9DA0 resolves the set and its member from the owned inventory published first.
        await inventories.SendAsync(session, new(committed.CharacterBag, committed.EquippedBag) { IsCompleteSnapshot = true }, ct)
            .ConfigureAwait(false);
        if (!CurrentScene()) return;
        await completions.SendAsync(session, new(set.Id, InventoryArticleIdentityResolver.Of(rows[0])!.ArticleKey), ct).ConfigureAwait(false);

        bool CurrentScene() => player.Phase == SessionPhase.MainMenu && player.Generation == generation;
        ValueTask RefuseAsync() => refusals.SendAsync(session, new(request.Operation), ct);
    }
}
