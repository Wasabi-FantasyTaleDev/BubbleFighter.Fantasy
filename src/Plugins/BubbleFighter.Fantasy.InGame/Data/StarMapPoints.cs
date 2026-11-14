using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Match;

namespace BubbleFighter.Fantasy.InGame.Data;

/// <summary>
/// Reads a map's star points from its <c>track.wb</c> helpers the way the host client builds its map report (kr-812
/// sub_50C840, kr-1256 sub_6DA5D0): a <c>#sp_star_&lt;first&gt;_&lt;period&gt;_&lt;n&gt;</c> helper is a small star, a
/// <c>#sp_bigStar_&lt;first&gt;_&lt;period&gt;_&lt;n&gt;</c> helper a big one, and the name's third and fourth segments are numbers.
/// </summary>
internal static class StarMapPoints
{
    internal static IReadOnlyList<RoundStarPoint> Load(IClientDataStore store, string stageDirectory,
        int maximumFileBytes, int maximumCandidates, int maximumPoints)
    {
        if (string.IsNullOrWhiteSpace(stageDirectory) || stageDirectory.Contains(':') || stageDirectory.Contains('\\')
            || stageDirectory.Split('/').Any(part => part.Length == 0 || part is "." or ".."))
            throw new InvalidDataException("Map stars require a relative map Directory without traversal.");
        var path = $"Maps/{stageDirectory}/track.wb";
        if (!store.Exists(path)) throw new InvalidDataException($"Map stars require '{path}'.");
        var points = WorldBlockFile.LoadHelpers(store, path, maximumFileBytes, maximumCandidates)
            .Select(Parse).OfType<RoundStarPoint>().ToArray();
        if (points.Length > maximumPoints)
            throw new InvalidDataException($"'{path}' has {points.Length} star points, more than {maximumPoints}.");
        return points;
    }

    /// <summary>Reads one helper as a star point, or returns null for a helper that is not one.</summary>
    internal static RoundStarPoint? Parse(WorldBlockHelper helper)
    {
        var segments = helper.Name.Split('_');
        if (segments.Length < 2 || segments[0] != "#sp") return null;
        var kind = segments[1] switch { "star" => 1, "bigStar" => 2, _ => 0 };
        var position = helper.Position;
        if (kind == 0 || !float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
            return null;
        return new RoundStarPoint(position.X, position.Y, position.Z, kind,
            segments.Length > 2 ? Number(segments[2]) : 0, segments.Length > 3 ? Number(segments[3]) : 0);
    }

    // A segment's leading decimal digits; one without any reads as zero.
    private static int Number(string segment)
    {
        long value = 0;
        foreach (var digit in segment)
        {
            if (digit is < '0' or > '9') break;
            value = Math.Min(int.MaxValue, value * 10 + (digit - '0'));
        }
        return (int)value;
    }
}
