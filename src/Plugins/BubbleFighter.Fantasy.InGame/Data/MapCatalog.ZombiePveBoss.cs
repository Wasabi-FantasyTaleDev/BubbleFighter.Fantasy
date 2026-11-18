using System.Globalization;
using System.Text;
using BubbleFighter.Fantasy.Contracts.ClientData;

namespace BubbleFighter.Fantasy.InGame.Data;

internal sealed partial class MapCatalog
{
    private readonly Dictionary<string, ZombiePveBoss> _zombiePveBosses = new(StringComparer.OrdinalIgnoreCase);

    internal ZombiePveBoss? ZombiePveBossFor(string map) => _zombiePveBosses.GetValueOrDefault(map);

    private void IndexZombiePveBoss(IClientDataStore store, string ini, string key)
    {
        // The INI keys and the Zombie_Boss resource name are ASCII even where comments use a regional code page.
        if (ReadZombiePveBoss(Encoding.Latin1.GetString(store.Read(ini, 256 * 1024))) is { } boss)
            _zombiePveBosses.Add(key, boss);
    }

    internal static ZombiePveBoss? ReadZombiePveBoss(string text)
    {
        var properties = new Dictionary<int, string>();
        var starts = new List<(int Property, int At)>();
        var section = "";
        var count = 0;
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } raw)
        {
            var line = raw.Split(';', 2)[0].Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line; continue; }
            var equals = line.IndexOf('=');
            if (equals < 1) continue;
            var key = line[..equals].Trim();
            var value = line[(equals + 1)..].Trim();
            if (section == "[CHR_PROPERTY]" && key == "CHR_PROPERTY_COUNT")
                count = int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
            else if (section == "[CHR_PROPERTY]" && Indexed(key, "CHR_PROPERTY_", out var index))
                properties.Add(index, value);
            else if (section == "[DIFFICULTY_0]" && Indexed(key, "CHR_SPAWNER_", out _)
                && Tag(value, "NameID", out var property) && Tag(value, "SartTime", out var at) && at >= 0)
                starts.Add((property, at));
        }
        // kr-1256 sub_A4E980 appends nonempty properties in numbered order; do not assume a sparse index matches the wire.
        if (count < 1 || count != properties.Count || properties.Keys.Any(key => key < 0 || key >= count)) return null;
        var bosses = properties.Where(row => row.Value.Contains("[Name:Zombie_Boss]", StringComparison.Ordinal)).ToArray();
        if (bosses.Length != 1) return null;
        var earliest = starts.Where(row => row.Property == bosses[0].Key).Select(row => (int?)row.At).Min();
        return earliest is { } start ? new(bosses[0].Key, start) : null;

        static bool Indexed(string key, string prefix, out int value)
        {
            value = -1;
            return key.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(key.AsSpan(prefix.Length),
                NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }
        static bool Tag(string row, string name, out int value)
        {
            value = -1;
            var pos = row.IndexOf("[" + name + ":", StringComparison.Ordinal);
            if (pos < 0) return false;
            var start = pos + name.Length + 2;
            var end = row.IndexOf(']', start);
            return end > start && int.TryParse(row.AsSpan(start, end - start), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out value);
        }
    }
}

internal sealed record ZombiePveBoss(int Property, int EarliestMapTimeMs);
