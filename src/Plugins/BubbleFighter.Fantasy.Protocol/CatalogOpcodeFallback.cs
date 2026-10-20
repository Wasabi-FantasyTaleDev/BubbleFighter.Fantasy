using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace BubbleFighter.Fantasy.Protocol;

internal sealed class CatalogOpcodeFallback(IOpcodeTable runtime, ILogger logger, string path)
{
    private readonly Dictionary<(OpcodeConnection, PacketDirection), List<Row>> _rows = [];

    internal static CatalogOpcodeFallback? Load(ProtocolOptions options, IOpcodeTable runtime, ILogger logger)
    {
        var path = options.OpcodeCatalogPath.Length > 0 ? options.OpcodeCatalogPath
            : Path.Combine(Path.GetDirectoryName(options.OpcodeTablePath) ?? "", "opcode-catalog.yaml");
        if (!File.Exists(path))
        {
            if (options.OpcodeCatalogPath.Length > 0) throw new ConfigException($"Configured opcode catalog is missing: {path}");
            return null;
        }
        if (new FileInfo(path).Length > options.MaxCatalogFileBytes) throw new ConfigException($"Opcode catalog exceeds its byte bound: {path}");
        Catalog document;
        try
        {
            document = new DeserializerBuilder().WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties().Build().Deserialize<Catalog>(File.ReadAllText(path));
        }
        catch (YamlDotNet.Core.YamlException error) { throw new ConfigException($"Invalid opcode catalog '{path}': {error.Message}"); }
        if (document is null || document.Profile != runtime.Profile.Id) throw new ConfigException($"Opcode catalog profile mismatch: {path}");
        if (document.Opcodes.Count > options.MaxCatalogRows) throw new ConfigException($"Opcode catalog exceeds its row bound: {path}");
        var result = new CatalogOpcodeFallback(runtime, logger, path);
        foreach (var row in document.Opcodes)
        {
            if (Grade(row.Grade) == 0 || row.Name.Length == 0 || row.Name.StartsWith("Unknown", StringComparison.Ordinal)) continue;
            var direction = row.Direction switch { "c2s" => PacketDirection.ClientToServer, "s2c" => PacketDirection.ServerToClient,
                _ => throw new ConfigException($"Invalid catalog direction in '{path}': {row.Direction}") };
            if (!ushort.TryParse(row.Value, out _)) throw new ConfigException($"Invalid catalog value in '{path}': {row.Value}");
            var connection = row.Connection;
            if (connection.Length == 0)
            {
                if (row.Name.StartsWith("rcp", StringComparison.Ordinal) || row.Name.StartsWith("rsp", StringComparison.Ordinal)) continue;
                if (document.Profile == "kr-1256" && (ushort.Parse(row.Value) >= 2000 || row.Name.StartsWith("gpp", StringComparison.Ordinal))) continue;
                connection = "game";
            }
            var scopes = connection switch { "game" => new[] { OpcodeConnection.Game }, "relay" => [OpcodeConnection.Relay],
                "both" => [OpcodeConnection.Game, OpcodeConnection.Relay], _ => throw new ConfigException($"Invalid catalog connection in '{path}': {connection}") };
            foreach (var scope in scopes)
            {
                var key = (scope, direction);
                if (!result._rows.TryGetValue(key, out var rows)) result._rows[key] = rows = [];
                rows.Add(row);
            }
        }
        return result;
    }

    internal ushort? Resolve(OpcodeConnection connection, PacketDirection direction, string name)
    {
        if (!_rows.TryGetValue((connection, direction), out var rows)) return null;
        var matches = rows.Where(row => row.Name == name).ToArray();
        if (matches.Length == 0) return null;
        var values = matches.Select(row => ushort.Parse(row.Value)).Distinct().ToArray();
        if (values.Length != 1) throw new ConfigException($"Catalog fallback for {runtime.Profile.Id}/{connection} {direction} {name} is ambiguous: {string.Join(',', values)}.");
        var value = values[0];
        var peers = rows.Where(row => ushort.Parse(row.Value) == value).ToArray();
        var bestGrade = peers.Max(row => Grade(row.Grade));
        var bestNames = peers.Where(row => Grade(row.Grade) == bestGrade).Select(row => row.Name).Distinct().ToArray();
        if (bestNames.Length != 1 || bestNames[0] != name)
            throw new ConfigException($"Catalog fallback for {runtime.Profile.Id}/{connection} {direction} {name} conflicts with another name at {value}.");
        var view = runtime.ForConnection(connection);
        if (view.TryGet(direction, value, out var occupied) && occupied is not null && !occupied.Name.StartsWith("Unknown", StringComparison.Ordinal))
            throw new ConfigException($"Catalog fallback {name}={value} conflicts with runtime {occupied.Name} on {runtime.Profile.Id}/{connection}.");
        var selected = matches.MaxBy(row => Grade(row.Grade))!;
        logger.LogWarning("Opcode catalog candidate binding: profile={Profile} connection={Connection} direction={Direction} requested={Requested} candidate={Candidate}? opcode={Opcode} grade={Grade} name_source={NameSource} method={Method} source={Source} note={Note}; runtime name missing, existing codec only; unreviewed, opcodes.yaml unchanged.",
            runtime.Profile.Id, connection, direction, name, selected.Name, value, selected.Grade, selected.NameSource, selected.Method, path, selected.Note);
        return value;
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
        public string Connection { get; set; } = "";
        public string Name { get; set; } = "";
        public string Grade { get; set; } = "";
        public string NameSource { get; set; } = "";
        public string Method { get; set; } = "";
        public string Note { get; set; } = "";
    }
}
