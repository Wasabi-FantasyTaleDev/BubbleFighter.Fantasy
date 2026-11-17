using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Enhancement;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Contracts.Pets;
using BubbleFighter.Fantasy.Contracts.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.ScienceLab;

internal sealed record LaboratoryRecipePlan(InventoryLaboratoryCommand Command, int PrimaryReference, LaboratoryOutcome Outcome,
    IReadOnlyList<int> PrimaryMembers, IReadOnlyList<int> ExtraStockIds)
{
    /// <summary>Gets copied primary output identities without resolving a possibly shared stock-set reference again.</summary>
    public IReadOnlyList<int> PrimaryMembers { get; } = Array.AsReadOnly(PrimaryMembers.ToArray());
    /// <summary>Gets copied native Stock references for separately displayed rewards.</summary>
    public IReadOnlyList<int> ExtraStockIds { get; } = Array.AsReadOnly(ExtraStockIds.ToArray());
}

/// <summary>Names the rule that refused a science-room request, for the operator log and the player's notice.</summary>
internal enum LaboratoryRefusal
{
    /// <summary>The request named no known recipe selection or malformed additive slots.</summary>
    InvalidRequest,
    /// <summary>A selected item is missing from the bag or owned in more than one row.</summary>
    ItemNotOwned,
    /// <summary>A selected item is equipped.</summary>
    ItemWorn,
    /// <summary>A selected item is not a usable timed item (wrong kind, unavailable, expired, a pet or not for this build).</summary>
    ItemUnusable,
    /// <summary>The members of one grouped selection carry different timers.</summary>
    GroupTimersDiffer,
    /// <summary>The recipe card is missing, short or not a permanent counted stack.</summary>
    CardMissing,
    /// <summary>The card does not belong to the requested operation.</summary>
    WrongCard,
    /// <summary>An additive is missing, short or not a permanent counted stack.</summary>
    AdditiveMissing,
    /// <summary>An additive is not one this operation accepts.</summary>
    AdditiveNotAllowed,
    /// <summary>The permanence additive was used on a result that is already permanent.</summary>
    AlreadyPermanent,
    /// <summary>Decomposition takes only permanent items.</summary>
    DecomposeNeedsPermanent,
    /// <summary>No decomposition recipe exists for this card and category.</summary>
    NoRecipe,
    /// <summary>The planned grants and debits overlap.</summary>
    Conflict,
    /// <summary>The player is not in the main menu, or the server is at its configured concurrency.</summary>
    NotAvailable,
    /// <summary>The same account already has a request in progress.</summary>
    InProgress,
    /// <summary>The player changed scene while the request ran.</summary>
    SceneChanged,
    /// <summary>The inventory store refused the transaction because the bag changed underneath it.</summary>
    StoreRefused,
}

internal static class LaboratoryPlanner
{
    internal static LaboratoryRecipePlan? Plan(ScienceLabOptions options, LaboratoryCatalog catalog, InventoryLoadoutScope scope,
        long owner, InventorySnapshot snapshot, LaboratoryRequested request, DateTimeOffset now, Func<int, int> choose,
        IReadOnlyList<LaboratoryRewardOptions>? bonusPool = null) =>
        Plan(options, catalog, scope, owner, snapshot, request, now, choose, out _, bonusPool);

    internal static LaboratoryRecipePlan? Plan(ScienceLabOptions options, LaboratoryCatalog catalog, InventoryLoadoutScope scope,
        long owner, InventorySnapshot snapshot, LaboratoryRequested request, DateTimeOffset now, Func<int, int> choose,
        out LaboratoryRefusal refusal, IReadOnlyList<LaboratoryRewardOptions>? bonusPool = null)
    {
        var reason = new StrongBox<LaboratoryRefusal>(LaboratoryRefusal.InvalidRequest);
        var plan = Core(options, catalog, scope, owner, snapshot, request, now, choose, reason, bonusPool);
        refusal = reason.Value;
        return plan;
    }

    private static LaboratoryRecipePlan? Core(ScienceLabOptions options, LaboratoryCatalog catalog, InventoryLoadoutScope scope,
        long owner, InventorySnapshot snapshot, LaboratoryRequested request, DateTimeOffset now, Func<int, int> choose,
        StrongBox<LaboratoryRefusal> reason, IReadOnlyList<LaboratoryRewardOptions>? bonusPool)
    {
        var selections = catalog.Select(request);
        // The slot count is the build's: kr-812 sub_9DA400 copies two, cn-717 sub_A99FF0 copies its panel's list (four
        // on the wire). Every filled slot is checked on its own below, so the count itself is not a rule.
        if (selections is null || request.Additives.Any(id => id != -1 && id <= 0)
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
                if (row is null) return Refused(LaboratoryRefusal.ItemNotOwned);
                if ((row.Flags & InventorySlotRecord.WornFlagBit) != 0) return Refused(LaboratoryRefusal.ItemWorn);
                if (!scope.Supports(id) || !catalog.Items.TryGetValue(id, out var item) || !item.Available
                    || item.DurationType != "day" || row.Quantity <= 0 || InventoryExpiry.IsExpired(row, now)
                    || row.Pet is not null || row.PetIdentity is not null
                    || row.Lifetime.Kind is not (InventoryLifetimeKind.Permanent or InventoryLifetimeKind.ExpiresAt))
                    return Refused(LaboratoryRefusal.ItemUnusable);
                members.Add(row);
            }
            // kr-812 sub_9DA530 uses the first member's timer for a grouped selection.
            if (members.Any(row => row.Lifetime != members[0].Lifetime)) return Refused(LaboratoryRefusal.GroupTimersDiffer);
            selectedRows.AddRange(members);
        }
        var debits = selectedRows.Select(row => new InventoryLaboratoryDebit(row, 0, true)).ToList();
        var grants = new List<InventoryLaboratoryGrant>();
        var extraStocks = new List<int>();
        var outcome = LaboratoryOutcome.Ordinary;
        var primary = 0;
        IReadOnlyList<int> primaryMembers;
        if (!Counted(request.CardItemId, options.CardQuantity!.Value)) return Refused(LaboratoryRefusal.CardMissing);
        foreach (var additive in request.Additives.Where(id => id > 0))
        {
            if (additive == options.PermanentAdditiveItemId && options.PermanentAdditiveItemId > 0)
            {
                if (request.Operation != LaboratoryOperation.Synthesis) return Refused(LaboratoryRefusal.AdditiveNotAllowed);
                if (!Counted(additive, options.PermanentAdditiveQuantity!.Value)) return Refused(LaboratoryRefusal.AdditiveMissing);
            }
            else if (additive == options.BonusAdditiveItemId && options.BonusAdditiveItemId > 0)
            {
                if (!Counted(additive, options.BonusAdditiveQuantity!.Value)) return Refused(LaboratoryRefusal.AdditiveMissing);
            }
            else return Refused(LaboratoryRefusal.AdditiveNotAllowed);
        }
        if (request.Operation == LaboratoryOperation.Synthesis)
        {
            if (request.CardItemId != options.SynthesisCardItemId || options.SynthesisCardItemId <= 0) return Refused(LaboratoryRefusal.WrongCard);
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
                if (permanent) return Refused(LaboratoryRefusal.AlreadyPermanent);
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
            if (selectedRows.Any(row => row.Lifetime.Kind != InventoryLifetimeKind.Permanent))
                return Refused(LaboratoryRefusal.DecomposeNeedsPermanent);
            if (request.Additives.Contains(options.PermanentAdditiveItemId) && options.PermanentAdditiveItemId > 0)
                return Refused(LaboratoryRefusal.AdditiveNotAllowed);
            var recipe = options.Decomposition.SingleOrDefault(value => value.CardItemId == request.CardItemId && value.Category == request.Category);
            if (recipe is null) return Refused(LaboratoryRefusal.NoRecipe);
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
        // Compact builds show one second slot, which an additive reward already fills; the bonus never displaces it.
        if (request.Operation == LaboratoryOperation.Synthesis && extraStocks.Count == 0 && bonusPool is { Count: > 0 }
            && options.SynthesisBonusBasisPoints is > 0 and var chance && (chance >= 10000 || Pick(10000) < chance))
        {
            // Candidates the store would refuse (several rows, permanent, expired, life, pet, article, worn, mismatched
            // mirrors, over the cap) are skipped so the bonus can never turn a valid synthesis into a refusal.
            var candidates = bonusPool.Where(BonusAdmitted).ToArray();
            if (candidates.Length > 0)
            {
                var sample = Pick(candidates.Sum(candidate => candidate.Weight));
                AddReward(candidates.First(candidate => (sample -= candidate.Weight) < 0));
            }
        }
        if (grants.Select(grant => grant.Term.ItemId).Distinct().Count() != grants.Count
            || debits.Select(debit => debit.ExpectedSlot.ItemId).Distinct().Count() != debits.Count
            || grants.Any(grant => debits.Any(debit => !debit.RemoveOwnership && debit.ExpectedSlot.ItemId == grant.Term.ItemId)))
            return Refused(LaboratoryRefusal.Conflict);
        return new(new(owner, scope, debits, grants, now), primary, outcome, primaryMembers, extraStocks);

        LaboratoryRecipePlan? Refused(LaboratoryRefusal value)
        {
            reason.Value = value;
            return null;
        }
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
        bool BonusAdmitted(LaboratoryRewardOptions candidate)
        {
            var id = candidate.ItemId;
            if (grants.Any(grant => grant.Term.ItemId == id) || debits.Any(debit => debit.ExpectedSlot.ItemId == id)) return false;
            var owned = snapshot.CharacterBag.Where(row => row.ItemId == id).Take(2).ToArray();
            var mirrors = snapshot.EquippedBag.Where(row => row.ItemId == id).ToArray();
            if (owned.Length == 0) return mirrors.Length == 0;
            if (owned.Length > 1) return false;
            var row = owned[0];
            if (row.Quantity <= 0 || row.Lifetime.Kind != InventoryLifetimeKind.ExpiresAt || InventoryExpiry.IsExpired(row, now)
                || row.Pet is not null || row.PetIdentity is not null || row.ArticleIdentity is not null
                || (row.Flags & InventorySlotRecord.WornFlagBit) != 0) return false;
            var held = Math.Max(0, (DateTimeOffset.FromUnixTimeMilliseconds(row.Lifetime.ExpiresAtUnixMilliseconds!.Value) - now).TotalDays);
            if (held + candidate.Amount > candidate.Maximum || held + candidate.Amount > (DateTimeOffset.MaxValue - now).TotalDays) return false;
            try { InventoryArticles.RequireMirrors(row, mirrors); }
            catch (InvalidDataException) { return false; }
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
    IEquipmentArticleResolver? articles = null, IPetEntitlements? pets = null,
    IReadOnlyList<LaboratoryRewardOptions>? bonusPool = null, ILogger? logger = null,
    IMessageSender<ServerNotice>? notices = null, IGameText? texts = null) : IDisposable
{
    private readonly SemaphoreSlim _capacity = new(options.MaxInFlightAccounts);
    private readonly ConcurrentDictionary<long, byte> _active = new();

    internal async ValueTask HandleAsync(ISession session, LaboratoryRequested request, CancellationToken ct)
    {
        if (!session.TryFeature<IPlayerSession>(out var player)) throw new InvalidOperationException("Science-room recipes require an authenticated player.");
        var owner = player.Identity.UserNum;
        if (player.Phase != SessionPhase.MainMenu || !await _capacity.WaitAsync(0, ct).ConfigureAwait(false))
        { await Refuse(LaboratoryRefusal.NotAvailable).ConfigureAwait(false); return; }
        var generation = player.Generation;
        if (!_active.TryAdd(owner, 0)) { _capacity.Release(); await Refuse(LaboratoryRefusal.InProgress).ConfigureAwait(false); return; }
        try
        {
            var snapshot = await store.ReadLaboratoryInventoryAsync(owner, scope, ct).ConfigureAwait(false);
            if (!CurrentLogin()) return;
            var now = time.GetUtcNow();
            var plan = LaboratoryPlanner.Plan(options, catalog, scope, owner, snapshot, request, now, choose, out var refusal, bonusPool);
            if (plan is null) { await Refuse(refusal).ConfigureAwait(false); return; }
            if (!CurrentScene()) { await Refuse(LaboratoryRefusal.SceneChanged).ConfigureAwait(false); return; }
            var result = await store.TryLaboratoryAsync(plan.Command with
            { ArticleInitialization = articles?.CreateInitialization(owner, now), PetInitialization = pets?.CreateInitialization(owner, now) }, ct).ConfigureAwait(false);
            if (!CurrentScene()) return;
            if (result is null) { await Refuse(LaboratoryRefusal.StoreRefused).ConfigureAwait(false); return; }
            // Install the committed ownership before the result can return control to the science-room inventory UI.
            await inventories.SendAsync(session, new(result.Inventory.CharacterBag, result.Inventory.EquippedBag)
                { IsCompleteSnapshot = true }, ct).ConfigureAwait(false);
            if (!CurrentScene()) return;
            var primaryItems = plan.PrimaryMembers;
            var primaryRow = result.GrantedItems.First(row => primaryItems.Contains(row.ItemId));
            await completions.SendAsync(session, new(request.Operation, plan.PrimaryReference, primaryRow.Lifetime, plan.Outcome,
                plan.ExtraStockIds.ElementAtOrDefault(0), plan.ExtraStockIds.ElementAtOrDefault(1)), ct).ConfigureAwait(false);

        }
        finally { _active.TryRemove(owner, out _); _capacity.Release(); }
        bool CurrentLogin() => session.TryFeature<IPlayerSession>(out var current) && ReferenceEquals(current, player);
        bool CurrentScene() => CurrentLogin() && player.Phase == SessionPhase.MainMenu && player.Generation == generation;
        async ValueTask Refuse(LaboratoryRefusal reason)
        {
            logger?.LogWarning("Science-room {Operation} refused for user {UserNum}: {Reason} (card {Card}, category {Category}, "
                + "materials [{Materials}], additives [{Additives}]).", request.Operation, owner, reason, request.CardItemId,
                request.Category, string.Join(",", request.Materials), string.Join(",", request.Additives));
            if (!CurrentLogin()) return;
            // The native result only signals failure (kr-812 sub_830A20 tests result < 0), so the reason travels as a notice.
            if (notices is not null && texts is not null)
                await notices.SendAsync(session, new ServerNotice(texts.Format(session.Profile, NoticeKey(reason))), ct).ConfigureAwait(false);
            await refusals.SendAsync(session, new(request.Operation), ct).ConfigureAwait(false);
        }
    }

    /// <summary>Gets the game message key that explains one refusal to the player.</summary>
    internal static string NoticeKey(LaboratoryRefusal reason) => reason switch
    {
        LaboratoryRefusal.InvalidRequest => "laboratory.refused.invalid_request",
        LaboratoryRefusal.ItemNotOwned => "laboratory.refused.item_not_owned",
        LaboratoryRefusal.ItemWorn => "laboratory.refused.item_worn",
        LaboratoryRefusal.ItemUnusable => "laboratory.refused.item_unusable",
        LaboratoryRefusal.GroupTimersDiffer => "laboratory.refused.group_timers_differ",
        LaboratoryRefusal.CardMissing => "laboratory.refused.card_missing",
        LaboratoryRefusal.WrongCard => "laboratory.refused.wrong_card",
        LaboratoryRefusal.AdditiveMissing => "laboratory.refused.additive_missing",
        LaboratoryRefusal.AdditiveNotAllowed => "laboratory.refused.additive_not_allowed",
        LaboratoryRefusal.AlreadyPermanent => "laboratory.refused.already_permanent",
        LaboratoryRefusal.DecomposeNeedsPermanent => "laboratory.refused.decompose_needs_permanent",
        LaboratoryRefusal.NoRecipe => "laboratory.refused.no_recipe",
        LaboratoryRefusal.Conflict => "laboratory.refused.conflict",
        LaboratoryRefusal.NotAvailable => "laboratory.refused.not_available",
        LaboratoryRefusal.InProgress => "laboratory.refused.in_progress",
        LaboratoryRefusal.SceneChanged => "laboratory.refused.scene_changed",
        LaboratoryRefusal.StoreRefused => "laboratory.refused.store_refused",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown science-room refusal."),
    };

    public void Dispose() => _capacity.Dispose();
}
