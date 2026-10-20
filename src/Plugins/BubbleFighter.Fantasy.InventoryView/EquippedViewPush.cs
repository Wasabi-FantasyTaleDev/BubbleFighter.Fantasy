using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InventoryView;

/// <summary>Publishes stored equipment on entry and the already committed selection on live changes.</summary>
internal sealed class EquippedViewPush(ILogger logger,
    IMessageSender<LocalEquipmentViewUpdated> localSender,
    IMessageSender<RemoteEquipmentViewUpdated> remoteSender,
    IPlayerInventoryState? playerState = null,
    IEquipmentSelection? selection = null) : IEquippedViewPush
{
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
        await localSender.SendAsync(session, new(userNum, characterId, snapshot, activeSlot, primary, special), ct)
            .ConfigureAwait(false);
        logger.LogInformation(
            "Session {Id} equipment view published: identity={Identity} character={Character} activeSlot={ActiveSlot} equipped=[{Equipped}].",
            session.Id, userNum, characterId, activeSlot, string.Join(", ", snapshot));
    }
}
