using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>
/// Publishes stored inventory state of one player onto a live session as the
/// S2C 802 / 803 / 808 / 810 push family plus the single-slot S2C 803 restate.
/// The lobby row owns the wire shapes; other rows (for example the shop purchase
/// grant and the equipment wear path) consume this service by interface so the
/// layouts have exactly one implementation.
/// </summary>
public interface IInventoryPushService
{
    /// <summary>
    /// Publishes all changed worn flags, including items displaced by an exclusive equipment
    /// selection. Slots are pushed strictly in order, one restated S2C 803 each; the returned
    /// task completes only after the last push does.
    /// </summary>
    /// <param name="session">The session to push to.</param>
    /// <param name="slots">The changed bag slots, in push order.</param>
    /// <param name="ct">Cancellation token.</param>
    async ValueTask PushSlotUpdatesAsync(ISession session, IReadOnlyList<InventorySlotRecord> slots, CancellationToken ct)
    {
        foreach (var slot in slots) await PushSlotUpdateAsync(session, slot, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads the player's stored inventory and pushes the full snapshot to the
    /// session. An empty bag pushes empty snapshots, which is a legal client
    /// state.
    /// </summary>
    /// <param name="session">The session to push to.</param>
    /// <param name="userNum">Login <c>players.user_num</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask PushInventoryAsync(ISession session, long userNum, CancellationToken ct);

    /// <summary>Publishes a purchase inventory snapshot followed by the newly granted slot updates.</summary>
    /// <param name="session">The buying session.</param>
    /// <param name="userNum">The owning player.</param>
    /// <param name="grants">Committed rows to restate in the incremental update.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask PushPurchaseAsync(ISession session, long userNum, IReadOnlyList<InventorySlotRecord> grants, CancellationToken ct);

    /// <summary>
    /// Pushes one S2C 803 whose container 1 restates a single bag slot (the
    /// <c>sub_10D8550</c> reader the 802 family shares) with containers 2 and 3
    /// empty. This is the frame the client's equip path expects after a C2S 804 /
    /// 805 reply: the container-1 walk both upserts the bag map entry, whose
    /// flags bit carries the worn state, and applies the model, so one frame
    /// closes state and render.
    /// </summary>
    /// <param name="session">The session to push to.</param>
    /// <param name="slot">The restated bag slot with its updated flags.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask PushSlotUpdateAsync(ISession session, InventorySlotRecord slot, CancellationToken ct);
}
