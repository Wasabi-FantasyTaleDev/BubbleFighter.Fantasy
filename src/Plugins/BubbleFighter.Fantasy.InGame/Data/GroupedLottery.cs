using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.InGame.Data;

internal static class GroupedLottery
{
    internal static bool IsValid(InGameOptions options) =>
        ValidWeights(options.LotteryCrewCountWeights, 3) && ValidWeights(options.LotteryCoinCountWeights, 4)
        && options.LotteryGroupWeights.Length is > 0 and <= 16
        && options.LotteryGroupWeights.Select(g => g.Group).Distinct().Count() == options.LotteryGroupWeights.Length
        && options.LotteryGroupWeights.All(g => g.Weight is > 0 and <= 100000 && g.Group is not ("crew" or "coin"))
        && options.LotteryMaxDuplicates is >= 1 and <= 10 && options.LotteryRepeatWeightPercent is >= 1 and <= 100
        && GroupCapacity(options, "crew", 3) && GroupCapacity(options, "coin", 4)
        && options.LotteryItems.Where(p => options.LotteryGroupWeights.Any(g => g.Group == p.Group)).Select(Identity).Distinct().Count() * options.LotteryMaxDuplicates
            >= 10 - (HasGroup(options, "crew") ? 1 : 0) - (HasGroup(options, "coin") ? 1 : 0)
        && options.LotteryGroupWeights.Any(g => options.LotteryItems.Any(p => p.Group == g.Group));

    private static bool ValidWeights(int[] weights, int count) =>
        weights.Length == count && weights.All(w => w is >= 0 and <= 100000) && weights.Sum() > 0;

    private static bool HasGroup(InGameOptions options, string group) => options.LotteryItems.Any(p => p.Group == group);

    // Native workbooks can omit a guaranteed group entirely; present groups retain their capacity contract.
    private static bool GroupCapacity(InGameOptions options, string group, int minimum) =>
        !HasGroup(options, group) || options.LotteryItems.Where(p => p.Group == group).Select(Identity).Distinct().Count() * options.LotteryMaxDuplicates >= minimum;

    internal static LotteryItem[] Draw(InGameOptions options)
    {
        if (!IsValid(options)) throw new ConfigException("The grouped reward pool is invalid.");
        var crew = HasGroup(options, "crew") ? Pick(Enumerable.Range(1, 3).ToArray(), options.LotteryCrewCountWeights) : 0;
        var coin = HasGroup(options, "coin") ? Pick(Enumerable.Range(1, 4).ToArray(), options.LotteryCoinCountWeights) : 0;
        var cards = new List<LotteryItem>(10);
        for (var i = 0; i < crew; i++) cards.Add(Reward(options, "crew", cards));
        for (var i = 0; i < coin; i++) cards.Add(Reward(options, "coin", cards));
        while (cards.Count < 10)
        {
            var groups = options.LotteryGroupWeights.Where(g => options.LotteryItems.Any(p => p.Group == g.Group
                && cards.Count(c => Identity(c) == Identity(p)) < options.LotteryMaxDuplicates)).ToArray();
            if (groups.Length == 0) throw new ConfigException("Reward groups cannot fill the board within the duplicate limit.");
            cards.Add(Reward(options, Pick(groups, groups.Select(g => g.Weight).ToArray()).Group, cards));
        }
        var result = cards.ToArray();
        Random.Shared.Shuffle(result);
        return result;
    }

    private static string Identity(LotteryItem prize) => prize.Lucci > 0 ? $"coin:{prize.Lucci}"
        : prize.Components.Length > 0 ? "set:" + string.Join(",", prize.Components.Select(c => c.ItemId).Order())
        : $"item:{prize.ItemId}";

    private static LotteryItem Reward(InGameOptions options, string group, IReadOnlyList<LotteryItem> drawn)
    {
        var candidates = options.LotteryItems.Where(p => p.Group == group
            && drawn.Count(c => Identity(c) == Identity(p)) < options.LotteryMaxDuplicates).ToArray();
        int RepeatWeight(LotteryItem prize, int weight) => Math.Max(1, (int)(weight * Math.Pow(
            options.LotteryRepeatWeightPercent / 100.0, drawn.Count(c => Identity(c) == Identity(prize)))));
        if (group == "coin") return Pick(candidates, candidates.Select(p => RepeatWeight(p, p.Weight * 100)).ToArray());
        // Choose equipment before duration so extra sale rows do not multiply its chance.
        var families = candidates.GroupBy(Identity).ToArray();
        var variants = Pick(families, families.Select(f => RepeatWeight(f.First(), 10000)).ToArray()).ToArray();
        return Pick(variants, variants.Select(p => p.Weight).ToArray());
    }

    private static T Pick<T>(IReadOnlyList<T> values, IReadOnlyList<int> weights)
    {
        var ticket = Random.Shared.Next(weights.Sum());
        for (var i = 0; i < values.Count; i++)
        {
            ticket -= weights[i];
            if (ticket < 0) return values[i];
        }
        throw new InvalidOperationException("Reward weights could not resolve a selection.");
    }
}

