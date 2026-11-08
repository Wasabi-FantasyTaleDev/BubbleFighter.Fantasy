using System.Numerics;
using BubbleFighter.Fantasy.Contracts.ClientData;

namespace BubbleFighter.Fantasy.InGame.Data;

/// <summary>Reads existing map helper positions for an explicitly enabled neutral-box server policy.</summary>
internal static class ItemModeMapPoints
{
    internal static IReadOnlyList<Vector3> Load(IClientDataStore store, string stageDirectory,
        int maximumFileBytes, int maximumCandidates, int count)
    {
        if (string.IsNullOrWhiteSpace(stageDirectory) || stageDirectory.Contains(':') || stageDirectory.Contains('\\')
            || stageDirectory.Split('/').Any(part => part.Length == 0 || part is "." or ".."))
            throw new InvalidDataException("Item-mode fallback requires a relative map Directory without traversal.");
        var path = $"Maps/{stageDirectory}/track.wb";
        if (!store.Exists(path)) throw new InvalidDataException($"Item-mode fallback requires '{path}'.");
        var helpers = WorldBlockFile.LoadHelpers(store, path, maximumFileBytes, maximumCandidates);
        var stars = helpers.Where(helper => helper.Name.StartsWith("#sp_star_", StringComparison.Ordinal)).ToArray();
        var candidates = (stars.Length > 0 ? stars : helpers.Where(helper => helper.Name.StartsWith("#ic_", StringComparison.Ordinal)))
            .Select(helper => helper.Position).Distinct().ToArray();
        if (candidates.Length == 0)
            throw new InvalidDataException("Item-mode fallback found no supported star or supply helpers in track.wb.");
        return Select(candidates, count);
    }

    internal static IReadOnlyList<Vector3> Select(IReadOnlyList<Vector3> candidates, int count)
    {
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
        if (candidates.Count <= count) return candidates.ToArray();
        var centerX = candidates.Average(point => (double)point.X);
        var centerZ = candidates.Average(point => (double)point.Z);
        var remaining = candidates.ToList();
        var first = remaining.MinBy(point => DistanceSquared(point, centerX, centerZ));
        List<Vector3> selected = [first];
        remaining.Remove(first);
        while (selected.Count < count)
        {
            // Spread boxes across the map instead of taking several adjacent helpers in file order.
            var next = remaining.MaxBy(point => selected.Min(chosen => DistanceSquared(point, chosen.X, chosen.Z)));
            selected.Add(next);
            remaining.Remove(next);
        }
        return selected;
    }

    private static double DistanceSquared(Vector3 point, double x, double z) =>
        ((double)point.X - x) * ((double)point.X - x) + ((double)point.Z - z) * ((double)point.Z - z);
}
