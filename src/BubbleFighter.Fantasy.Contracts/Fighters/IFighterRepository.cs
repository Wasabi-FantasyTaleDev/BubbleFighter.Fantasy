namespace BubbleFighter.Fantasy.Contracts.Fighters;

/// <summary>
/// Loads and replaces the local / partner fighter slots used by login S2C 133
/// and the nested <c>FighterInfo</c> on S2C 105.
/// </summary>
public interface IFighterRepository
{
    /// <summary>Returns stored fighter slots for one player, at most <see cref="FighterLimits.MaxFighterSlots"/> rows.</summary>
    /// <param name="userNum">Login <c>players.user_num</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<IReadOnlyList<FighterRecord>> ListByUserNumAsync(long userNum, CancellationToken ct);

    /// <summary>
    /// Inserts or replaces exactly one fighter slot after range checks: a positive user number, a
    /// slot below <see cref="FighterLimits.MaxFighterSlots"/>, a level of at least one, a required
    /// (possibly empty) nickname, a live-fighter <c>character_type</c> in 1..31 (0 allowed for the
    /// unused partner), and an equipped item id of 0 or 300000+T with T in 1..31.
    /// </summary>
    /// <param name="record">The slot to store; its user number, slot, level and types must be in range.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when any numeric range above is violated.</exception>
    /// <exception cref="ArgumentException">Thrown when the nickname is null.</exception>
    ValueTask UpsertAsync(FighterRecord record, CancellationToken ct);
}
