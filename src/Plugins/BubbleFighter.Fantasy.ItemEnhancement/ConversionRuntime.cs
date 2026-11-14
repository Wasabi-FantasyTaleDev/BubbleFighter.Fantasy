using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Enhancement;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Contracts.Pets;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.ItemEnhancement.Data;

namespace BubbleFighter.Fantasy.ItemEnhancement;

/// <summary>
/// Converts one individual timed item, or every member of one native set, to permanent ownership for counted cards.
/// A set is refused unless the deployment states its card quantity.
/// </summary>
internal sealed class ConversionRuntime(ItemConversionOptions options, EnhancementCatalog catalog, ILaboratoryInventoryStore store,
    IEquipmentArticleResolver articles, InventoryLoadoutScope scope, IMessageSender<LabOperationRefused> refusals,
    IMessageSender<LabConversionCompleted> completions, IMessageSender<InventoryReplaced> inventories, TimeProvider time,
    IPetEntitlements? pets)
{
    /// <summary>kr-1256 sub_D44890 refuses an action-11 target whose packed flags carry bit 0, 3, 4, 5 or 6.</summary>
    internal const byte ForbiddenFlags = 0x79;

    private const long DayMilliseconds = 86_400_000;

    internal async ValueTask HandleAsync(ISession session, IPlayerSession player, long generation,
        LabOperationRequested request, CancellationToken ct)
    {
        // kr-1256 sub_DC96F0 sends one positive base item identity and argument 0; an Article key is not the selector.
        // kr-1256 sub_DCA620 sends one set identity and argument 0 for a set, with no member keys.
        if (request.Keys.Count != 1 || request.Argument != 0 || request.Keys[0] is <= 0 or > int.MaxValue)
        { await RefuseAsync().ConfigureAwait(false); return; }
        var owner = player.Identity.UserNum;
        var cardId = options.CardItemId!.Value;
        if (Plan((int)request.Keys[0], cardId) is not { } plan)
        { await RefuseAsync().ConfigureAwait(false); return; }
        var (setId, targetIds, cardQuantity) = plan;
        var snapshot = await store.ReadLaboratoryInventoryAsync(owner, scope, ct).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var rows = new List<InventorySlotRecord>(targetIds.Length);
        var guards = new List<InventoryConversionGuard>(targetIds.Length);
        foreach (var targetId in targetIds)
        {
            var targets = snapshot.CharacterBag.Where(row => row.ItemId == targetId).Take(2).ToArray();
            var guard = new InventoryConversionGuard(targetId, options.MinimumDays!.Value * DayMilliseconds, ForbiddenFlags);
            // Every member needs its own single timed slot; one that fails keeps the whole set unconverted.
            if (targets.Length != 1 || !guard.Admits(targets[0], now))
            { await RefuseAsync().ConfigureAwait(false); return; }
            rows.Add(targets[0]);
            guards.Add(guard);
        }
        var cards = snapshot.CharacterBag.Where(row => row.ItemId == cardId).Take(2).ToArray();
        if (cards.Length != 1 || cards[0].Quantity < cardQuantity
            || cards[0].ArticleIdentity is not null || cards[0].Lifetime.Kind != InventoryLifetimeKind.Permanent
            || !CurrentScene())
        { await RefuseAsync().ConfigureAwait(false); return; }

        var initialization = articles.CreateInitialization(owner, now);
        // A converted item receives its equipment identity even when Enchant and Modification do not cover it.
        var missing = targetIds.Where(id => !initialization.EligibleItemIds.Contains(id)).ToArray();
        if (missing.Length != 0)
            initialization = new(initialization.UserNum, initialization.Scope, [.. initialization.EligibleItemIds, .. missing], initialization.Now);
        var command = new InventoryLaboratoryCommand(owner, scope, [new(cards[0], cardQuantity, false)],
            [.. targetIds.Select((id, index) => new InventoryLaboratoryGrant(new(id, InventoryGrantKind.Permanent, 1, 1), rows[index]))], now)
        {
            Conversions = guards,
            ArticleInitialization = initialization,
            PetInitialization = pets?.CreateInitialization(owner, now),
        };
        var committed = await store.TryLaboratoryAsync(command, ct).ConfigureAwait(false);
        if (!CurrentScene()) return;
        if (committed is null)
        { await RefuseAsync().ConfigureAwait(false); return; }
        // The first member in the set row's column order is the key the reply names; the client checks its membership.
        var identities = targetIds.Select(id => InventoryArticleIdentityResolver.Of(Single(committed.GrantedItems, id))).ToArray();
        if (identities.Any(identity => identity is null))
            throw new InvalidDataException("A committed conversion did not install every target's equipment identity.");
        // kr-1256 sub_DF9DA0 resolves the result keys from the complete inventory published first.
        await inventories.SendAsync(session, new(committed.Inventory.CharacterBag, committed.Inventory.EquippedBag) { IsCompleteSnapshot = true }, ct)
            .ConfigureAwait(false);
        if (!CurrentScene()) return;
        var member = identities[0]!.ArticleKey;
        await completions.SendAsync(session, setId is { } set ? LabConversionCompleted.ForSet(set, member) : new(member), ct)
            .ConfigureAwait(false);

        bool CurrentScene() => player.Phase == SessionPhase.MainMenu && player.Generation == generation;
        ValueTask RefuseAsync() => refusals.SendAsync(session, new(request.Operation), ct);
    }

    /// <summary>Resolves the requested identity to the items converted, or null when it names nothing convertible.</summary>
    private ConversionPlan? Plan(int key, int cardId)
    {
        if (catalog.Items.TryGetValue(key, out var item))
            return key != cardId && scope.Supports(key) && item.ConversionRequiresArticle ? new ConversionPlan(null, [key], 1) : null;
        // kr-1256 sub_D457C0 admits a set only when every member has the unlimited flag (sub_A1E3B0); a member that is not
        // equipment has no Article for the client's owned-set map, so it keeps the set refused.
        // The kr-1256 workbook leaves secPartLocation, which kr-1256 sub_DCA620 requires of each member before sending,
        // empty for every unlimited set, so no native client requests one there; the check stays with the client.
        if (options.SetCardQuantity is not { } quantity || !catalog.Sets.TryGetValue(key, out var set)) return null;
        var members = set.Members.ToArray();
        return members.All(member => member != cardId && scope.Supports(member)
            && catalog.Items.TryGetValue(member, out var definition) && definition.ConversionRequiresArticle)
            ? new ConversionPlan(key, members, quantity) : null;
    }

    /// <summary>The items one request converts, and the cards it spends.</summary>
    private readonly record struct ConversionPlan(int? SetId, int[] Targets, int CardQuantity);

    private static InventorySlotRecord Single(IReadOnlyList<InventorySlotRecord> rows, int itemId) =>
        rows.Where(row => row.ItemId == itemId).Take(2).ToArray() is [var only] ? only
            : throw new InvalidDataException("A conversion must install exactly each of its targets once.");
}
