using System.Numerics;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Rooms;

namespace BubbleFighter.Fantasy.InGame.Data;

internal sealed partial class MapCatalog
{
    private const string ZombiePveWidget = "wgtZombiePVE";
    // kr-1256 sub_C80220, cn-774 sub_895CF0 and tw-454 sub_709630 cycle each team's index modulo six.
    private const int TeamSpawnCycle = 6;
    private readonly Dictionary<string, (string Key, int Anchors)> _zombiePveStages = new(StringComparer.OrdinalIgnoreCase);

    private void LoadZombiePveStages(IClientDataStore store, int bytes, int helpers)
    {
        // kr-1256 sub_9A0880, cn-774 sub_BA0A50 and tw-454 sub_A7E150 need a MapMeta record;
        // a false mode flag sets a rule bit but does not suppress the concrete start request.
        foreach (var key in _meta.Maps)
        {
            var stage = _meta.Get(key, "Directory");
            if (!_meta.IsSet(key, "isRelease") || string.IsNullOrWhiteSpace(stage)) continue;
            var ini = $"Maps/{stage}/{Path.GetFileName(stage)}_ZombiePve.ini";
            var world = $"Maps/{stage}/track.wb";
            if (!store.Exists(ini) || store.Length(ini) == 0 || !store.Exists(world)) continue;
            var nodes = WorldBlockFile.LoadHelpers(store, world, bytes, helpers);
            Vector3? Point(string name)
            {
                var matches = nodes.Where(node => node.Name == name).Take(2).ToArray();
                return matches.Length == 1 ? matches[0].Position : null;
            }
            if (Point("#mapMinPoint") is not { } min || Point("#mapMaxPoint") is not { } max
                || min.X >= max.X || min.Y > max.Y || min.Z >= max.Z) continue;
            var positions = new HashSet<Vector3>();
            for (var ordinal = 1; ordinal <= TeamSpawnCycle; ordinal++)
            {
                if (Point(FormattableString.Invariant($"#spawn_T2{ordinal}")) is not { } point
                    || point.X < min.X || point.X > max.X || point.Y < min.Y || point.Y > max.Y
                    || point.Z < min.Z || point.Z > max.Z || !positions.Add(point)) break;
            }
            if (positions.Count == 0) continue;
            _zombiePveStages.Add(key, (key, positions.Count));
            IndexZombiePveBoss(store, ini, key);
        }
    }

    /// <summary>Resolves a concrete or random cooperative stage with all the start anchors this roster will consume.</summary>
    internal string? ZombiePveMap(string mapKey, string selection, int players, Random random)
    {
        if (players < 1) return null;
        var needed = Math.Min(players, TeamSpawnCycle);
        bool Fits(string key) => _zombiePveStages.TryGetValue(key, out var stage) && stage.Anchors >= needed;
        var marker = RandomMapMarkers.Contains(selection) ? selection : RandomMapMarkers.Contains(mapKey) ? mapKey : null;
        if (marker is null)
        {
            foreach (var key in new[] { mapKey, selection })
                if (Fits(key)) return _zombiePveStages[key].Key;
            return null;
        }
        if (marker == "Random_Hot")
        {
            var pool = _hot.GetValueOrDefault(ZombiePveWidget)?.Where(row => Fits(row.Map)).ToArray();
            if (pool is not { Length: > 0 }) return null;
            var resolved = pool.FirstOrDefault(row => string.Equals(row.Map, mapKey, StringComparison.OrdinalIgnoreCase)).Map;
            return _zombiePveStages[resolved ?? PickWeighted(pool, random)].Key;
        }
        var plain = ModePool((int)GameModeId.ZombiePve).Where(Fits).ToArray();
        return plain.Length == 0 ? null : plain.FirstOrDefault(key => string.Equals(key, mapKey, StringComparison.OrdinalIgnoreCase))
            ?? plain[random.Next(plain.Length)];
    }
}
