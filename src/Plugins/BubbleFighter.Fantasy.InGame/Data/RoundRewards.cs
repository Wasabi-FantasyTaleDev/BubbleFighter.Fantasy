namespace BubbleFighter.Fantasy.InGame.Data;

/// <summary>
/// What one seat earned in a round and the experience figures the S2C 614 row shows: row +56
/// level, +368 the level's starting experience, +372 the total after the round, +376 the next
/// level's start, +380 the experience earned and +384 the Lucci earned. The destinations are the
/// client's own — kr-1256 <c>sub_429620</c> stores the row's 27 stat words there in wire order
/// (packets/score.yaml <c>ScoreRow@kr-1256</c> notes the wire order is not the destination order) —
/// but no client consumer names these fields: the meanings are pinned from recorded result rows
/// (captured evidence), matching the kr-812 baseline row's named experience figures. The client ships
/// no reward formula, so the amounts are deployment options.
/// </summary>
/// <param name="Level">The level after the round.</param>
/// <param name="LevelStart">Experience at which <paramref name="Level"/> starts.</param>
/// <param name="Exp">Total experience after the round.</param>
/// <param name="NextLevelStart">Experience at which the next level starts.</param>
/// <param name="ExpEarned">Experience the round earned.</param>
/// <param name="LucciEarned">Lucci the round earned.</param>
internal sealed record RoundReward(int Level, long LevelStart, long Exp, long NextLevelStart, int ExpEarned, int LucciEarned)
{
    internal static RoundReward ComputeConfigured(LevelTable levels, long expBefore, bool won, int kills,
        int score, InGameOptions options)
    {
        var original = options.ScoreBasedRewards ? null : Compute(levels, expBefore, won, kills,
            options.RewardBaseExp, options.RewardExpPerKill, options.RewardWinMultiplier, options.RewardLucciPercent);
        var experience = original?.ExpEarned ?? Math.Floor(options.RewardCompletionExp
            + Math.Max(0, score) * options.RewardExpPerScore + (won ? options.RewardVictoryExp : 0));
        var lucci = original?.LucciEarned ?? Math.Floor(options.RewardCompletionLucci
            + Math.Max(0, score) * options.RewardLucciPerScore + (won ? options.RewardVictoryLucci : 0));
        var earned = checked((int)Math.Floor(experience * options.RewardExpMultiplier));
        var coins = checked((int)Math.Floor(lucci * options.RewardLucciMultiplier));
        if (options.ScoreBasedRewards)
        {
            earned = Math.Max(1, earned);
            coins = Math.Max(1, coins);
        }
        var total = checked(expBefore + earned);
        var level = levels.LevelFor(total);
        return new(level, levels.Start(level), total, levels.Start(level + 1), earned, coins);
    }

    /// <summary>
    /// Computes a seat's reward: <paramref name="baseExp"/> plus <paramref name="expPerKill"/> per
    /// kill, times <paramref name="winMultiplier"/> for the winning team, and Lucci as
    /// <paramref name="lucciPercent"/> percent of that experience.
    /// </summary>
    internal static RoundReward Compute(LevelTable levels, long expBefore, bool won, int kills,
        int baseExp, int expPerKill, int winMultiplier, int lucciPercent)
    {
        var earned = checked((baseExp + expPerKill * Math.Max(kills, 0)) * (won ? winMultiplier : 1));
        var exp = checked(expBefore + earned);
        var level = levels.LevelFor(exp);
        return new RoundReward(level, levels.Start(level), exp, levels.Start(level + 1), earned,
            checked(earned * lucciPercent / 100));
    }
}
