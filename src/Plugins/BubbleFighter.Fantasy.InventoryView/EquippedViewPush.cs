using BubbleFighter.Fantasy.Contracts.Inventory;
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
internal sealed class EquippedViewPush(ILogger logger,
    IMessageSender<LocalEquipmentViewUpdated> localSender,
    IMessageSender<RemoteEquipmentViewUpdated> remoteSender,
    IPlayerInventoryState? playerState = null,
    IEquipmentSelection? selection = null,
    bool persistPresetPages = false) : IEquippedViewPush, IWeaponPresetPageSource
{
    /// <inheritdoc />
    public ValueTask<WeaponPresetPage[]?> ReadAsync(long userNum, CancellationToken ct) =>
        persistPresetPages && playerState is not null
            ? playerState.GetWeaponPresetPagesAsync(userNum, ct)
            : ValueTask.FromResult<WeaponPresetPage[]?>(null);

    /// <inheritdoc />
    public ValueTask SendRemoteAsync(ISession recipient, int userNum, int characterId,
        IReadOnlyList<int> equippedIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(equippedIds);
        return remoteSender.SendAsync(recipient, new(userNum, characterId, equippedIds.ToArray()), ct);
    }

    /// <inheritdoc />
    public ValueTask SendAsync(
        ISession session, int userNum, int characterId, IReadOnlyList<int> equippedIds, CancellationToken ct) =>
        SendCoreAsync(session, userNum, characterId, equippedIds, false, ct);

    /// <inheritdoc />
    public ValueTask SendSelectionAsync(
        ISession session, int userNum, int characterId, IReadOnlyList<int> equippedIds, CancellationToken ct) =>
        SendCoreAsync(session, userNum, characterId, equippedIds, true, ct);

    private async ValueTask SendCoreAsync(ISession session, int userNum, int characterId,
        IReadOnlyList<int> equippedIds, bool selectionChange, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(equippedIds);
        // Own the supplied snapshot before a store read can yield to a caller changing its collection.
        var snapshot = equippedIds.ToArray();
        // Live changes carry the committed selection; another read can repair storage or observe a later change.
        if (selection is not null && !selectionChange)
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
        await localSender.SendAsync(session,
            new(userNum, characterId, snapshot, activeSlot, primary, special, pages), ct).ConfigureAwait(false);
        logger.LogInformation(
            "Session {Id} equipment view published: identity={Identity} character={Character} activeSlot={ActiveSlot} equipped=[{Equipped}] presetPages={PresetPages}.",
            session.Id, userNum, characterId, activeSlot, string.Join(", ", snapshot),
            pages is null ? "derived" : "persisted");
    }

    /// <summary>
    /// Reads the player's persisted preset pages, materializing them from the worn pair on first sight. The
    /// swap chain has no client-to-server save packet, so this is the only writer: once pages exist they win
    /// over every later worn change, which is what makes the bar server-owned rather than re-derived.
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
