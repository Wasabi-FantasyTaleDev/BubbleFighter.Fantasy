using System.Globalization;
using System.Numerics;
using System.Text;
using BubbleFighter.Fantasy.Contracts.ClientData;
using Microsoft.VisualBasic.FileIO;

namespace BubbleFighter.Fantasy.InGame.Data;

internal readonly record struct FinalZoneAmountRange(int Minimum, int Maximum);
internal sealed record FinalZoneItemCandidate(FinalZoneItemSpec Item, FinalZoneAmountRange Quantity);
internal sealed record FinalZoneSpawnRule(int Permyriad, IReadOnlyList<FinalZoneItemCandidate> Main,
    IReadOnlyList<FinalZoneItemCandidate> Bonus, FinalZoneAmountRange BonusCount);
internal sealed record FinalZoneItemSpot(string Cluster, int Serial, Vector3 Position, bool Valid,
    IReadOnlyList<FinalZoneSpawnRule> Rules);

/// <summary>Native spawn points, cluster mappings and weighted item choices for one concrete FinalZone map.</summary>
internal sealed class FinalZoneMapItems(IReadOnlyList<FinalZoneItemSpot> spots)
{
    internal IReadOnlyList<FinalZoneItemSpot> Spots { get; } = spots;

    internal static FinalZoneMapItems Load(IClientDataStore store, string stageDirectory,
        FinalZoneItemCatalog items, FinalZoneDataLimits limits)
    {
        limits.Validate();
        if (string.IsNullOrWhiteSpace(stageDirectory) || stageDirectory.Contains(':') || stageDirectory.Contains('\\')
            || stageDirectory.Split('/').Any(part => part.Length == 0 || part is "." or ".."))
            throw new InvalidDataException("FinalZone requires a relative map Directory without traversal.");
        // kr-1256 sub_CD5BD0 prefixes the MapMeta Directory with Maps/ for the Area32 resource paths.
        var prefix = $"Maps/{stageDirectory}/";
        var groups = ReadGroups(store, prefix + "GenSpawnTable.csv", items, limits);
        var clusters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in Rows(store, prefix + "GenClusterTable.csv", limits, "ClusterName", "SpawnGroupName"))
        {
            if (row[0].Length == 0 || !groups.ContainsKey(row[1]) || !clusters.TryAdd(row[0], row[1]))
                throw new InvalidDataException($"{prefix}: duplicate cluster or unknown spawn group '{row[0]}'.");
        }
        var spots = new List<FinalZoneItemSpot>();
        var identities = new HashSet<(string Cluster, int Serial)>();
        foreach (var row in Rows(store, prefix + "GenSpotList.csv", limits, "ClusterName", "SpotSn", "SpotPoint", "SpotValid"))
        {
            if (!bool.TryParse(row[3], out var valid)) throw new InvalidDataException($"{prefix}: invalid SpotValid.");
            var serial = Integer(row[1], 0, int.MaxValue, prefix);
            if (row[0].Length == 0 || !identities.Add((row[0], serial)))
                throw new InvalidDataException($"{prefix}: empty or duplicate spawn point.");
            var coordinates = row[2].Split(',');
            if (coordinates.Length != 3) throw new InvalidDataException($"{prefix}: a SpotPoint needs three coordinates.");
            var point = new Vector3(Coordinate(coordinates[0], prefix), Coordinate(coordinates[1], prefix), Coordinate(coordinates[2], prefix));
            // kr-1256 sub_D34BC0 uses <Default> only when the spot's cluster has no mapping.
            var group = clusters.GetValueOrDefault(row[0], "<Default>");
            if (!groups.TryGetValue(group, out var rules)) throw new InvalidDataException($"{prefix}: missing spawn group '{group}'.");
            // kr-1256 sub_D2DAA0 retains SpotValid; kr-1256 sub_D34BC0 iterates every returned spot without testing it.
            spots.Add(new(row[0], serial, point, valid, rules));
        }
        if (spots.Count == 0) throw new InvalidDataException($"{prefix}: no generated-item spots.");
        return new(Array.AsReadOnly(spots.ToArray()));
    }

    internal static Dictionary<string, IReadOnlyList<FinalZoneSpawnRule>> ReadGroups(IClientDataStore store,
        string path, FinalZoneItemCatalog items, FinalZoneDataLimits limits)
    {
        var groups = new Dictionary<string, List<FinalZoneSpawnRule>>(StringComparer.Ordinal);
        foreach (var row in Rows(store, path, limits, "SpawnGroupName", "Permyriad", "MainCandidates", "BonusCandidates", "BonusSpawnCount"))
        {
            var main = Candidates(row[2], items, limits, path);
            var bonus = Candidates(row[3], items, limits, path);
            var count = Range(row[4], 0, limits.MaximumBonusItems, path);
            if (row[0].Length == 0 || main.Count == 0 || (bonus.Count == 0) != (count.Maximum == 0))
                throw new InvalidDataException($"{path}: missing main candidate or inconsistent bonus choices/count.");
            var weight = Integer(row[1], 0, 10000, path);
            if (!groups.TryGetValue(row[0], out var rules)) groups.Add(row[0], rules = []);
            // kr-1256 sub_D34BC0 samples [0,10000); unused weight deliberately produces no item.
            if (rules.Sum(rule => rule.Permyriad) + weight > 10000)
                throw new InvalidDataException($"{path}: group '{row[0]}' exceeds 10000 permyriad.");
            rules.Add(new(weight, main, bonus, count));
        }
        return groups.ToDictionary(pair => pair.Key,
            pair => (IReadOnlyList<FinalZoneSpawnRule>)Array.AsReadOnly(pair.Value.ToArray()), StringComparer.Ordinal);
    }

    private static IReadOnlyList<FinalZoneItemCandidate> Candidates(string value, FinalZoneItemCatalog items,
        FinalZoneDataLimits limits, string path)
    {
        if (value.Length == 0) return [];
        var tokens = value.Split(',');
        if (tokens.Length > limits.MaximumAlternatives) throw new InvalidDataException($"{path}: too many item alternatives.");
        var candidates = new List<FinalZoneItemCandidate>();
        foreach (var raw in tokens)
        {
            var token = raw.Trim();
            var bracket = token.IndexOf('[');
            var name = bracket < 0 ? token : token[..bracket].Trim();
            var range = new FinalZoneAmountRange(1, 1);
            if (bracket >= 0)
            {
                if (!token.EndsWith(']')) throw new InvalidDataException($"{path}: malformed candidate '{token}'.");
                range = Range(token[(bracket + 1)..^1], 1, limits.MaximumQuantity, path);
            }
            candidates.Add(new(items.Resolve(name), range));
        }
        return Array.AsReadOnly(candidates.ToArray());
    }

    internal static FinalZoneAmountRange Range(string value, int minimum, int maximum, string path)
    {
        var parts = value.Split('~');
        if (parts.Length is < 1 or > 2) throw new InvalidDataException($"{path}: invalid amount range '{value}'.");
        var first = Integer(parts[0], minimum, maximum, path);
        var last = parts.Length == 1 ? first : Integer(parts[1], first, maximum, path);
        return new(first, last);
    }

    private static int Integer(string value, int minimum, int maximum, string path) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number >= minimum && number <= maximum
            ? number : throw new InvalidDataException($"{path}: invalid bounded integer '{value}'.");

    private static float Coordinate(string value, string path) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && float.IsFinite(number)
            ? number : throw new InvalidDataException($"{path}: invalid finite coordinate '{value}'.");

    internal static IEnumerable<string[]> Rows(IClientDataStore store, string path, FinalZoneDataLimits limits, params string[] columns)
    {
        var bytes = FinalZoneItemCatalog.ReadBounded(store, path, limits);
        using var reader = new TextFieldParser(new MemoryStream(bytes), Encoding.Unicode, detectEncoding: true)
        {
            TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = true,
        };
        reader.SetDelimiters("\t");
        var header = reader.ReadFields() ?? throw new InvalidDataException($"{path}: missing header.");
        var indexes = columns.Select(column =>
        {
            var matches = Enumerable.Range(0, header.Length).Where(index => header[index] == column).ToArray();
            return matches.Length == 1 ? matches[0] : throw new InvalidDataException($"{path}: expected one '{column}' column.");
        }).ToArray();
        var rows = 0;
        while (!reader.EndOfData)
        {
            if (++rows > limits.MaximumRows) throw new InvalidDataException($"{path}: too many resource rows.");
            var fields = reader.ReadFields() ?? throw new InvalidDataException($"{path}: missing row.");
            if (indexes.Any(index => index >= fields.Length)) throw new InvalidDataException($"{path}: short row.");
            yield return indexes.Select(index => fields[index]).ToArray();
        }
    }
}
