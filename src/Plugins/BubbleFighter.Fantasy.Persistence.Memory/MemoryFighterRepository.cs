using BubbleFighter.Fantasy.Contracts.Fighters;

namespace BubbleFighter.Fantasy.Persistence.Memory;

/// <summary>
/// In-process store for the local / partner fighter slots, mirroring the guard
/// behaviour of the MySQL store: the same slot, level, nickname, character-type
/// and equipped-item checks run before any state is touched, an upsert replaces
/// one slot, and the list reads the slots back in slot order.
/// </summary>
public sealed class MemoryFighterRepository(MemoryPlayerTable table) : IFighterRepository
{
    /// <inheritdoc />
    public ValueTask<IReadOnlyList<FighterRecord>> ListByUserNumAsync(long userNum, CancellationToken ct)
    {
        if (userNum <= 0)
            throw new ArgumentOutOfRangeException(nameof(userNum), userNum, "user_num must be a positive login id.");

        var entry = table.Find(userNum);
        if (entry is null)
            return ValueTask.FromResult<IReadOnlyList<FighterRecord>>([]);
        lock (entry.Gate)
            return ValueTask.FromResult<IReadOnlyList<FighterRecord>>([.. entry.Fighters.Values.OrderBy(f => f.Slot)]);
    }

    /// <inheritdoc />
    public ValueTask UpsertAsync(FighterRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.UserNum <= 0)
            throw new ArgumentOutOfRangeException(nameof(record), record.UserNum, "user_num must be a positive login id.");
        if (record.Slot >= FighterLimits.MaxFighterSlots)
            throw new ArgumentOutOfRangeException(nameof(record), record.Slot, "fighter slot must be 0 (local) or 1 (partner).");
        if (record.Level < 1)
            throw new ArgumentOutOfRangeException(nameof(record), record.Level, "fighter level must be at least 1.");
        if (record.Nickname is null)
            throw new ArgumentException("fighter nickname is required (empty is legal for an unused partner).", nameof(record));

        if (record.Slot == 0)
        {
            if (record.CharacterType is < 1 or > 31)
                throw new ArgumentOutOfRangeException(nameof(record), record.CharacterType, "live fighter character_type must be a FighterConfig Field id in 1..31.");
        }
        else if (record.CharacterType is < 0 or > 31)
        {
            throw new ArgumentOutOfRangeException(nameof(record), record.CharacterType, "partner character_type must be 0 or a Field id in 1..31.");
        }

        if (record.EquippedItemId != 0)
        {
            var type = record.EquippedItemId - 300000;
            if (type is < 1 or > 31)
                throw new ArgumentOutOfRangeException(nameof(record), record.EquippedItemId, "equipped_item_id must be 0 or 300000+T for Field id T in 1..31.");
        }

        var entry = table.GetOrAdd(record.UserNum);
        lock (entry.Gate)
            entry.Fighters[record.Slot] = record;
        return ValueTask.CompletedTask;
    }
}
