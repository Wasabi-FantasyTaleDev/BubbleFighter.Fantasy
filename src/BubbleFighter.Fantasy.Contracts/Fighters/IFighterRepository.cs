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

    /// <summary>Inserts or replaces one fighter slot after range checks.</summary>
    /// <param name="record">The slot to store.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask UpsertAsync(FighterRecord record, CancellationToken ct);
}
