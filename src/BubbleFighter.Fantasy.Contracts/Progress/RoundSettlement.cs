namespace BubbleFighter.Fantasy.Contracts.Progress;

/// <summary>
/// What one round credits one player: experience applied to the player's stored progress and Lucci added to the
/// wallet, at most once per round start.
/// </summary>
/// <param name="MatchId">
/// The round start's identity, the same for every seat of that start and never reused: 1 to 64 ASCII letters,
/// digits or hyphens. A second settlement with the same match and player changes nothing.
/// </param>
/// <param name="UserNum">Login <c>players.user_num</c>.</param>
/// <param name="LucciEarned">Lucci the round earned, 0 or more.</param>
/// <param name="Advance">
/// Computes the progress after the round from the progress stored when the store holds the player's lock, so a
/// change made between the round's read and this write is not lost. It must return the same player and a level of
/// 1 or more, and it must not call back into the store.
/// </param>
public sealed record RoundSettlementCommand(string MatchId, long UserNum, int LucciEarned,
    Func<PlayerProgress, PlayerProgress> Advance)
{
    /// <summary>The longest match identity a store accepts.</summary>
    public const int MaxMatchIdLength = 64;

    /// <summary>Throws when the command cannot be stored.</summary>
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Advance);
        if (string.IsNullOrEmpty(MatchId) || MatchId.Length > MaxMatchIdLength
            || !MatchId.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-'))
            throw new ArgumentException("A match id is 1 to 64 ASCII letters, digits or hyphens.", nameof(MatchId));
        if (UserNum <= 0)
            throw new ArgumentOutOfRangeException(nameof(UserNum), UserNum, "user_num must be a positive login id.");
        if (LucciEarned < 0)
            throw new ArgumentOutOfRangeException(nameof(LucciEarned), LucciEarned, "A round never takes Lucci.");
    }

    /// <summary>Applies <see cref="Advance"/> and checks what it returned.</summary>
    /// <param name="stored">The progress the store holds for the player.</param>
    /// <returns>The progress to store.</returns>
    public PlayerProgress AdvanceChecked(PlayerProgress stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        var after = Advance(stored) ?? throw new InvalidOperationException("A round settlement computed no progress.");
        if (after.UserNum != UserNum || after.Level < 1 || after.Exp < 0)
            throw new InvalidOperationException(
                $"A round settlement for user_num {UserNum} computed an invalid progress (user {after.UserNum}, level {after.Level}, exp {after.Exp}).");
        return after;
    }
}

/// <summary>Whether a settlement changed the player.</summary>
public enum RoundSettlementOutcome
{
    /// <summary>The progress and the Lucci were stored now.</summary>
    Settled,

    /// <summary>The same match had already settled this player; nothing changed and the stored result is returned.</summary>
    AlreadySettled,
}

/// <summary>The result of one settlement; a repeated settlement returns the first one's progress and Lucci.</summary>
/// <param name="Outcome">Whether this call changed the player.</param>
/// <param name="Before">The progress stored before the round.</param>
/// <param name="After">The progress the round left.</param>
/// <param name="LucciEarned">The Lucci the round credited.</param>
/// <param name="LucciBalance">The wallet's Lucci now.</param>
public sealed record RoundSettlementResult(RoundSettlementOutcome Outcome, PlayerProgress Before, PlayerProgress After,
    int LucciEarned, int LucciBalance);
