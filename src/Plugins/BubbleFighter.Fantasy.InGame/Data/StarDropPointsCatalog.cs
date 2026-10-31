using System.Text.Json;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Handlers;

namespace BubbleFighter.Fantasy.InGame.Data;

/// <summary>Loads the per-map star positions captured from the kr-1256 star-mode client.</summary>
internal static class StarDropPointsCatalog
{
    private const int SmallTag = 3;
    private const int BigTag = 4;
    private const int MaxPointsPerKind = 4096;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Loads one map's small and big star seeds, or null when the map has no capture file.</summary>
    internal static IReadOnlyList<RoundStarSeed>? Load(string directory, string mapKey)
    {
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(mapKey))
            return null;
        var safeMap = Path.GetFileName(mapKey);
        if (!string.Equals(safeMap, mapKey, StringComparison.Ordinal))
            throw new InvalidDataException($"Star drop map key '{mapKey}' contains a path separator.");

        var path = Path.Combine(directory, mapKey + ".json");
        if (!File.Exists(path))
        {
            path = Directory.EnumerateFiles(directory, "*.json").FirstOrDefault(file =>
                string.Equals(Path.GetFileNameWithoutExtension(file), mapKey, StringComparison.OrdinalIgnoreCase)) ?? "";
            if (path.Length == 0)
                return null;
        }

        using var stream = File.OpenRead(path);
        var set = JsonSerializer.Deserialize<StarDropPointSet>(stream, JsonOptions)
            ?? throw new InvalidDataException($"Star drop file '{path}' is empty.");
        if (set.Small.Length > MaxPointsPerKind || set.Big.Length > MaxPointsPerKind)
            throw new InvalidDataException($"Star drop file '{path}' exceeds {MaxPointsPerKind} points per kind.");

        var seeds = new List<RoundStarSeed>(set.Small.Length + set.Big.Length);
        Add(set.Small, 1, SmallTag);
        Add(set.Big, 2, BigTag);
        return seeds;

        void Add(StarDropPoint[] points, int kind, int tag)
        {
            foreach (var point in points)
            {
                var x = (float)point.X;
                var y = (float)point.Y;
                var z = (float)point.Z;
                if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
                    throw new InvalidDataException($"Star drop file '{path}' contains a non-finite position.");
                seeds.Add(new RoundStarSeed(kind, new RoundLucciPoint(x, y, z, 0, 0, 0), tag));
            }
        }
    }

    private sealed class StarDropPointSet
    {
        public StarDropPoint[] Small { get; set; } = [];
        public StarDropPoint[] Big { get; set; } = [];
    }

    private sealed class StarDropPoint
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
    }
}
