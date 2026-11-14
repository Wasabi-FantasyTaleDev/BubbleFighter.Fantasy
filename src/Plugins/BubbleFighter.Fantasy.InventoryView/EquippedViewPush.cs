using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InventoryView;

/// <summary>Publishes stored equipment on entry and the already committed selection on live changes.</summary>
/// <param name="logger">Logs each publication.</param>
/// <param name="localSender">The local view publications of the owning protocol.</param>
/// <param name="remoteSender">The observed-player view publications of the owning protocol.</param>
/// <param name="playerState">Persists the active slot and, when <paramref name="persistPresetPages"/> is set,
/// the materialized preset pages; may be null, which keeps the derived bar.</param>
/// <param name="selection">Reads the worn set the derived bar and the page seeding come from; may be null.</param>
/// <param name="persistPresetPages">Whether the preset bar is served from persisted pages (the
/// <c>persist_preset_pages</c> row switch, default off).</param>
/// <param name="events">Publishes the completed local snapshot after every dialect frame succeeds.</param>
/// <param name="inventory">Reads saved item modifiers while preserving the supplied equipped identities.</param>
internal sealed class EquippedViewPush(ILogger logger,
    IMessageSender<LocalEquipmentViewUpdated> localSender,
    IMessageSender<RemoteEquipmentViewUpdated> remoteSender,
    IPlayerInventoryState? playerState = null,
    IEquipmentSelection? selection = null,
    bool persistPresetPages = false, IEventBus? events = null, IInventoryRepository? inventory = null) : IEquippedViewPush, IWeaponPresetPageSource
{
    /// <inheritdoc />
    public ValueTask<WeaponPresetPage[]?> ReadAsync(long userNum, CancellationToken ct) =>
        persistPresetPages && playerState is not null
            ? playerState.GetWeaponPresetPagesAsync(userNum, ct)
            : ValueTask.FromResult<WeaponPresetPage[]?>(null);

    /// <inheritdoc />
    public async ValueTask SendRemoteAsync(ISession recipient, int userNum, int characterId,
        IReadOnlyList<int> equippedIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(equippedIds);
        var snapshot = equippedIds.ToArray();
        var attributes = await AttributesAsync(userNum, snapshot, ct).ConfigureAwait(false);
        await remoteSender.SendAsync(recipient, new(userNum, characterId, snapshot) { ItemAttributes = attributes }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask SendAsync(
        ISession session, int userNum, int characterId, IReadOnlyList<int> equippedIds, CancellationToken ct) =>
        SendCoreAsync(session, userNum, characterId, equippedIds, false, ct);

    /// <inheritdoc />
    public ValueTask SendSelectionAsync(
        ISession session, int userNum, int characterId, IReadOnlyList<int> equippedIds, CancellationToken ct) =>
        SendCoreAsync(session, userNum, characterId, equippedIds, true, ct);

    internal async ValueTask SendSuppliedAsync(EquipmentViewRequested request, CancellationToken ct)
    {
        var snapshot = new BubbleFighter.Fantasy.Contracts.Progress.RoundEquipmentSnapshot(
            request.EquippedItemIds, request.ItemAttributes ?? throw new ArgumentException("A supplied view requires explicit modifiers."));
        if (request.Remote)
            await remoteSender.SendAsync(request.Session, new(request.UserNum, request.CharacterItemId, snapshot.WornItemIds)
                { ItemAttributes = snapshot.ItemAttributes }, ct).ConfigureAwait(false);
        else
            await SendCoreAsync(request.Session, request.UserNum, request.CharacterItemId, snapshot.WornItemIds,
                false, ct, committedAttributes: snapshot.ItemAttributes, sceneReplay: true).ConfigureAwait(false);
    }

    /// <summary>Publishes the worn items and projected attributes from a validated copy of the committed character bag, with the selected character first.</summary>
    public ValueTask SendSnapshotAsync(ISession session, int userNum, int characterId,
        IReadOnlyList<InventorySlotRecord> characterBag, CancellationToken ct) =>
        SendCommittedAsync(session, userNum, characterId, characterBag, recordPage: true, ct);

    /// <summary>Publishes a granted preset switch's committed bag without recording the worn weapons into the switched-to page.</summary>
    public ValueTask SendPresetSwitchAsync(ISession session, int userNum, int characterId,
        IReadOnlyList<InventorySlotRecord> characterBag, CancellationToken ct) =>
        SendCommittedAsync(session, userNum, characterId, characterBag, recordPage: false, ct);

    private ValueTask SendCommittedAsync(ISession session, int userNum, int characterId,
        IReadOnlyList<InventorySlotRecord> characterBag, bool recordPage, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(characterBag);
        if (characterBag.Count > InventoryLimits.MaxInventorySlotsPerBag
            || characterBag.Any(row => row.UserNum != userNum || row.Bag != InventoryBag.CharacterBag))
            throw new ArgumentException("The committed equipment bag has an invalid owner or capacity.", nameof(characterBag));
        var rows = Array.AsReadOnly(characterBag.ToArray());
        var ids = rows.Where(row => (row.Flags & InventorySlotRecord.WornFlagBit) != 0).Select(row => row.ItemId).Distinct()
            .OrderBy(id => characterId > 0 && id == characterId ? 0 : 1).ToArray();
        return SendCoreAsync(session, userNum, characterId, ids, true, ct, rows, recordPage: recordPage);
    }

    private async ValueTask SendCoreAsync(ISession session, int userNum, int characterId,
        IReadOnlyList<int> equippedIds, bool selectionChange, CancellationToken ct,
        IReadOnlyList<InventorySlotRecord>? committedRows = null,
        IReadOnlyList<BubbleFighter.Fantasy.Contracts.Rooms.RoomItemAttribute>? committedAttributes = null, bool sceneReplay = false,
        bool recordPage = true)
    {
        ArgumentNullException.ThrowIfNull(equippedIds);
        // Own the supplied snapshot before a store read can yield to a caller changing its collection.
        var snapshot = equippedIds.ToArray();
        // Live changes carry the committed selection; another read can repair storage or observe a later change.
        if (selection is not null && !selectionChange && committedRows is null && committedAttributes is null)
        {
            var bag = await selection.ReadAsync(userNum, ct).ConfigureAwait(false);
            var worn = bag.Where(row => (row.Flags & InventorySlotRecord.WornFlagBit) != 0)
                .Select(row => row.ItemId).Distinct().ToArray();
            characterId = worn.FirstOrDefault(id => id is >= 300001 and <= 300031, characterId);
            snapshot = new[] { characterId }.Concat(worn.Where(id => id != characterId)).ToArray();
        }
        // 0x808F10 and 0x81B9C0 consume a primary/special pair, never two arbitrary bag entries.
        var primary = snapshot.FirstOrDefault(id => id is >= 202000 and < 205000);
        var special = snapshot.FirstOrDefault(id => id is >= 205000 and < 206000);
        var activeSlot = playerState is null ? (byte)0
            : await playerState.GetActiveWeaponPresetSlotAsync(userNum, ct).ConfigureAwait(false);
        var pages = persistPresetPages && playerState is not null
            ? await ReadOrMaterializePagesAsync(playerState, userNum, primary, special, ct).ConfigureAwait(false)
            : null;
        if (pages is not null && selectionChange && recordPage && primary != 0)
            pages = await RecordActivePageAsync(playerState!, userNum, pages, activeSlot, primary, special, ct).ConfigureAwait(false);
        var published = new LocalEquipmentViewUpdated(userNum, characterId, Array.AsReadOnly(snapshot), activeSlot,
            primary, special, pages is null ? null : Array.AsReadOnly(pages.ToArray()))
        {
            ItemAttributes = committedAttributes ?? (committedRows is not null && events is not null
                ? await InventoryPresentations.EquippedAsync(events, localSender.Protocol, committedRows, snapshot, ct).ConfigureAwait(false)
                : await AttributesAsync(userNum, snapshot, ct).ConfigureAwait(false)),
        };
        await localSender.SendAsync(session, published, ct).ConfigureAwait(false);
        if (events is not null)
            await events.SerialAsync(new EquipmentViewPublished(localSender.Protocol, session, published, selectionChange)
                { IsSceneReplay = sceneReplay }, ct)
                .ConfigureAwait(false);
        logger.LogInformation(
            "Session {Id} equipment view published: identity={Identity} character={Character} activeSlot={ActiveSlot} equipped=[{Equipped}] presetPages={PresetPages}.",
            session.Id, userNum, characterId, activeSlot, string.Join(", ", snapshot),
            pages is null ? "derived" : "persisted");
    }

    private async ValueTask<IReadOnlyList<BubbleFighter.Fantasy.Contracts.Rooms.RoomItemAttribute>> AttributesAsync(
        int userNum, IReadOnlyList<int> equippedIds, CancellationToken ct)
    {
        if (inventory is null || events is null) return [];
        var rows = await inventory.ListSlotsAsync(userNum, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, ct).ConfigureAwait(false);
        return await InventoryPresentations.EquippedAsync(events, localSender.Protocol, rows, equippedIds, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Records a committed selection into the active page. No client-to-server save packet exists (the equip
    /// request carries only the item, as in <c>packets/equipment-control.yaml</c>; the switch only the slot, <c>kr-812 sub_97A8D0</c>,
    /// <c>kr-1256 sub_808F10</c>), so the page a player edits is the page active when the equipment commits, and the
    /// server writes it. Other pages keep their weapons, which is what lets the tabs differ. Only a worn primary is
    /// recorded: removing it leaves the page's weapon, because a zero first word unbinds the page and the client
    /// would never offer it again (<c>kr-812 sub_97A8D0</c>, <c>kr-1256 sub_81B9C0</c>).
    /// </summary>
    /// <param name="playerState">The store holding the pages.</param>
    /// <param name="userNum">The player whose page is recorded.</param>
    /// <param name="pages">The pages just read for this publication.</param>
    /// <param name="activeSlot">The active page.</param>
    /// <param name="primary">The committed worn primary, nonzero.</param>
    /// <param name="special">The committed worn special, written to the second word by the server's convention.</param>
    /// <param name="ct">Cancellation for the store write.</param>
    /// <returns>The pages the publication carries.</returns>
    private static async ValueTask<WeaponPresetPage[]> RecordActivePageAsync(IPlayerInventoryState playerState, long userNum,
        WeaponPresetPage[] pages, byte activeSlot, int primary, int special, CancellationToken ct)
    {
        if (activeSlot >= pages.Length) return pages;
        var current = pages[activeSlot];
        if (current.Unknown0 == primary && current.Unknown4 == special) return pages;
        var updated = WeaponPresetPages.CopyTable(pages);
        updated[activeSlot] = current with { Unknown0 = primary, Unknown4 = special };
        await playerState.SetWeaponPresetPagesAsync(userNum, updated, ct).ConfigureAwait(false);
        return updated;
    }

    /// <summary>
    /// Reads the player's persisted preset pages, materializing them from the worn pair on first sight. Once pages
    /// exist they are never re-derived; a committed selection changes only the active page.
    /// </summary>
    /// <param name="playerState">The store holding the pages.</param>
    /// <param name="userNum">The player whose pages are read.</param>
    /// <param name="primary">The worn primary, the derived page's first word.</param>
    /// <param name="special">The worn special, the derived page's second word.</param>
    /// <param name="ct">Cancellation for the store round trips.</param>
    /// <returns>The five pages the publication carries.</returns>
    private static async ValueTask<WeaponPresetPage[]> ReadOrMaterializePagesAsync(
        IPlayerInventoryState playerState, long userNum, int primary, int special, CancellationToken ct)
    {
        return await playerState.GetOrCreateWeaponPresetPagesAsync(userNum,
            WeaponPresetPages.SeedFromWorn(primary, special), ct).ConfigureAwait(false);
    }
}
