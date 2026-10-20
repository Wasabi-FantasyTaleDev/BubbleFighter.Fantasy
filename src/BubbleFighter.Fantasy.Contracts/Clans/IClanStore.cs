namespace BubbleFighter.Fantasy.Contracts.Clans;

/// <summary>Which empty C2S last touched a stored player row.</summary>
public enum ClanOpenKind
{
    /// <summary>C2S 25 bounce from clan-server opcode <c>0x3EA</c>.</summary>
    ClanServerAck = 0,

    /// <summary>C2S 2040 <c>DlgClanMain</c> / session init.</summary>
    MainOpen = 1,

    /// <summary>C2S 2041 <c>FrmClanAttendance</c> check-in.</summary>
    Attendance = 2,
}

/// <summary>
/// One remembered clan-UI open. Search and roster rows are not stored: the 200-byte
/// <c>FrmClanSearch</c> stride and the 96-byte member stride are clan-server payloads
/// and must not be invented on this dialect.
/// </summary>
/// <param name="UserNum">Player id when known; 0 when the session has no proven identity.</param>
/// <param name="Kind">Which C2S was handled.</param>
public sealed record ClanOpenRecord(long UserNum, ClanOpenKind Kind);

/// <summary>
/// Membership snapshot. <see cref="ClanId"/> 0 means the player is not in a clan.
/// No member nicknames or ranks are carried: those fields are unproven on the game port.
/// </summary>
/// <param name="UserNum">Player id.</param>
/// <param name="ClanId">Stored clan id, or 0 when absent.</param>
public sealed record ClanMembership(long UserNum, long ClanId);

/// <summary>
/// Persists clan-UI opens and reads membership through the configured storage provider.
/// List queries are ordered by identity and fail when their requested bound is exceeded.
/// </summary>
public interface IClanStore
{
    /// <summary>Records an open. A missing player id is ignored.</summary>
    /// <param name="record">The open to remember.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask RememberOpenAsync(ClanOpenRecord record, CancellationToken ct);

    /// <summary>Returns the stored membership, or a zero clan id when none exists.</summary>
    /// <param name="userNum">Player id.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<ClanMembership> GetMembershipAsync(long userNum, CancellationToken ct);

    /// <summary>Returns the last remembered open, or null when no open has been recorded.</summary>
    /// <param name="userNum">Player id; zero represents a session with no proven identity.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<ClanOpenRecord?> FindLastOpenAsync(long userNum, CancellationToken ct);

    /// <summary>Returns stored clan ids in ascending order, failing instead of truncating an oversized snapshot.</summary>
    /// <param name="maxCount">Maximum result count, from one through Int32.MaxValue minus one.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<IReadOnlyList<long>> ListClanIdsAsync(int maxCount, CancellationToken ct);

    /// <summary>Returns member user ids in ascending order, failing instead of truncating an oversized snapshot.</summary>
    /// <param name="clanId">Positive clan id.</param>
    /// <param name="maxCount">Maximum result count, from one through Int32.MaxValue minus one.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<IReadOnlyList<long>> ListMemberUserNumsAsync(long clanId, int maxCount, CancellationToken ct);
}
