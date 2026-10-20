using BubbleFighter.Fantasy.Contracts.Inventory;

namespace BubbleFighter.Fantasy.Shop.Data;

/// <summary>
/// Process-local hold queue for S2C 803 container-2 equip records. A purchase
/// grants the bag slot immediately but queues the equip record here instead of
/// sending it, because the client skips container 2 entirely until the player
/// opens the inventory dialog (<c>sub_9AEB10</c> guards on the my-fighter
/// object, and the weapon category <c>+1632</c> is only written by the client's
/// own <c>DlgInventory2</c> refresh, <c>sub_5F7C90</c>). The client's C2S 803
/// (<c>gcpReqOpenInventory</c>, stamped at <c>sub_932E10</c> right after that
/// refresh) is the only signal that the dialog is open, so it is the flush
/// trigger.
/// </summary>
/// <remarks>
/// <para>
/// One record set per player, bounded on both axes: at most
/// <see cref="MaxRecordsPerPlayer"/> records per player and at most
/// <see cref="MaxPlayers"/> tracked players. Both overflow loudly with an
/// <see cref="InvalidOperationException"/> rather than growing the table or
/// dropping a record, because a silently dropped equip record is a purchase the
/// player paid for and never saw equipped.
/// </para>
/// <para>
/// The queue is deliberately not persisted. A held record is only meaningful
/// while the session that bought the item is alive; a restart clears it, which
/// is the same lifetime the in-memory wallet and session stores already have.
/// </para>
/// </remarks>
public sealed class EquipHoldStore
{
    /// <summary>Maximum container-2 records held for one player.</summary>
    public const int MaxRecordsPerPlayer = 16;

    /// <summary>Maximum players with a held record set at the same time.</summary>
    public const int MaxPlayers = 65_536;

    private readonly object _gate = new();
    private readonly Dictionary<long, List<EquippedSlotRecord>> _byUser = [];

    /// <summary>
    /// Queues one container-2 record for a player. The records are sent in the
    /// order they were queued, which is purchase order.
    /// </summary>
    /// <param name="userNum">Login <c>players.user_num</c> of the buyer.</param>
    /// <param name="record">The record to hold.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the player already holds <see cref="MaxRecordsPerPlayer"/>
    /// records, or when <see cref="MaxPlayers"/> players are already tracked.
    /// </exception>
    public void Remember(long userNum, EquippedSlotRecord record)
    {
        lock (_gate)
        {
            if (!_byUser.TryGetValue(userNum, out var rows))
            {
                if (_byUser.Count >= MaxPlayers)
                {
                    throw new InvalidOperationException(
                        $"Equip hold store already tracks {_byUser.Count} players (cap {MaxPlayers}); refusing to queue a record for player {userNum}.");
                }

                rows = [];
                _byUser[userNum] = rows;
            }

            if (rows.Count >= MaxRecordsPerPlayer)
            {
                throw new InvalidOperationException(
                    $"Player {userNum} already holds {rows.Count} S2C 803 container-2 equip record(s); the cap is "
                    + $"EquipHoldStore.MaxRecordsPerPlayer = {MaxRecordsPerPlayer}. The client has not opened the inventory dialog since those purchases; "
                    + "raise the cap only when the client is shown to hold a larger set.");
            }

            rows.Add(record);
        }
    }

    /// <summary>
    /// Returns whether one more record can be held for a player right now. A purchase checks this
    /// before charging, so a full queue refuses the purchase instead of failing after the commit.
    /// </summary>
    /// <param name="userNum">Login <c>players.user_num</c>.</param>
    public bool CanHold(long userNum)
    {
        lock (_gate)
        {
            return _byUser.TryGetValue(userNum, out var rows)
                ? rows.Count < MaxRecordsPerPlayer
                : _byUser.Count < MaxPlayers;
        }
    }

    /// <summary>
    /// Queues one record like <see cref="Remember"/>, but reports a full queue by returning
    /// <see langword="false"/> instead of throwing. Used after a purchase has committed, when the
    /// item is already in the bag and throwing would only turn a paid purchase into an error reply.
    /// </summary>
    /// <param name="userNum">Login <c>players.user_num</c> of the buyer.</param>
    /// <param name="record">The record to hold.</param>
    /// <returns><see langword="true"/> when the record was queued.</returns>
    public bool TryRemember(long userNum, EquippedSlotRecord record)
    {
        lock (_gate)
        {
            if (!CanHold(userNum))
                return false;
            Remember(userNum, record);
            return true;
        }
    }

    /// <summary>
    /// Removes and returns every record held for one player, so a second C2S 803
    /// on the same session sends nothing. The removal and the read happen under
    /// one lock, so two concurrent dialog opens cannot both receive the set.
    /// </summary>
    /// <param name="userNum">Login <c>players.user_num</c>.</param>
    /// <param name="records">The held records, empty when nothing is pending.</param>
    /// <returns><see langword="true"/> when at least one record was held.</returns>
    public bool TryTake(long userNum, out IReadOnlyList<EquippedSlotRecord> records)
    {
        lock (_gate)
        {
            if (!_byUser.TryGetValue(userNum, out var rows) || rows.Count == 0)
            {
                records = [];
                return false;
            }

            records = [.. rows];
            _byUser.Remove(userNum);
            return true;
        }
    }

    /// <summary>Returns how many records are currently held for one player.</summary>
    /// <param name="userNum">Login <c>players.user_num</c>.</param>
    public int Count(long userNum)
    {
        lock (_gate)
        {
            return _byUser.TryGetValue(userNum, out var rows) ? rows.Count : 0;
        }
    }
}
