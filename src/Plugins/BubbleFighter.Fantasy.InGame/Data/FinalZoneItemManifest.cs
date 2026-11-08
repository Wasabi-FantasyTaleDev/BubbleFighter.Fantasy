using BubbleFighter.Fantasy.Contracts.Match;

namespace BubbleFighter.Fantasy.InGame.Data;

/// <summary>Builds a bounded, explicit item manifest from the current map's native spawn rules.</summary>
internal static class FinalZoneItemManifest
{
    internal static IReadOnlyList<FinalZoneMapItem> Create(FinalZoneMapItems map, Random random,
        int firstObjectId, int maximumObjects)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(firstObjectId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumObjects);
        _ = checked(firstObjectId + (maximumObjects - 1));
        foreach (var spot in map.Spots)
            foreach (var rule in spot.Rules)
                if (rule.Main.Concat(rule.Bonus).Any(candidate => candidate.Item.Group == "AutoItem"))
                    // kr-1256 sub_D36270 creates a different object class for category 23; kr-1256 sub_CDB180 does not.
                    throw new InvalidDataException("FinalZone explicit item initialization does not cover this map's automatic terrain objects.");
        var manifest = new List<FinalZoneMapItem>();
        foreach (var spot in map.Spots)
        {
            var pick = random.Next(10000);
            FinalZoneSpawnRule? chosen = null;
            foreach (var rule in spot.Rules)
            {
                if (pick < rule.Permyriad)
                {
                    chosen = rule;
                    break;
                }
                pick -= rule.Permyriad;
            }
            if (chosen is null) continue;
            Add(chosen.Main[random.Next(chosen.Main.Count)], spot);
            var bonuses = Amount(chosen.BonusCount);
            for (var index = 0; index < bonuses; index++)
                Add(chosen.Bonus[random.Next(chosen.Bonus.Count)], spot);
        }
        return Array.AsReadOnly(manifest.ToArray());

        int Amount(FinalZoneAmountRange range) => (int)random.NextInt64(range.Minimum, (long)range.Maximum + 1);

        void Add(FinalZoneItemCandidate candidate, FinalZoneItemSpot spot)
        {
            if (manifest.Count >= maximumObjects)
                throw new InvalidDataException("FinalZone map generation exceeds the configured round object limit.");
            var position = spot.Position;
            // kr-1256 sub_D2B900 initializes these descriptor words and reinforcement vectors to zero/empty.
            manifest.Add(new(firstObjectId + manifest.Count, candidate.Item.Id, Amount(candidate.Quantity),
                position.X, position.Y, position.Z, 0, 0, 0, false, new([], [])));
        }
    }
}
