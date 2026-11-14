using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Enhancement;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Pets;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Contracts.Titles;
using BubbleFighter.Fantasy.Titles.Data;

namespace BubbleFighter.Fantasy.Titles;

internal sealed class TitleRuntime(TitlesOptions options, TitleCatalog catalog, ITitleMedalStore medals,
    IInventoryLoadoutStore inventory, InventoryLoadoutScope scope, IEquipmentSelection selection,
    IEquippedViewPush view, IEventBus events, IMessageSender<InventoryReplaced> inventories,
    IMessageSender<TitleMakeReplied> made, IMessageSender<TitleSetReplied> equipped,
    IMessageSender<MedalInfoReplied> balances, TimeProvider time,
    IPetEntitlements? pets = null, IEquipmentArticleResolver? articles = null,
    IFavoriteWeaponProgress? favorite = null, IMessageSender<TitleProgressNotified>? progress = null) : IDisposable
{
    /// <summary>The client's condition kind for Favorite (kr-1256 sub_113D820 returns 6 for that label).</summary>
    internal const int FavoriteConditionId = 6;

    private readonly SemaphoreSlim _capacity = new(options.MaxInFlightAccounts);
    private readonly ConcurrentDictionary<long, byte> _active = new();
    private readonly TimeZoneInfo? _acquisitionZone = options.AcquisitionTimeZone.Length == 0
        ? null : TimeZones.Resolve(options.AcquisitionTimeZone);

    internal async ValueTask HandleMakeAsync(ISession session, TitleMakeRequested request, CancellationToken ct)
    {
        var player = Player(session);
        if (request.UserNumber != player.Identity.UserNum || player.Phase != SessionPhase.MainMenu
            || !catalog.Titles.TryGetValue(request.TitleId, out var title) || !title.UsesMedals
            || title.Concealed || title.HasUnsupportedBonuses || title.HasUnsupportedConditions
            || (title.BonusGrants.Count != 0 || title.BonusMedals.Count != 0) && !medals.SupportsAcquisitionBonuses
            || title.Requirements.Count == 0 || !await EnterAsync(player.Identity.UserNum, ct).ConfigureAwait(false))
        {
            await RefuseAsync().ConfigureAwait(false);
            return;
        }
        try
        {
            if (title.Window is { } window && (_acquisitionZone is null || !window.Contains(time.GetUtcNow(), _acquisitionZone)))
            {
                await made.SendAsync(session, new(-10, request.TitleId, 0), ct).ConfigureAwait(false);
                return;
            }
            var generation = player.Generation;
            var owner = player.Identity.UserNum;
            var bag = await inventory.ListLoadoutSlotsAsync(owner, scope, InventoryBag.CharacterBag,
                InventoryLimits.MaxInventorySlotsPerBag, ct).ConfigureAwait(false);
            var matching = bag.Where(row => row.ItemId == title.GrantTerm.ItemId).Take(2).ToArray();
            if (matching.Length > 1 || player.Phase != SessionPhase.MainMenu || player.Generation != generation)
            {
                await RefuseAsync().ConfigureAwait(false);
                return;
            }
            var now = time.GetUtcNow();
            if (title.Window is { } currentWindow && (_acquisitionZone is null || !currentWindow.Contains(now, _acquisitionZone)))
            {
                await made.SendAsync(session, new(-10, request.TitleId, 0), ct).ConfigureAwait(false);
                return;
            }
            var command = new TitleClaimCommand(owner, scope, title.TitleId, title.StockId,
                title.Requirements.Select(row => new TitleMedalCount(row.MedalId, row.Count)).ToArray(),
                title.GrantTerm, matching.SingleOrDefault(), now)
            {
                BonusGrants = title.BonusGrants,
                BonusMedals = title.BonusMedals,
                ArticleInitialization = articles?.CreateInitialization(owner, now),
                PetInitialization = pets?.CreateInitialization(owner, now),
            };
            var committed = await medals.TryClaimAsync(command, ct).ConfigureAwait(false);
            if (!CurrentScene()) return;
            if (committed is null)
            {
                await RefuseAsync().ConfigureAwait(false);
                return;
            }
            // kr-812 sub_885F40 and kr-1256 sub_EAF420 read installed stock ownership before refreshing the UI.
            await inventories.SendAsync(session, new(committed.Inventory.CharacterBag, committed.Inventory.EquippedBag)
                { IsCompleteSnapshot = true }, ct).ConfigureAwait(false);
            if (!CurrentScene()) return;
            await balances.SendAsync(session, new(1, VisibleMedals(committed.Medals)), ct).ConfigureAwait(false);
            if (!CurrentScene()) return;
            // Result zero is the ordinary success branch in kr-812 sub_8230F0 and kr-1256 sub_9B0F70.
            await made.SendAsync(session, new(0, title.TitleId, title.StockId), ct).ConfigureAwait(false);

            bool CurrentScene() => player.Phase == SessionPhase.MainMenu && player.Generation == generation;
        }
        finally { Leave(player.Identity.UserNum); }

        // Both native handlers route unrecognized non-success codes to the generic unmet-condition notice.
        ValueTask RefuseAsync() => made.SendAsync(session, new(-1, request.TitleId, 0), ct);
    }

    /// <summary>
    /// Grants every Favorite title whose Master-weapon count is reached, without medals, then installs the titles' progress. It runs at
    /// login and after each Favorite commit; a busy account skips the run, because the next trigger evaluates the same committed state.
    /// </summary>
    internal async ValueTask EvaluateFavoriteAsync(ISession session, CancellationToken ct)
    {
        if (favorite is null || progress is null) throw new InvalidOperationException("Favorite titles are not configured.");
        var player = Player(session);
        var titles = catalog.Titles.Values.Where(title => title.FavoriteMasters is not null).OrderBy(title => title.TitleId).ToArray();
        if (titles.Length == 0 || player.Phase != SessionPhase.MainMenu) return;
        var owner = player.Identity.UserNum;
        var generation = player.Generation;
        if (!await EnterAsync(owner, ct).ConfigureAwait(false)) return;
        try
        {
            var masters = await favorite.CountMasterWeaponsAsync(owner, ct).ConfigureAwait(false);
            if (!CurrentScene()) return;
            var bag = await inventory.ListLoadoutSlotsAsync(owner, scope, InventoryBag.CharacterBag,
                InventoryLimits.MaxInventorySlotsPerBag, ct).ConfigureAwait(false);
            foreach (var title in titles)
            {
                if (masters < title.FavoriteMasters) continue;
                var matching = bag.Where(row => row.ItemId == title.GrantTerm.ItemId).Take(2).ToArray();
                // Permanent ownership is the grant's only record: a held title is never granted twice.
                if (!CurrentScene() || matching.Length > 1
                    || matching.Length == 1 && matching[0].Quantity > 0 && matching[0].Lifetime.Kind == InventoryLifetimeKind.Permanent) continue;
                var now = time.GetUtcNow();
                var committed = await medals.TryClaimAsync(new TitleClaimCommand(owner, scope, title.TitleId, title.StockId, [],
                    title.GrantTerm, matching.SingleOrDefault(), now)
                {
                    IsAutomaticGrant = true,
                    ArticleInitialization = articles?.CreateInitialization(owner, now),
                    PetInitialization = pets?.CreateInitialization(owner, now),
                }, ct).ConfigureAwait(false);
                if (committed is null) continue;
                bag = committed.Inventory.CharacterBag;
                if (!CurrentScene()) return;
                // kr-1256 sub_EAF420 reads installed stock ownership, so the complete inventory is published first.
                await inventories.SendAsync(session, new(committed.Inventory.CharacterBag, committed.Inventory.EquippedBag)
                    { IsCompleteSnapshot = true }, ct).ConfigureAwait(false);
            }
            if (!CurrentScene()) return;
            // The client divides current by required for its bar, so the count is capped at the requirement.
            await progress.SendAsync(session, new(titles.Select(title => new TitleProgress(title.TitleId,
                [new(FavoriteConditionId, Math.Min(masters, title.FavoriteMasters!.Value), title.FavoriteMasters.Value)])).ToArray()), ct)
                .ConfigureAwait(false);
        }
        finally { Leave(owner); }

        bool CurrentScene() => player.Phase == SessionPhase.MainMenu && player.Generation == generation;
    }

    internal async ValueTask HandleSetAsync(ISession session, TitleSetRequested request, CancellationToken ct)
    {
        var player = Player(session);
        if (request.UserNumber != player.Identity.UserNum || request.Action is not (0 or 1)
            || player.Phase != SessionPhase.MainMenu || !catalog.Titles.TryGetValue(request.TitleId, out var title)
            || title.RequiresPartner || !await EnterAsync(player.Identity.UserNum, ct).ConfigureAwait(false))
        {
            await RefuseAsync().ConfigureAwait(false);
            return;
        }
        try
        {
            var generation = player.Generation;
            try
            {
                await events.SerialAsync(new EquipmentSelectionStarting(session), ct).ConfigureAwait(false);
                if (player.Phase != SessionPhase.MainMenu || player.Generation != generation)
                {
                    await RefuseAsync().ConfigureAwait(false);
                    return;
                }
                var owner = player.Identity.UserNum;
                var bag = await inventory.ListLoadoutSlotsAsync(owner, scope, InventoryBag.CharacterBag,
                    InventoryLimits.MaxInventorySlotsPerBag, ct).ConfigureAwait(false);
                var matching = bag.Where(row => row.ItemId == title.GrantTerm.ItemId).Take(2).ToArray();
                if (matching.Length != 1 || matching[0].Quantity <= 0
                    || matching[0].Lifetime.Kind == InventoryLifetimeKind.Unspecified
                    || InventoryExpiry.IsExpired(matching[0], time.GetUtcNow()) || player.Phase != SessionPhase.MainMenu
                    || player.Generation != generation)
                {
                    await RefuseAsync().ConfigureAwait(false);
                    return;
                }
                var committed = await selection.ApplyOwnedSnapshotAsync(owner, matching[0], request.Action == 0, ct)
                    .ConfigureAwait(false);
                if (committed is null)
                {
                    await RefuseAsync().ConfigureAwait(false);
                    return;
                }
                var ids = Array.AsReadOnly(committed.CharacterBag.Where(row => (row.Flags & InventorySlotRecord.WornFlagBit) != 0)
                    .Select(row => row.ItemId).Distinct().ToArray());
                var characters = ids.Where(id => id is >= 300001 and <= 300031).ToArray();
                if (characters.Length != 1 || ids.Count > InventoryLimits.MaxEquippedSlots)
                    throw new InvalidDataException("A title selection committed an invalid equipment snapshot.");
                var attributes = await InventoryPresentations.EquippedAsync(events, options.Protocol,
                    committed.CharacterBag, ids, ct).ConfigureAwait(false);
                await events.SerialAsync(new EquipmentSelectionCommitted(session, owner, characters[0], ids)
                    { ItemAttributes = attributes }, ct).ConfigureAwait(false);
                await inventories.SendAsync(session, new(committed.CharacterBag, committed.EquippedBag)
                    { IsCompleteSnapshot = true }, ct).ConfigureAwait(false);
                await view.SendSnapshotAsync(session, owner, characters[0], committed.CharacterBag, ct).ConfigureAwait(false);
                // kr-812 sub_823640 and kr-1256 sub_9B1680 consume one for equipped and two for removed.
                await equipped.SendAsync(session, new(request.Action == 0 ? 1 : 2, title.TitleId), ct).ConfigureAwait(false);
            }
            finally { await events.SerialAsync(new EquipmentSelectionSettled(session), CancellationToken.None).ConfigureAwait(false); }
        }
        finally { Leave(player.Identity.UserNum); }

        ValueTask RefuseAsync() => equipped.SendAsync(session, new(0, request.TitleId), ct);
    }

    internal async ValueTask HandleMedalsAsync(ISession session, MedalInfoRequested request, CancellationToken ct)
    {
        var player = Player(session);
        if (request.TargetUserNumber <= 0 || player.Phase is SessionPhase.Authenticated or SessionPhase.Migrating)
        {
            await balances.SendAsync(session, new(2, []), ct).ConfigureAwait(false);
            return;
        }
        // kr-812 sub_6C2F80 and kr-1256 sub_931B10 carry the selected player's user number, including remote players.
        var result = await medals.ReadAsync(request.TargetUserNumber, scope.Key, ct).ConfigureAwait(false);
        await balances.SendAsync(session, new(1, VisibleMedals(result)), ct).ConfigureAwait(false);
    }

    private IReadOnlyList<TitleMedalCount> VisibleMedals(IReadOnlyList<TitleMedalCount> rows) =>
        Array.AsReadOnly(rows.Where(row => catalog.Medals.ContainsKey(row.MedalId)).ToArray());

    private static IPlayerSession Player(ISession session) => session.TryFeature<IPlayerSession>(out var player)
        ? player : throw new InvalidOperationException("Title operations require an authenticated player.");

    private async ValueTask<bool> EnterAsync(long owner, CancellationToken ct)
    {
        if (!await _capacity.WaitAsync(0, ct).ConfigureAwait(false)) return false;
        if (_active.TryAdd(owner, 0)) return true;
        _capacity.Release();
        return false;
    }

    private void Leave(long owner) { _active.TryRemove(owner, out _); _capacity.Release(); }

    /// <summary>Releases the operation budget after the owning row has drained its registered request handlers.</summary>
    public void Dispose() => _capacity.Dispose();
}
