using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace BubbleFighter.Fantasy.Diagnostics;

/// <summary>Indexes research names exclusively for packet log presentation, independently of protocol routing.</summary>
public sealed class PacketCatalogNames
{
    private readonly Dictionary<(string Profile, string Connection, PacketDirection Direction, ushort Value), string> _names = [];
    private PacketCatalogNames() { }

    /// <summary>Loads bounded catalogs from profile folders; absent folders mean no diagnostic hints.</summary>
    /// <param name="directory">Profile root, or an empty path to disable hints.</param>
    /// <param name="maxEntries">Maximum total input rows.</param>
    /// <param name="maxFileBytes">Maximum bytes per input file.</param>
    /// <returns>An immutable diagnostic lookup that registers no codecs.</returns>
    public static PacketCatalogNames Load(string directory, int maxEntries, int maxFileBytes)
    {
        var result = new PacketCatalogNames();
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return result;
        var reader = new DeserializerBuilder().WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties().Build();
        var total = 0;
        foreach (var folder in Directory.EnumerateDirectories(directory))
        {
            var path = Path.Combine(folder, "opcode-catalog.yaml");
            if (!File.Exists(path)) continue;
            if (new FileInfo(path).Length > maxFileBytes) throw new ConfigException($"Diagnostic catalog exceeds its byte bound: {path}");
            Catalog document;
            try { document = reader.Deserialize<Catalog>(File.ReadAllText(path)); }
            catch (YamlDotNet.Core.YamlException error) { throw new ConfigException($"Invalid diagnostic catalog '{path}': {error.Message}"); }
            if (document is null || document.Profile != Path.GetFileName(folder))
                throw new ConfigException($"Diagnostic catalog profile does not match its folder: {path}");
            total = checked(total + document.Opcodes.Count);
            if (total > maxEntries) throw new ConfigException("Diagnostic catalogs exceed the configured row bound.");
            var choices = new Dictionary<(string Connection, PacketDirection Direction, ushort Value), List<(string Name, int Grade)>>();
            foreach (var row in document.Opcodes)
            {
                var grade = Grade(row.Grade);
                if (grade == 0 || row.Name.StartsWith("Unknown", StringComparison.Ordinal) || row.Name.Length == 0) continue;
                if (!ushort.TryParse(row.Value, out var value)) throw new ConfigException($"Invalid diagnostic opcode in '{path}': {row.Value}");
                var direction = row.Direction switch { "c2s" => PacketDirection.ClientToServer, "s2c" => PacketDirection.ServerToClient,
                    _ => throw new ConfigException($"Invalid diagnostic direction in '{path}': {row.Direction}") };
                var connection = row.Connection;
                if (connection.Length == 0)
                {
                    if (row.Name.StartsWith("rcp", StringComparison.Ordinal) || row.Name.StartsWith("rsp", StringComparison.Ordinal)) continue;
                    if (document.Profile == "kr-1256" && (value >= 2000 || row.Name.StartsWith("gpp", StringComparison.Ordinal))) continue;
                    connection = "game";
                }
                if (connection is not ("game" or "relay" or "both")) throw new ConfigException($"Invalid diagnostic connection in '{path}': {connection}");
                foreach (var scope in connection == "both" ? new[] { "game", "relay" } : [connection])
                {
                    var key = (scope, direction, value);
                    if (!choices.TryGetValue(key, out var list)) choices[key] = list = [];
                    list.Add((row.Name.TrimEnd('?'), grade));
                }
            }
            foreach (var (key, choicesForKey) in choices)
            {
                var bestGrade = choicesForKey.Max(item => item.Grade);
                var bestNames = choicesForKey.Where(item => item.Grade == bestGrade).Select(item => item.Name).Distinct().ToArray();
                if (bestNames.Length == 1) result._names[(document.Profile, key.Connection, key.Direction, key.Value)] = bestNames[0];
            }
        }
        return result;
    }

    /// <summary>Uses the runtime name first and appends a question mark to a research fallback without changing the runtime table.</summary>
    /// <param name="protocol">Profile and connection joined by a slash.</param>
    /// <param name="direction">Packet direction.</param>
    /// <param name="value">Numeric opcode.</param>
    /// <param name="table">Runtime connection view, when available.</param>
    /// <returns>A display label for logging only.</returns>
    public string Describe(string protocol, PacketDirection direction, ushort value, IOpcodeTable? table)
    {
        var runtime = table?.Describe(direction, value) ?? $"Unknown({value})";
        if (table is not null && table.TryGet(direction, value, out var entry) && entry is not null
            && !entry.Name.StartsWith("Unknown", StringComparison.Ordinal)) return runtime;
        var slash = protocol.IndexOf('/');
        return slash > 0 && _names.TryGetValue((protocol[..slash], protocol[(slash + 1)..], direction, value), out var name)
            ? $"{name}?({value})" : runtime;
    }

    private static int Grade(string grade) => grade switch
    {
        "reviewed" or "runtime" => 10, "label" => 9, "strong" => 8, "cross" => 7,
        "shape" => 6, "shape-single" => 5, "candidate" => 4, "ordered" => 3, "weak" => 2, "pair" => 1, _ => 0,
    };

    private sealed class Catalog
    {
        public string Profile { get; set; } = "";
        public List<Row> Opcodes { get; set; } = [];
    }

    private sealed class Row
    {
        public string Value { get; set; } = "";
        public string Direction { get; set; } = "";
        public string Name { get; set; } = "";
        public string Grade { get; set; } = "";
        public string Connection { get; set; } = "";
    }
}
