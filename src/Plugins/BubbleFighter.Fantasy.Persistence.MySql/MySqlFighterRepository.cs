using BubbleFighter.Fantasy.Contracts.Fighters;
using BubbleFighter.Fantasy.Abstractions.Persistence;

namespace BubbleFighter.Fantasy.Persistence.MySql;

/// <summary>MySQL store for the <c>fighters</c> table from migration 0004.</summary>
public sealed class MySqlFighterRepository(IDbConnectionFactory connections) : IFighterRepository
{
    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<FighterRecord>> ListByUserNumAsync(long userNum, CancellationToken ct)
    {
        if (userNum <= 0)
            throw new ArgumentOutOfRangeException(nameof(userNum), userNum, "user_num must be a positive login id.");

        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var rows = await session.Db.Queryable<FighterRow>()
            .Where(f => f.UserNum == userNum)
            .OrderBy(f => f.Slot)
            .Take(FighterLimits.MaxFighterSlots)
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(r => new FighterRecord(r.UserNum, r.Slot, r.CharacterType, r.Level, r.EquippedItemId, r.Nickname))
            .ToList();
    }

    /// <inheritdoc />
    public async ValueTask UpsertAsync(FighterRecord record, CancellationToken ct)
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

        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var row = new FighterRow
        {
            UserNum = record.UserNum,
            Slot = record.Slot,
            CharacterType = record.CharacterType,
            Level = record.Level,
            EquippedItemId = record.EquippedItemId,
            Nickname = record.Nickname,
            UpdatedAt = DateTime.UtcNow,
        };
        var rows = await SugarSql.UpsertAsync(session.Db, row, [],
            [nameof(FighterRow.CharacterType), nameof(FighterRow.Level), nameof(FighterRow.EquippedItemId), nameof(FighterRow.Nickname)],
            [(nameof(FighterRow.UpdatedAt), "UTC_TIMESTAMP()")], ct).ConfigureAwait(false);
        if (rows < 1)
            throw new InvalidOperationException($"fighters upsert for user_num {record.UserNum} slot {record.Slot} wrote {rows} row(s).");
    }
}
