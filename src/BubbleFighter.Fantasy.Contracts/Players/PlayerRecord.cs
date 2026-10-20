namespace BubbleFighter.Fantasy.Contracts.Players;

/// <summary>
/// The storage-independent player identity resolved by shared lobby logic and handed to a build's dialect.
/// </summary>
/// <param name="UserNum">Numeric user identity written into every identity slot.</param>
/// <param name="Username">Login name presented by the client in <c>gcpReqLogin</c>.</param>
/// <param name="Nickname">Display name; the client matches itself in room player lists by it.</param>
/// <param name="Level">Stored progression level used by login and room identity projections.</param>
public sealed record PlayerRecord(long UserNum, string Username, string Nickname, int Level)
{
    /// <summary>Gets the player's avatar material color index; zero retains the native default.</summary>
    public ushort ColorIndex { get; init; }
}
