using BubbleFighter.Fantasy.Contracts.Enhancement;
using System.Globalization;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Pets;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Pets.Data;
using BubbleFighter.Fantasy.Pets.Mileage;

namespace BubbleFighter.Fantasy.Pets;

internal sealed class PetRuntime(PetCatalog catalog, PetsOptions options, PetRules rules,
    IInventoryLoadoutStore inventory, IPetMutationStore mutations, InventoryLoadoutScope scope,
    IMessageSender<InventoryItemUseReplied> uses, IMessageSender<InventoryReplaced> inventories,
    IMessageSender<PetEvolved> evolutions, IEquippedViewPush equipment, TimeProvider time,
    IPetEntitlements? entitlements = null, PetMileagePolicy? mileage = null, IEquipmentArticleResolver? articles = null)
{
    internal async ValueTask<bool> UseAsync(ISession session, InventoryItemUseRequested request, CancellationToken ct)
    {
        if (!catalog.Items.TryGetValue(request.ItemId, out var food) || food.Function is not (19 or 20 or 21 or 39))
            return false;
        var outcome = new MutationOutcome(PetFailure.Unavailable, null, null);
        var player = RequirePlayer(session);
        if (Allowed(player) && request.Quantity == 1 && TryTarget(request.Parameters, out var target)
            && (food.Function is 19 or 20 || options.SkillResetsEnabled)
            && (food.Function == 39 ? request.SecondaryParameter is not null : request.SecondaryParameter is null))
        {
            int? selector = null;
            var valid = food.Function != 39 || int.TryParse(request.SecondaryParameter, NumberStyles.None,
                CultureInfo.InvariantCulture, out var selected) && (selector = selected) > 0;
            if (valid)
                outcome = await MutateAsync(player.Identity.UserNum, target, food.ItemId, !options.ManualLevelUp,
                    (state, pet, now) => food.Function is 19 or 20 ? rules.Feed(state, pet, food, now) : rules.Reset(state, selector),
                    ct, reset: food.Function is 21 or 39)
                    .ConfigureAwait(false);
            if (outcome.Rows is not null)
            {
                await PublishAsync(session, player.Identity.UserNum, outcome.Rows, ct).ConfigureAwait(false);
                if (outcome.Evolution is { } evolved)
                    await evolutions.SendAsync(session, new(evolved, target) { Rewards = outcome.RewardItemIds ?? [] }, ct).ConfigureAwait(false);
            }
        }
        var code = outcome.Failure switch
        {
            PetFailure.None => 0,
            PetFailure.Full when options.ManualLevelUp => 2,
            PetFailure.Cooldown => 16,
            PetFailure.MaximumGrade when options.ManualLevelUp && food.Function is 19 or 20 => 17,
            PetFailure.MaximumExperience => 18,
            _ => 1,
        };
        // cn-717 sub_8BB460 reads the installed skill and arms the food-animation suppression before the use reply.
        await uses.SendAsync(session, new(code, "") { ItemId = request.ItemId }, ct).ConfigureAwait(false);
        return true;
    }

    internal async ValueTask LevelUpAsync(ISession session, PetLevelUpRequested request,
        IMessageSender<PetLevelUpReplied> replies, CancellationToken ct)
    {
        var player = RequirePlayer(session);
        var outcome = Allowed(player) && options.ManualLevelUp
            ? await MutateAsync(player.Identity.UserNum, request.Target, 0, false, (state, pet, _) => rules.Grow(state, pet), ct).ConfigureAwait(false)
            : new MutationOutcome(PetFailure.Unavailable, null, null);
        if (outcome.Rows is not null)
            await PublishAsync(session, player.Identity.UserNum, outcome.Rows, ct).ConfigureAwait(false);
        await replies.SendAsync(session, new(request.Target, outcome.Failure == PetFailure.None ? 0 : 1), ct).ConfigureAwait(false);
    }

    internal async ValueTask ChangeAsync(ISession session, PetAbilityChangeRequested request,
        IMessageSender<PetAbilityChangeReplied> replies, CancellationToken ct)
    {
        var player = RequirePlayer(session);
        var outcome = Allowed(player) && PetArticleKeys.IsAddressable(request.Target)
            && catalog.Items.TryGetValue(request.ConsumableItemId, out var consumable) && consumable.Function == 38
            ? await MutateAsync(player.Identity.UserNum, request.Target, request.ConsumableItemId, false,
                (state, _, _) => rules.Change(state, request.OldSelector, request.NewSelector), ct).ConfigureAwait(false)
            : new MutationOutcome(PetFailure.Unavailable, null, null);
        if (outcome.Rows is not null)
            await PublishAsync(session, player.Identity.UserNum, outcome.Rows, ct).ConfigureAwait(false);
        await replies.SendAsync(session, new(request.Target, outcome.Failure == PetFailure.None ? 0 : 1, request.NewSelector), ct).ConfigureAwait(false);
    }

    private async ValueTask<MutationOutcome> MutateAsync(long userNum, ulong target, int consumable, bool requireEquipped,
        Func<PetProgression, PetItem, DateTimeOffset, PetTransition> transition, CancellationToken ct, bool reset = false)
    {
        if (target == 0 || target >= PetArticleKeys.First && !PetArticleKeys.IsAddressable(target))
            return new(PetFailure.Unavailable, null, null);
        if (reset && mileage is null && catalog.Items.TryGetValue(PetMileagePolicy.BalanceItemId, out var balance) && balance.Function == 1114)
            throw new InvalidOperationException("Native mileage resets require a complete mileage policy before mutation.");
        for (var attempt = 0; attempt < options.MutationAttempts; attempt++)
        {
            var rows = entitlements is null
                ? await inventory.ListLoadoutSlotsAsync(userNum, scope, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, ct).ConfigureAwait(false)
                : (await entitlements.PrepareAsync(userNum, ct).ConfigureAwait(false)).CharacterBag;
            // kr-1256 sub_806CE0 uses ordinary item identities below the Article lookup boundary.
            var matches = rows.Where(row => target < PetArticleKeys.First
                ? row.ItemId == (int)target : row.PetIdentity?.ArticleKey == target).Take(2).ToArray();
            if (matches.Length != 1) return new(PetFailure.Unavailable, null, null);
            var slot = matches[0];
            if (!catalog.Items.TryGetValue(slot.ItemId, out var pet) || pet.Function != PetCatalog.PetFunction)
                return new(PetFailure.Unavailable, null, null);
            var now = time.GetUtcNow();
            if (slot.Quantity <= 0 || InventoryExpiry.IsExpired(slot, now)
                || requireEquipped && (slot.Flags & InventorySlotRecord.WornFlagBit) == 0)
                return new(PetFailure.Unavailable, null, null);
            var state = slot.Pet?.Find(options.BuildKey)
                ?? throw new InvalidDataException("Pet operations require durable initialization for the selected build.");
            var planned = transition(state, pet, now);
            if (planned.Failure != PetFailure.None) return new(planned.Failure, null, null);
            PetMileageEffect? effect;
            try { effect = reset ? mileage?.Plan(userNum, consumable, rows, now) : null; }
            catch (InventoryGrantRejectedException) { return new(PetFailure.Unavailable, null, null); }
            var committed = await mutations.MutatePetWithResultAsync(new(slot, scope, planned.State!, consumable, requireEquipped, now)
                { Initialization = entitlements?.CreateInitialization(userNum, now), ArticleInitialization = articles?.CreateInitialization(userNum, now), Mileage = effect }, ct).ConfigureAwait(false);
            if (committed is not null) return new(PetFailure.None, committed.Snapshot, planned.Evolution, committed.RewardItemIds);
        }
        return new(PetFailure.Unavailable, null, null);
    }

    private async ValueTask PublishAsync(ISession session, long userNum, PetInventorySnapshot snapshot, CancellationToken ct)
    {
        var rows = snapshot.CharacterBag;
        await inventories.SendAsync(session, new(rows, snapshot.EquippedBag) { IsCompleteSnapshot = true }, ct).ConfigureAwait(false);
        var worn = rows.Where(row => (row.Flags & InventorySlotRecord.WornFlagBit) != 0).Select(row => row.ItemId).Distinct().ToArray();
        var character = worn.FirstOrDefault(id => id is >= 300001 and <= 300031);
        if (character != 0)
            await equipment.SendSnapshotAsync(session, checked((int)userNum), character, rows, ct).ConfigureAwait(false);
    }

    private static bool TryTarget(string text, out ulong target) =>
        ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out target) && target > 0;

    private static IPlayerSession RequirePlayer(ISession session) => session.TryFeature<IPlayerSession>(out var player)
        ? player : throw new InvalidOperationException("Pet operations require an authenticated player.");

    private static bool Allowed(IPlayerSession player) => player.Phase is SessionPhase.MainMenu or SessionPhase.Room or SessionPhase.Plaza;

    private sealed record MutationOutcome(PetFailure Failure, PetInventorySnapshot? Rows, int? Evolution,
        IReadOnlyList<int>? RewardItemIds = null);
}
