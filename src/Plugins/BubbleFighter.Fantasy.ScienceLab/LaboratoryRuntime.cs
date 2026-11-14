using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Enhancement;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Contracts.Pets;
using BubbleFighter.Fantasy.Contracts.Sessions;

namespace BubbleFighter.Fantasy.ScienceLab;

internal sealed record LaboratoryRecipePlan(InventoryLaboratoryCommand Command, int PrimaryReference, LaboratoryOutcome Outcome,
    IReadOnlyList<int> PrimaryMembers, IReadOnlyList<int> ExtraStockIds)
{
    /// <summary>Gets copied primary output identities without resolving a possibly shared stock-set reference again.</summary>
    public IReadOnlyList<int> PrimaryMembers { get; } = Array.AsReadOnly(PrimaryMembers.ToArray());
    /// <summary>Gets copied native Stock references for separately displayed rewards.</summary>
    public IReadOnlyList<int> ExtraStockIds { get; } = Array.AsReadOnly(ExtraStockIds.ToArray());
}

internal static class LaboratoryPlanner
{
    internal static LaboratoryRecipePlan? Plan(ScienceLabOptions options, LaboratoryCatalog catalog, InventoryLoadoutScope scope,
        long owner, InventorySnapshot snapshot, LaboratoryRequested request, DateTimeOffset now, Func<int, int> choose)
    {
        var selections = catalog.Select(request);
        if (selections is null || request.Additives.Count != 2 || request.Additives.Any(id => id != -1 && id <= 0)
            || request.Additives.Where(id => id > 0).Distinct().Count() != request.Additives.Count(id => id > 0)) return null;
        InventorySlotRecord? Owned(int id)
        {
            var found = snapshot.CharacterBag.Where(row => row.ItemId == id).Take(2).ToArray();
            return found.Length == 1 ? found[0] : null;
        }
        var selectedRows = new List<InventorySlotRecord>();
        foreach (var selection in selections)
        {
            var members = new List<InventorySlotRecord>();
            foreach (var id in selection.Members)
            {
                var row = Owned(id);
                if (row is null || !scope.Supports(id) || !catalog.Items.TryGetValue(id, out var item) || !item.Available
                    || item.DurationType != "day" || row.Quantity <= 0 || InventoryExpiry.IsExpired(row, now)
                    || row.Pet is not null || row.PetIdentity is not null || (row.Flags & InventorySlotRecord.WornFlagBit) != 0
                    || row.Lifetime.Kind is not (InventoryLifetimeKind.Permanent or InventoryLifetimeKind.ExpiresAt)) return null;
                members.Add(row);
            }
            // kr-812 sub_9DA530 uses the first member's timer for a grouped selection.
            if (members.Any(row => row.Lifetime != members[0].Lifetime)) return null;
            selectedRows.AddRange(members);
        }
        var debits = selectedRows.Select(row => new InventoryLaboratoryDebit(row, 0, true)).ToList();
        var grants = new List<InventoryLaboratoryGrant>();
        var extraStocks = new List<int>();
        var outcome = LaboratoryOutcome.Ordinary;
        var primary = 0;
        IReadOnlyList<int> primaryMembers;
        if (!Counted(request.CardItemId, options.CardQuantity!.Value)) return null;
        foreach (var additive in request.Additives.Where(id => id > 0))
        {
            if (additive == options.PermanentAdditiveItemId && options.PermanentAdditiveItemId > 0)
            {
                if (request.Operation != LaboratoryOperation.Synthesis
                    || !Counted(additive, options.PermanentAdditiveQuantity!.Value)) return null;
            }
            else if (additive == options.BonusAdditiveItemId && options.BonusAdditiveItemId > 0)
            {
                if (!Counted(additive, options.BonusAdditiveQuantity!.Value)) return null;
            }
            else return null;
        }
        if (request.Operation == LaboratoryOperation.Synthesis)
        {
            if (request.CardItemId != options.SynthesisCardItemId || options.SynthesisCardItemId <= 0) return null;
            var permanent = selections.Any(selection => Owned(selection.Members[0])!.Lifetime.Kind == InventoryLifetimeKind.Permanent);
            var days = permanent ? 0 : selections.Sum(selection =>
                (DateTimeOffset.FromUnixTimeMilliseconds(Owned(selection.Members[0])!.Lifetime.ExpiresAtUnixMilliseconds!.Value) - now).TotalDays);
            if (permanent || days >= options.SynthesisPermanentAtDays!.Value)
            {
                permanent = true;
                outcome = LaboratoryOutcome.BecamePermanent;
            }
            else if (days > options.SynthesisMaximumDays!.Value)
            {
                days = options.SynthesisMaximumDays.Value;
                outcome = LaboratoryOutcome.DurationCapped;
            }
            if (request.Additives.Contains(options.PermanentAdditiveItemId) && options.PermanentAdditiveItemId > 0)
            {
                if (permanent) return null;
                if (Pick(10000) < options.PermanentAdditiveBasisPoints!.Value)
                {
                    permanent = true;
                    outcome = LaboratoryOutcome.AdditivePermanent;
                }
                else
                {
                    AddReward(options.PermanentFailureReward!);
                    outcome = LaboratoryOutcome.AdditiveBonus;
                }
            }
            var selected = selections[options.SynthesisSurvivor == "first" ? 0 : Pick(selections.Count)];
            primary = selected.Reference;
            primaryMembers = selected.Members;
            foreach (var id in selected.Members)
                grants.Insert(0, new(new(id, permanent ? InventoryGrantKind.Permanent : InventoryGrantKind.Days,
                    permanent ? 1 : days, permanent ? 1 : options.SynthesisMaximumDays!.Value), Owned(id)));
        }
        else
        {
            if (selectedRows.Any(row => row.Lifetime.Kind != InventoryLifetimeKind.Permanent)
                || request.Additives.Contains(options.PermanentAdditiveItemId) && options.PermanentAdditiveItemId > 0) return null;
            var recipe = options.Decomposition.SingleOrDefault(value => value.CardItemId == request.CardItemId && value.Category == request.Category);
            if (recipe is null) return null;
            var sample = Pick(recipe.Rewards.Sum(reward => reward.Weight));
            var reward = recipe.Rewards.First(value => (sample -= value.Weight) < 0);
            primary = reward.StockId;
            primaryMembers = new[] { reward.ItemId };
            AddReward(reward, primaryReward: true);
        }
        foreach (var additive in request.Additives.Where(id => id > 0))
        {
            if (additive == options.PermanentAdditiveItemId && options.PermanentAdditiveItemId > 0) continue;
            AddReward(options.BonusReward!);
        }
        if (grants.Select(grant => grant.Term.ItemId).Distinct().Count() != grants.Count
            || debits.Select(debit => debit.ExpectedSlot.ItemId).Distinct().Count() != debits.Count
            || grants.Any(grant => debits.Any(debit => !debit.RemoveOwnership && debit.ExpectedSlot.ItemId == grant.Term.ItemId))) return null;
        return new(new(owner, scope, debits, grants, now), primary, outcome, primaryMembers, extraStocks);

        bool Counted(int id, int quantity)
        {
            var row = Owned(id);
            if (row is null || row.Quantity < quantity || row.Lifetime.Kind != InventoryLifetimeKind.Permanent
                || !scope.Supports(id) || row.Pet is not null || row.PetIdentity is not null || row.ArticleIdentity is not null
                || (row.Flags & InventorySlotRecord.WornFlagBit) != 0
                || !catalog.Items.TryGetValue(id, out var item) || !item.Available || item.DurationType != "count") return false;
            debits.Add(new(row, quantity, false));
            return true;
        }
        void AddReward(LaboratoryRewardOptions reward, bool primaryReward = false)
        {
            catalog.RequireRewardStock(reward);
            grants.Add(new(reward.Term(), Owned(reward.ItemId)));
            if (!primaryReward) extraStocks.Add(reward.StockId);
        }
        int Pick(int count)
        {
            if (count <= 0) throw new InvalidOperationException("Science-room reward pool is empty.");
            var value = choose(count);
            return value >= 0 && value < count ? value : throw new InvalidOperationException("Science-room random source returned an out-of-range value.");
        }
    }
}

internal sealed class LaboratoryRuntime(ScienceLabOptions options, LaboratoryCatalog catalog, InventoryLoadoutScope scope,
    ILaboratoryInventoryStore store, IMessageSender<LaboratoryRefused> refusals, IMessageSender<LaboratoryCompleted> completions,
    IMessageSender<InventoryReplaced> inventories, TimeProvider time, Func<int, int> choose,
    IEquipmentArticleResolver? articles = null, IPetEntitlements? pets = null) : IDisposable
{
    private readonly SemaphoreSlim _capacity = new(options.MaxInFlightAccounts);
    private readonly ConcurrentDictionary<long, byte> _active = new();

    internal async ValueTask HandleAsync(ISession session, LaboratoryRequested request, CancellationToken ct)
    {
        if (!session.TryFeature<IPlayerSession>(out var player)) throw new InvalidOperationException("Science-room recipes require an authenticated player.");
        if (player.Phase != SessionPhase.MainMenu || !await _capacity.WaitAsync(0, ct).ConfigureAwait(false))
        { await Refuse().ConfigureAwait(false); return; }
        var owner = player.Identity.UserNum;
        var generation = player.Generation;
        if (!_active.TryAdd(owner, 0)) { _capacity.Release(); await Refuse().ConfigureAwait(false); return; }
        try
        {
            var snapshot = await store.ReadLaboratoryInventoryAsync(owner, scope, ct).ConfigureAwait(false);
            var now = time.GetUtcNow();
            var plan = LaboratoryPlanner.Plan(options, catalog, scope, owner, snapshot, request, now, choose);
            if (plan is null || player.Generation != generation || player.Phase != SessionPhase.MainMenu)
            { await Refuse().ConfigureAwait(false); return; }
            var result = await store.TryLaboratoryAsync(plan.Command with
            { ArticleInitialization = articles?.CreateInitialization(owner, now), PetInitialization = pets?.CreateInitialization(owner, now) }, ct).ConfigureAwait(false);
            if (!CurrentScene()) return;
            if (result is null) { await Refuse().ConfigureAwait(false); return; }
            // Install the committed ownership before the result can return control to the science-room inventory UI.
            await inventories.SendAsync(session, new(result.Inventory.CharacterBag, result.Inventory.EquippedBag)
                { IsCompleteSnapshot = true }, ct).ConfigureAwait(false);
            if (!CurrentScene()) return;
            var primaryItems = plan.PrimaryMembers;
            var primaryRow = result.GrantedItems.First(row => primaryItems.Contains(row.ItemId));
            await completions.SendAsync(session, new(request.Operation, plan.PrimaryReference, primaryRow.Lifetime, plan.Outcome,
                plan.ExtraStockIds.ElementAtOrDefault(0), plan.ExtraStockIds.ElementAtOrDefault(1)), ct).ConfigureAwait(false);

            bool CurrentScene() => player.Phase == SessionPhase.MainMenu && player.Generation == generation;
        }
        finally { _active.TryRemove(owner, out _); _capacity.Release(); }
        ValueTask Refuse() => refusals.SendAsync(session, new(request.Operation), ct);
    }

    public void Dispose() => _capacity.Dispose();
}
