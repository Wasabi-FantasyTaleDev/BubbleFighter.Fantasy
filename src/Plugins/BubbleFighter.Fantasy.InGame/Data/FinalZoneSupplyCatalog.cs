using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Match;

namespace BubbleFighter.Fantasy.InGame.Data;

/// <summary>Reconstructs the native seeded contents of drone boxes and golden supplies.</summary>
internal sealed class FinalZoneSupplyCatalog(FinalZoneAmountRange weaponCount, FinalZoneAmountRange supplyCount,
    IReadOnlyList<FinalZoneSpawnRule> weapons, IReadOnlyList<FinalZoneSpawnRule> supplies)
{
    internal const string Path = "Config/GenDroneSpec.csv";

    private static string RewardPath(string stageDirectory) => $"Maps/{stageDirectory}/GenDroneRewardTable.csv";

    internal static FinalZoneSupplyCatalog? LoadForMap(IClientDataStore store, string stageDirectory,
        FinalZoneItemCatalog items, FinalZoneDataLimits limits, bool dronesEnabled) =>
        // The native rel_Sudden07_Apartment disables drones and has no reward table; other maps can still use it for drops.
        dronesEnabled || store.Exists(RewardPath(stageDirectory)) ? Load(store, stageDirectory, items, limits) : null;

    internal static FinalZoneSupplyCatalog Load(IClientDataStore store, string stageDirectory,
        FinalZoneItemCatalog items, FinalZoneDataLimits limits)
    {
        limits.Validate();
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in FinalZoneMapItems.Rows(store, Path, limits, "Filter", "Property", "Value"))
            if (row[0] == "Reward" && !properties.TryAdd(row[1], row[2]))
                throw new InvalidDataException($"{Path}: duplicate reward property '{row[1]}'.");
        string Property(string key) => properties.TryGetValue(key, out var value) ? value
            : throw new InvalidDataException($"{Path}: missing '{key}'.");
        var groups = FinalZoneMapItems.ReadGroups(store, RewardPath(stageDirectory), items, limits);
        IReadOnlyList<FinalZoneSpawnRule> Group(string key) => groups.TryGetValue(Property(key), out var rows) ? rows
            : throw new InvalidDataException($"{Path}: unknown reward group '{Property(key)}'.");
        // kr-1256 sub_D35740 draws both counts first, then draws distinct row identities within each group.
        return new(FinalZoneMapItems.Range(Property("WeaponLotteryCount"), 0, limits.MaximumBonusItems, Path),
            FinalZoneMapItems.Range(Property("SupplyLotteryCount"), 0, limits.MaximumBonusItems, Path),
            Group("WeaponSpawnGroupName"), Group("SupplySpawnGroupName"));
    }

    internal IReadOnlyList<FinalZoneBackpackEntry> Generate(int seed)
    {
        var random = new Area32Random(seed);
        var firstCount = random.Next(weaponCount.Minimum, checked(weaponCount.Maximum + 1));
        var secondCount = random.Next(supplyCount.Minimum, checked(supplyCount.Maximum + 1));
        var result = new List<FinalZoneBackpackEntry>();
        Draw(weapons, firstCount);
        Draw(supplies, secondCount);
        return Array.AsReadOnly(result.ToArray());

        void Draw(IReadOnlyList<FinalZoneSpawnRule> group, int count)
        {
            var available = group.ToList();
            for (var index = 0; index < count; index++)
            {
                var weight = available.Sum(row => row.Permyriad);
                if (weight == 0) break;
                var choice = random.Next(weight);
                var selected = 0;
                while (choice >= available[selected].Permyriad) choice -= available[selected++].Permyriad;
                var row = available[selected];
                available.RemoveAt(selected);
                var candidate = row.Main[random.Next(row.Main.Count)];
                var quantity = random.Next(candidate.Quantity.Minimum, checked(candidate.Quantity.Maximum + 1));
                result.Add(new(candidate.Item.Id, candidate.Item.WeaponId, quantity, new([], [])));
            }
        }
    }
}
