namespace BubbleFighter.Fantasy.Contracts.Progress;

/// <summary>A player's level and total experience.</summary>
/// <param name="UserNum">Login <c>players.user_num</c>.</param>
/// <param name="Level">The level, 1 or more.</param>
/// <param name="Exp">Total experience earned, 0 or more.</param>
public sealed record PlayerProgress(long UserNum, int Level, long Exp);
